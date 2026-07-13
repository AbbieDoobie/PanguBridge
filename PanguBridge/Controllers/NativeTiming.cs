using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PanguBridge.Controllers;

/// <summary>Win32 interop for precise timing loops - raising the system timer resolution to
/// 1ms plus a high-resolution waitable timer gets sleep precision down to ~1ms without CPU
/// burn, versus the ~15ms floor a plain Thread.Sleep has at default timer resolution. Shared
/// between HidMaestroOutput.SubmitThreadProc (submitting gamepad state to the game) and
/// PanguEngine.RumbleGateLoop (sampling the physical trigger for Adaptive Trigger Simulation)
/// via RunPrecisionLoop below.</summary>
internal static class NativeTiming
{
    public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x00000002;
    public const uint TIMER_ALL_ACCESS = 0x1F0003;
    public const uint WAIT_OBJECT_0    = 0x00000000;
    public const uint INFINITE         = 0xFFFFFFFF;

    [DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", ExactSpelling = true)]
    public static extern uint timeEndPeriod(uint uMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWaitableTimerExW(
        IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWaitableTimer(
        IntPtr hTimer, ref long pDueTime, int lPeriod,
        IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForMultipleObjects(
        uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Runs `tick` repeatedly on an absolute schedule (nextTick += interval, not
    /// "sleep after work completes" - the latter accumulates drift equal to however long each
    /// tick's work took; if a tick overruns into the next deadline, e.g. after a GC pause, this
    /// resyncs to now instead of bursting to catch up). Sleeps the bulk of each interval on a
    /// high-resolution waitable timer for ~1ms precision without CPU burn, then spin-waits the
    /// last sub-millisecond to close out timer overshoot (or the whole remaining wait, on
    /// pre-1803 Windows where the HR timer isn't available - falls back to
    /// token.WaitHandle.WaitOne, still tightened to ~1ms by timeBeginPeriod(1)).
    /// intervalTicksProvider (in Stopwatch ticks, not ms, for full precision at high rates) is
    /// re-read every tick so a live rate change takes effect on the very next tick, no thread
    /// restart. shouldContinue, if given, is checked before each tick's work and lets the loop
    /// exit without waiting out the remainder of the current interval - for callers whose
    /// underlying resource (a device session, a control object) can go away mid-loop. Cancelling
    /// token wakes a blocked wait immediately via CancellationToken.WaitHandle, waited on
    /// alongside the timer handle - no separate stop-signal plumbing needed at the call site.
    /// </summary>
    public static void RunPrecisionLoop(
        CancellationToken token, Func<long> intervalTicksProvider, Action tick, Func<bool>? shouldContinue = null)
    {
        bool periodRaised = false;
        IntPtr timerHandle = IntPtr.Zero;
        try
        {
            periodRaised = timeBeginPeriod(1) == 0;
            timerHandle = CreateWaitableTimerExW(
                IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);

            IntPtr[]? waitHandles = timerHandle != IntPtr.Zero
                ? new[] { token.WaitHandle.SafeWaitHandle.DangerousGetHandle(), timerHandle }
                : null;

            long freq = Stopwatch.Frequency;
            long nextTick = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                if (shouldContinue != null && !shouldContinue()) break;

                tick();

                nextTick += intervalTicksProvider();
                long now = Stopwatch.GetTimestamp();
                if (now >= nextTick) { nextTick = now; continue; }

                double remainingMs = (nextTick - now) * 1000.0 / freq;

                if (waitHandles != null && remainingMs > 1.5)
                {
                    long dueTime100ns = -(long)((remainingMs - 1.0) * 10_000); // negative = relative
                    if (SetWaitableTimer(timerHandle, ref dueTime100ns, 0, IntPtr.Zero, IntPtr.Zero, false))
                    {
                        uint waitResult = WaitForMultipleObjects(
                            (uint)waitHandles.Length, waitHandles, false, INFINITE);
                        if (waitResult == WAIT_OBJECT_0) return; // token canceled (index 0)
                    }
                }
                else if (remainingMs > 1.5)
                {
                    // No high-resolution timer available (pre-1803 Windows) - fall back to
                    // token.WaitHandle, still tightened to ~1ms by timeBeginPeriod(1) above.
                    if (token.WaitHandle.WaitOne((int)(remainingMs - 1.0))) return;
                }

                // Final close-out: spin the last sub-millisecond to cover both HR-timer
                // overshoot and (when no HR timer exists) the whole remaining wait.
                while (Stopwatch.GetTimestamp() < nextTick)
                {
                    if (token.IsCancellationRequested) return;
                    Thread.SpinWait(50);
                }
            }
        }
        finally
        {
            if (timerHandle != IntPtr.Zero) CloseHandle(timerHandle);
            if (periodRaised) timeEndPeriod(1);
        }
    }
}
