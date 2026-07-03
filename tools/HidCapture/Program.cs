using HidSharp;

const int VendorId = 0x20BC;
const int ProductId = 0x5162;

var candidates = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
var device = candidates.FirstOrDefault(d => d.DevicePath.Contains("MI_04", StringComparison.OrdinalIgnoreCase))
             ?? candidates.FirstOrDefault(d => TryGetUsagePage(d, out int usagePage) && usagePage == 0xFF80);

static bool TryGetUsagePage(HidDevice device, out int usagePage)
{
    try
    {
        var descriptor = device.GetReportDescriptor();
        foreach (var item in descriptor.DeviceItems)
        {
            foreach (var usage in item.Usages.GetAllValues())
            {
                usagePage = (int)(usage >> 16);
                return true;
            }
        }
    }
    catch
    {
        // fall through
    }

    usagePage = 0;
    return false;
}

if (device is null)
{
    Console.WriteLine("No HID device found for VID 0x20BC / PID 0x5162.");
    Console.WriteLine("Make sure the Pangu's wireless USB receiver is plugged in and Beitong's");
    Console.WriteLine("iControl app is closed (it holds the device exclusively).");
    Console.WriteLine();
    Console.WriteLine("All currently connected HID devices, for comparison:");
    foreach (var d in DeviceList.Local.GetHidDevices())
    {
        TryGetUsagePage(d, out int usagePage);
        Console.WriteLine($"  VID 0x{d.VendorID:X4} / PID 0x{d.ProductID:X4}  usage page 0x{usagePage:X4}  {d.DevicePath}");
    }

    return;
}

using var stream = device.Open();
stream.ReadTimeout = 1000;
stream.WriteTimeout = 2000;

int inLen = device.GetMaxInputReportLength();
int outLen = device.GetMaxOutputReportLength();

string logPath = Path.Combine(AppContext.BaseDirectory, $"capture_{DateTime.Now:yyyyMMdd_HHmmss}.log");
using var log = new StreamWriter(logPath, append: false) { AutoFlush = true };

void Log(string line)
{
    Console.WriteLine(line);
    log.WriteLine(line);
}

Log($"# PanguBridge HID capture - {DateTime.Now:O}");
Log($"# Device path: {device.DevicePath}");
Log($"# MaxInputReportLength={inLen}, MaxOutputReportLength={outLen}");
Log($"# Log file: {logPath}");
Log("#");
Log("# Only changed bytes are printed (idle report is the baseline). Type a short note");
Log("# and press Enter at any time to mark what you're about to do (e.g. \"left stick up\"),");
Log("# then do it - the marker timestamp lines up with the report lines right after it.");
Log("# Type \"toggle\" and press Enter to send the PC/Nintendo Switch mode-switch sequence");
Log("# (flips whichever mode is currently active - check LED color first if unsure).");
Log("# Type \"togglegyro\" and press Enter to send the Mouse/Button gyro-output-mode toggle");
Log("# (a separate setting, under Gyro Settings in iControl). Verify either one actually");
Log("# switched (LED color / iControl UI / gyro-mouse behavior) before trusting it.");
Log("# Ctrl+C to stop.");
Log("#");

// HidStream doesn't document concurrent-write safety (same reasoning as HidReader.cs's
// _writeLock in the main app). The keepalive thread and the on-demand toggle sends call
// stream.Write from different threads, so all writes go through this lock.
var writeLock = new object();

var enable = new byte[outLen];
enable[0] = 0x02;
enable[1] = 0x37;
lock (writeLock) stream.Write(enable);

var cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

var keepaliveThread = new Thread(() =>
{
    bool toggle = true;
    while (!cts.IsCancellationRequested)
    {
        try
        {
            var report = new byte[outLen];
            report[0] = 0x02;
            report[1] = (byte)(toggle ? 0x25 : 0x21);
            lock (writeLock) stream.Write(report);
            toggle = !toggle;
        }
        catch
        {
            return;
        }

        Thread.Sleep(250);
    }
}) { IsBackground = true };
keepaliveThread.Start();

// Sending "02 36" alone does not switch PC/Nintendo Switch mode - the device acks it with a
// distinct "02 36 51 ..." response, but LED color and gyro-as-mouse behavior stay on the
// previous mode until two follow-ups are sent: "02 11 <dir> 14" (same status-query command
// used elsewhere, with byte 2 indicating direction: 0x01 entering NS mode, 0x00 entering PC
// mode) and "02 15". iControl's own 4-part "02 12 00 05 ..." profile/config dump that follows
// a real toggle on the wire is UI resync, not a firmware requirement, and is not replicated
// here. isNsMode starts false (PC) as the tool's assumed initial state - toggle twice to
// resync if the device is actually in NS mode when the tool starts.
bool isNsMode = false;

void SendToggleNsMode()
{
    void Send(params byte[] bytes)
    {
        var report = new byte[outLen];
        Array.Copy(bytes, report, bytes.Length);
        lock (writeLock) stream.Write(report);
    }

    isNsMode = !isNsMode;
    byte followUpParam = (byte)(isNsMode ? 0x01 : 0x00);

    Send(0x02, 0x36);
    Thread.Sleep(15);
    Send(0x02, 0x11, followUpParam, 0x14);
    Thread.Sleep(15);
    Send(0x02, 0x15);
    Log($"==== SENT toggle sequence -> now targeting {(isNsMode ? "NS" : "PC")} mode (02 36 / 02 11 {followUpParam:X2} 14 / 02 15) @ {DateTime.Now:HH:mm:ss.fff} ====");
}

// "02 13 00 00..." toggles gyro output mode between Mouse and Button (a separate setting
// from the PC/Nintendo Switch toggle above, under Gyro Settings in iControl) - same
// no-parameter-toggle shape as "02 36". Sending it from this tool does not actually switch
// the mode; see docs/usb-reverse-engineering.md's "Gyro Output Mode" section. The "02 15"
// follow-up sent here is speculative, not a confirmed requirement.
void SendToggleGyroOutputMode()
{
    var report = new byte[outLen];
    report[0] = 0x02;
    report[1] = 0x13;
    lock (writeLock) stream.Write(report);
    Thread.Sleep(15);
    var followUp = new byte[outLen];
    followUp[0] = 0x02;
    followUp[1] = 0x15;
    lock (writeLock) stream.Write(followUp);
    Log($"==== SENT 02 13 / 02 15 (toggle Mouse/Button gyro output mode) @ {DateTime.Now:HH:mm:ss.fff} ====");
}

var markerThread = new Thread(() =>
{
    while (!cts.IsCancellationRequested)
    {
        string? line = Console.ReadLine();
        if (line is null) return;
        string trimmed = line.Trim();
        if (trimmed.Length == 0) continue;

        if (trimmed.Equals("toggle", StringComparison.OrdinalIgnoreCase))
        {
            try { SendToggleNsMode(); }
            catch (Exception ex) { Log($"# Failed to send toggle command: {ex.Message}"); }
            continue;
        }

        if (trimmed.Equals("togglegyro", StringComparison.OrdinalIgnoreCase))
        {
            try { SendToggleGyroOutputMode(); }
            catch (Exception ex) { Log($"# Failed to send togglegyro command: {ex.Message}"); }
            continue;
        }

        Log($"==== MARKER @ {DateTime.Now:HH:mm:ss.fff} : {trimmed} ====");
    }
}) { IsBackground = true };
markerThread.Start();

byte[]? previous = null;
var buffer = new byte[inLen];

while (!cts.IsCancellationRequested)
{
    int read;
    try
    {
        read = stream.Read(buffer, 0, inLen);
    }
    catch (TimeoutException)
    {
        continue;
    }
    catch (Exception ex)
    {
        Log($"# Read failed: {ex.Message}");
        break;
    }

    if (read < 2 || buffer[0] != 0x02 || buffer[1] != 0x25) continue;

    if (previous is null)
    {
        previous = (byte[])buffer.Clone();
        Log($"{DateTime.Now:HH:mm:ss.fff}  BASELINE  {FormatBytes(buffer, read)}");
        continue;
    }

    var changed = new List<int>();
    for (int i = 0; i < read; i++)
    {
        if (buffer[i] != previous[i]) changed.Add(i);
    }

    if (changed.Count > 0)
    {
        string diff = string.Join(", ", changed.Select(i => $"[{i}] {previous[i]:X2}->{buffer[i]:X2}"));
        Log($"{DateTime.Now:HH:mm:ss.fff}  {diff}");
        Log($"{DateTime.Now:HH:mm:ss.fff}    full: {FormatBytes(buffer, read)}");
        previous = (byte[])buffer.Clone();
    }
}

Log("# Capture stopped.");
return;

static string FormatBytes(byte[] bytes, int count) =>
    string.Join(' ', bytes.Take(count).Select(b => b.ToString("X2")));
