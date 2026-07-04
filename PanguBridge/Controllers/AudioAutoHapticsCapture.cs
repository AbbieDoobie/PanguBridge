using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace PanguBridge.Controllers;

/// <summary>
/// Derives a rumble signal from whatever audio the PC is currently playing - not literal
/// per-game haptic-feedback content (no virtual controller can receive that at all; see
/// docs/rumble.md), but a bass + transient heuristic approximation for games that send no
/// rumble of their own. The DSP chain (low-pass filter -> envelope follower -> soft clip) is
/// ported from loteran/DS5Dongle's "Audio Auto Haptics" feature (MIT licensed) - see
/// THIRD-PARTY-NOTICES.md.
/// </summary>
public sealed class AudioAutoHapticsCapture : IDisposable, IMMNotificationClient
{
    /// <summary>AppSettings.AudioAutoHapticsDeviceId sentinel selecting the Default
    /// Communication Device (Role.Communications) instead of the Default Device
    /// (Role.Multimedia, selected by null/empty).</summary>
    public const string CommunicationsDeviceSelector = "communications";

    // mmreg.h speaker-position bits for WAVEFORMATEXTENSIBLE's channel mask. Channels are packed
    // into the interleaved buffer in ascending bit-position order among the mask's set bits, so
    // a given channel's index is however many set bits precede its own.
    private const int SpeakerFrontLeft          = 0x1;
    private const int SpeakerFrontRight         = 0x2;
    private const int SpeakerFrontCenter        = 0x4;
    private const int SpeakerLowFrequency       = 0x8;
    private const int SpeakerBackLeft           = 0x10;
    private const int SpeakerBackRight          = 0x20;
    private const int SpeakerFrontLeftOfCenter  = 0x40;
    private const int SpeakerFrontRightOfCenter = 0x80;
    private const int SpeakerBackCenter         = 0x100;
    private const int SpeakerSideLeft           = 0x200;
    private const int SpeakerSideRight          = 0x400;
    private const int SpeakerTopCenter          = 0x800;
    private const int SpeakerTopFrontLeft       = 0x1000;
    private const int SpeakerTopFrontCenter     = 0x2000;
    private const int SpeakerTopFrontRight      = 0x4000;
    private const int SpeakerTopBackLeft        = 0x8000;
    private const int SpeakerTopBackCenter      = 0x10000;
    private const int SpeakerTopBackRight       = 0x20000;

    // Every "this speaker lives on the left/right side of the room" bit, regardless of whether
    // it's a front, rear, side, or height position - each one folds into that side's motor
    // chain, not just the front pair. Center-ish positions (front/back/top center) are their own
    // bucket, folded equally into both sides when IncludeCenter is on.
    private const int LeftSpeakerMask =
        SpeakerFrontLeft | SpeakerBackLeft | SpeakerFrontLeftOfCenter | SpeakerSideLeft |
        SpeakerTopFrontLeft | SpeakerTopBackLeft;
    private const int RightSpeakerMask =
        SpeakerFrontRight | SpeakerBackRight | SpeakerFrontRightOfCenter | SpeakerSideRight |
        SpeakerTopFrontRight | SpeakerTopBackRight;
    private const int CenterSpeakerMask =
        SpeakerFrontCenter | SpeakerBackCenter | SpeakerTopCenter | SpeakerTopFrontCenter |
        SpeakerTopBackCenter;

    private readonly MMDeviceEnumerator _enumerator = new();
    private WasapiLoopbackCapture? _capture;
    private string? _deviceSelector;
    private bool _notificationRegistered;
    private bool _disposed;

    /// <summary>Only bass below this frequency drives vibration.</summary>
    public double CutoffHz { get; set; } = 160.0;

    /// <summary>How quickly vibration ramps up when a sudden sound hits.</summary>
    public double AttackMs { get; set; } = 1.0;

    /// <summary>How long vibration takes to fade after a sound ends.</summary>
    public double ReleaseMs { get; set; } = 80.0;

    /// <summary>How strongly loud moments are emphasized over quiet ones.</summary>
    public double IntensityBoost { get; set; } = 3.0;

    /// <summary>When true (default), a subwoofer/LFE channel - if the capture format declares
    /// one - is folded into both Left/Right chains. When false, LFE is ignored entirely even
    /// if present, using only the front left/right channels.</summary>
    public bool IncludeLfe { get; set; } = true;

    /// <summary>When true (default), a dedicated center channel - if the capture format
    /// declares one - is folded into both Left/Right chains. When false, the center channel is
    /// ignored entirely even if present, using only the other channels.</summary>
    public bool IncludeCenter { get; set; } = true;

    // Per-channel low-pass/envelope-follower memory, carried across DataAvailable calls and
    // reset whenever capture (re)starts against a new device/format.
    private float _lpLeft, _lpRight;
    private float _envLeft, _envRight;

    /// <summary>Latest derived intensity (0-255), read continuously by PanguEngine's rumble
    /// gate loop. Plain volatile fields are sufficient here - there's no multi-field invariant
    /// to protect, just two independent bytes updated from the capture thread.</summary>
    public volatile byte LeftIntensity;
    public volatile byte RightIntensity;

    /// <summary>All render (output) devices selectable in the Options UI - the two pseudo
    /// entries first (Id null = Default Device, Id CommunicationsDeviceSelector = Default
    /// Communication Device), then every currently-enumerated speaker/headphone output by
    /// name, for pinning to one specific device regardless of what Windows currently calls
    /// default.</summary>
    public IReadOnlyList<AudioDeviceOption> EnumerateOutputDevices()
    {
        var list = new List<AudioDeviceOption>
        {
            new(null, "Default Device"),
            new(CommunicationsDeviceSelector, "Default Communication Device"),
        };

        try
        {
            foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
        }
        catch
        {
            // Best-effort - the two pseudo entries above still let capture work even if
            // enumeration itself fails for some reason.
        }

        return list;
    }

    /// <summary>Starts (or restarts, if already running) loopback capture against the device
    /// the selector resolves to - see AppSettings.AudioAutoHapticsDeviceId for what the
    /// selector values mean. Safe to call again with a new selector while already running.</summary>
    public void Start(string? deviceSelector)
    {
        _deviceSelector = deviceSelector;

        if (!_notificationRegistered)
        {
            _enumerator.RegisterEndpointNotificationCallback(this);
            _notificationRegistered = true;
        }

        RestartCapture();
    }

    public void Stop()
    {
        if (_notificationRegistered)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); }
            catch { /* best-effort */ }
            _notificationRegistered = false;
        }

        StopCaptureOnly();
    }

    private void RestartCapture()
    {
        StopCaptureOnly();

        MMDevice? device = ResolveDevice(_deviceSelector);
        if (device is null) return;

        _lpLeft = _lpRight = 0f;
        _envLeft = _envRight = 0f;

        var capture = new WasapiLoopbackCapture(device);
        capture.DataAvailable += OnDataAvailable;
        _capture = capture;
        capture.StartRecording();
    }

    private void StopCaptureOnly()
    {
        if (_capture is null) return;

        try
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.StopRecording();
            _capture.Dispose();
        }
        catch
        {
            // Best-effort teardown - matches HidMaestroOutput/HidReader's own stop paths.
        }

        _capture = null;
        LeftIntensity = 0;
        RightIntensity = 0;
    }

    private MMDevice? ResolveDevice(string? selector)
    {
        try
        {
            if (string.IsNullOrEmpty(selector))
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (selector == CommunicationsDeviceSelector)
                return _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
            return _enumerator.GetDevice(selector);
        }
        catch
        {
            // Device unplugged/no default configured/pinned ID no longer exists - leave
            // capture stopped rather than throw; the next default-device-changed notification
            // or manual re-selection will recover it.
            return null;
        }
    }

    // WASAPI shared-mode loopback always delivers the endpoint's own mix format - commonly
    // 32-bit IEEE float, 2+ channels. Every channel the format's speaker mask designates as a
    // left-side position (front, back, side, height - doesn't matter which) folds into the Left
    // chain, and the mirrored right-side positions fold into Right, so the physical left
    // trigger/grip motor buzzes from left-side audio content and the right motor from
    // right-side content even on 5.1/7.1. Center/LFE (if present) fold into both chains equally
    // instead, since neither is meaningfully directional. A channel the mask doesn't recognize,
    // or a format with no mask at all beyond 2 channels, falls back to folding into both chains
    // equally rather than being silently dropped.
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (sender is not WasapiCapture capture) return;
        WaveFormat format = capture.WaveFormat;

        int channels = format.Channels;
        if (channels < 1 || format.BitsPerSample != 32) return; // only IEEE float mix formats are handled

        // Classified once per buffer (not per frame) into which motor chain(s) each channel
        // index feeds - see ClassifyChannels below. Backing storage is stackalloc'd here (not
        // inside ClassifyChannels) since a Span's stackalloc'd memory can't escape the method
        // that allocated it - passing the spans in by value and filling them works fine.
        Span<int> leftIdx = stackalloc int[channels];
        Span<int> rightIdx = stackalloc int[channels];
        Span<int> centerIdx = stackalloc int[channels];
        Span<int> otherIdx = stackalloc int[channels];
        ClassifyChannels(format, channels, leftIdx, out int leftCount, rightIdx, out int rightCount,
            centerIdx, out int centerCount, otherIdx, out int otherCount, out int lfeIndex);

        int frameSize = 4 * channels;
        int frames = e.BytesRecorded / frameSize;
        if (frames == 0) return;

        float lpA    = 1f - MathF.Exp(-2f * MathF.PI * (float)CutoffHz / format.SampleRate);
        float envAtk = 1f - MathF.Exp(-1f / ((float)AttackMs  / 1000f * format.SampleRate));
        float envRel = 1f - MathF.Exp(-1f / ((float)ReleaseMs / 1000f * format.SampleRate));
        float depth  = (float)IntensityBoost;

        float lpL = _lpLeft, lpR = _lpRight, envL = _envLeft, envR = _envRight;
        float peakL = 0f, peakR = 0f;

        var samples = MemoryMarshal.Cast<byte, float>(e.Buffer.AsSpan(0, frames * frameSize));

        for (int i = 0; i < frames; i++)
        {
            int baseIdx = i * channels;

            float leftSum = 0f;
            for (int k = 0; k < leftCount; k++) leftSum += samples[baseIdx + leftIdx[k]];
            float rightSum = 0f;
            for (int k = 0; k < rightCount; k++) rightSum += samples[baseIdx + rightIdx[k]];
            float otherSum = 0f;
            for (int k = 0; k < otherCount; k++) otherSum += samples[baseIdx + otherIdx[k]];

            float center = 0f;
            if (IncludeCenter) for (int k = 0; k < centerCount; k++) center += samples[baseIdx + centerIdx[k]];
            float lfe = IncludeLfe && lfeIndex >= 0 ? samples[baseIdx + lfeIndex] : 0f;

            lpL += lpA * (leftSum + otherSum + lfe + center - lpL);
            lpR += lpA * (rightSum + otherSum + lfe + center - lpR);

            float absL = MathF.Abs(lpL);
            float absR = MathF.Abs(lpR);
            envL = absL > envL ? envL + envAtk * (absL - envL) : envL + envRel * (absL - envL);
            envR = absR > envR ? envR + envAtk * (absR - envR) : envR + envRel * (absR - envR);

            float sigL = lpL * (1f + depth * envL);
            float sigR = lpR * (1f + depth * envR);
            sigL /= 1f + MathF.Abs(sigL); // cheap tanh-style soft clip
            sigR /= 1f + MathF.Abs(sigR);

            float absSigL = MathF.Abs(sigL);
            float absSigR = MathF.Abs(sigR);
            if (absSigL > peakL) peakL = absSigL;
            if (absSigR > peakR) peakR = absSigR;
        }

        _lpLeft = lpL; _lpRight = lpR; _envLeft = envL; _envRight = envR;

        // Unlike earlier versions of this feature, no noise gate is applied here - the same
        // Left/Right intensity feeds several different motor groups (grip, trigger-pulled,
        // trigger-idle) each with their own configurable floor, so gating happens downstream in
        // PanguEngine.ApplyRumbleOutput once it's known which group a given sample is driving.
        LeftIntensity  = (byte)Math.Clamp(peakL * 255f, 0f, 255f);
        RightIntensity = (byte)Math.Clamp(peakR * 255f, 0f, 255f);
    }

    // WaveFormatExtensible.dwChannelMask isn't publicly exposed by NAudio - read it directly
    // from the marshaled WAVEFORMATEXTENSIBLE struct instead. Layout per the Windows SDK's
    // mmreg.h: WAVEFORMATEX (18 bytes) + a 2-byte union (wValidBitsPerSample etc.), so
    // dwChannelMask sits at a fixed byte offset of 20.
    private static int GetChannelMask(WaveFormat format)
    {
        IntPtr ptr = WaveFormat.MarshalToPtr(format);
        try { return Marshal.ReadInt32(ptr, 20); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    // Sorts every channel index into which motor chain(s) it should feed. Without a channel mask
    // to go by (plain stereo, or an unusual >2 channel format with no mask), falls back to
    // conventional Left/Right channel order with no Center/LFE/other distinction. With a mask,
    // walks its bits in ascending order (a channel's index is simply how many set bits precede
    // its own) and buckets each one by LeftSpeakerMask/RightSpeakerMask/CenterSpeakerMask/LFE -
    // anything else lands in "other", folded into both chains the same as Center/LFE.
    private static void ClassifyChannels(WaveFormat format, int channels,
        Span<int> leftIdx, out int leftCount,
        Span<int> rightIdx, out int rightCount,
        Span<int> centerIdx, out int centerCount,
        Span<int> otherIdx, out int otherCount,
        out int lfeIndex)
    {
        leftCount = rightCount = centerCount = otherCount = 0;
        lfeIndex = -1;

        if (channels <= 2 || format is not WaveFormatExtensible)
        {
            leftIdx[leftCount++] = 0;
            if (channels > 1) rightIdx[rightCount++] = 1;
            for (int ch = 2; ch < channels; ch++) otherIdx[otherCount++] = ch;
            return;
        }

        int channelMask = GetChannelMask(format);
        int idx = 0;
        for (int bit = 0; bit < 32 && idx < channels; bit++)
        {
            int bitValue = 1 << bit;
            if ((channelMask & bitValue) == 0) continue;

            if (bitValue == SpeakerLowFrequency) lfeIndex = idx;
            else if ((bitValue & LeftSpeakerMask) != 0) leftIdx[leftCount++] = idx;
            else if ((bitValue & RightSpeakerMask) != 0) rightIdx[rightCount++] = idx;
            else if ((bitValue & CenterSpeakerMask) != 0) centerIdx[centerCount++] = idx;
            else otherIdx[otherCount++] = idx;

            idx++;
        }

        // The mask had fewer set bits than the format's actual channel count (shouldn't happen
        // in practice, but don't silently drop trailing channels if it does) - fold them into
        // both chains equally, same as any other unrecognized channel.
        for (; idx < channels; idx++) otherIdx[otherCount++] = idx;
    }

    // IMMNotificationClient - only default-device-role changes are acted on, so capture keeps
    // following whichever pseudo-device (Default Device / Default Communication Device) is
    // currently selected. Device-added/removed intentionally aren't tracked here - the Options
    // UI's device list only refreshes via its manual refresh button, by design.
    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow != DataFlow.Render) return;

        bool tracksDefault = string.IsNullOrEmpty(_deviceSelector) && role == Role.Multimedia;
        bool tracksCommunications = _deviceSelector == CommunicationsDeviceSelector && role == Role.Communications;
        if (tracksDefault || tracksCommunications)
            RestartCapture();
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string deviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string deviceId, PropertyKey key) { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _enumerator.Dispose();
    }
}

/// <summary>One entry in the Options UI's Audio Device dropdown. Id matches
/// AppSettings.AudioAutoHapticsDeviceId's format - null for Default Device,
/// AudioAutoHapticsCapture.CommunicationsDeviceSelector for Default Communication Device, or a
/// specific MMDevice ID.</summary>
public sealed record AudioDeviceOption(string? Id, string Name);
