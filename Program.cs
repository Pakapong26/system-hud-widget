// HUD Widget: a small sci-fi system monitor for the desktop (CPU, per-thread load, clock, RAM, GPU load, CPU/GPU temperature).
// Light by design: one refresh a second, Windows performance counters (no admin), and temperatures read from
// LibreHardwareMonitor's local web server (http://localhost:8085/data.json) when it runs; nothing else is polled.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace HudWidget;

static class Program
{
    public static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HudWidget"); Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "errors.log"); if (File.Exists(f) && new FileInfo(f).Length > 200_000) File.Delete(f);
            File.AppendAllText(f, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}\r\n");
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "HudWidget.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        // never show the .NET error dialog: log the error and carry on (the next frame redraws everything)
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        var args = Environment.GetCommandLineArgs();
        if (args.Length >= 3 && args[1] == "--snap") { HudForm.Snapshot(args[2], args.Length > 3 ? args[3] : ""); return; }      // render one frame to a PNG (for checking the design)
        Application.Run(new HudForm());
    }
}

sealed class Sensors : IDisposable
{
    public readonly int Threads = Environment.ProcessorCount;
    public float CpuLoad, CpuGhz, RamUsedGb, RamTotalGb, GpuLoad = -1;
    public float CpuTemp = float.NaN, GpuTemp = float.NaN, CpuPower = float.NaN;
    public float[] ThreadLoad;
    public bool LhmOnline;
    public string Source = "";
    static readonly string bridgeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HudWidget", "sensors.txt");

    // SensorBridge.exe (admin, LibreHardwareMonitorLib) writes this file once a second; use it while it is fresh (< 5 s old)
    bool ReadBridge()
    {
        try
        {
            var fi = new FileInfo(bridgeFile);
            if (!fi.Exists || (DateTime.Now - fi.LastWriteTime).TotalSeconds > 5) return false;
            var kv = File.ReadAllLines(bridgeFile).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            float F(string k) => kv.TryGetValue(k, out var v) ? Num(v) : float.NaN;
            CpuTemp = F("cpuTemp"); GpuTemp = F("gpuTemp"); CpuPower = F("cpuPower");
            var gl = F("gpuLoad"); if (!float.IsNaN(gl)) GpuLoad = gl;
            return true;
        }
        catch { return false; }
    }
    public string CpuName = "CPU", GpuName = "GPU";

    readonly PdhQuery q = new();
    readonly IntPtr cTotal, cPerf, cThreads, cGpu;
    readonly float baseGhz;
    int tick;
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMilliseconds(700) };
    bool lhmBusy;

    public Sensors()
    {
        ThreadLoad = new float[Threads];
        cTotal = q.Add(@"\Processor Information(_Total)\% Processor Utility");
        cPerf = q.Add(@"\Processor Information(_Total)\% Processor Performance");
        cThreads = q.Add(@"\Processor Information(*)\% Processor Utility");
        cGpu = q.Add(@"\GPU Engine(*engtype_3D)\Utilization Percentage");
        q.Collect();
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            baseGhz = k?.GetValue("~MHz") is int mhz ? mhz / 1000f : 3.2f;
            var n = (k?.GetValue("ProcessorNameString") as string ?? "CPU").Trim();
            CpuName = n.Replace("with Radeon Graphics", "").Replace("AMD ", "").Replace("Intel(R) Core(TM) ", "").Trim();
        }
    }

    public void Update()
    {
        tick++;
        if (q.Collect())
        {
            var tl = PdhQuery.Value(cTotal); if (double.IsFinite(tl)) CpuLoad = Clamp((float)tl);    // home PC 2026-09-30: one NaN sample stuck the ring at NaN and GDI+ threw "Overflow error"
            var pf = PdhQuery.Value(cPerf); if (!double.IsNaN(pf)) CpuGhz = Math.Clamp(baseGhz * (float)pf / 100f, 0f, 9f);
            foreach (var (name, v) in PdhQuery.Array(cThreads))                      // instances "0,0" .. "0,15" (group, thread); skip the totals
            {
                var parts = name.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[1], out int i) && i >= 0 && i < Threads) ThreadLoad[i] = Clamp((float)v);
            }
            if (!LhmOnline && cGpu != IntPtr.Zero) GpuLoad = Clamp((float)PdhQuery.Array(cGpu).Sum(x => x.value));
        }
        var ms = new MEMORYSTATUSEX(); ms.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        if (GlobalMemoryStatusEx(ref ms)) { RamTotalGb = ms.ullTotalPhys / 1073741824f; RamUsedGb = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824f; }
        if (ReadBridge()) { LhmOnline = true; Source = "SensorBridge"; }
        else if (tick % 2 == 0 && !lhmBusy) _ = PollLhm();
    }

    async Task PollLhm()
    {
        lhmBusy = true;
        try
        {
            var json = await http.GetStringAsync("http://localhost:8085/data.json");
            using var doc = JsonDocument.Parse(json);
            float cpuT = float.NaN, gpuT = float.NaN, pw = float.NaN, gpuL = float.NaN;
            Walk(doc.RootElement, null, null, ref cpuT, ref gpuT, ref pw, ref gpuL);
            CpuTemp = cpuT; GpuTemp = gpuT; CpuPower = pw; if (!float.IsNaN(gpuL)) GpuLoad = gpuL;
            LhmOnline = true; Source = "LibreHardwareMonitor";
        }
        catch { LhmOnline = false; CpuTemp = GpuTemp = CpuPower = float.NaN; }
        finally { lhmBusy = false; }
    }

    // hw = "cpu" or "gpu" for the hardware node we are under; grp = the sensor group ("Temperatures", "Powers", "Load" ...)
    void Walk(JsonElement n, string hw, string grp, ref float cpuT, ref float gpuT, ref float pw, ref float gpuL)
    {
        string text = n.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
        string img = n.TryGetProperty("ImageURL", out var im) ? (im.GetString() ?? "").ToLowerInvariant() : "";
        if (img.Contains("cpu")) { hw = "cpu"; CpuName = Short(text); }
        else if (img.Contains("ati") || img.Contains("nvidia") || img.Contains("gpu") || text.Contains("Radeon") || text.Contains("GeForce")) { if (hw == null || hw == "gpu") { hw = "gpu"; if (img.Length > 0) GpuName = Short(text); } }
        if (text is "Temperatures" or "Powers" or "Load") grp = text;
        if (n.TryGetProperty("Value", out var v) && hw != null && grp != null && !(n.TryGetProperty("Children", out var ch0) && ch0.GetArrayLength() > 0))
        {
            float val = Num(v.GetString());
            if (!float.IsNaN(val))
            {
                if (hw == "cpu" && grp == "Temperatures" && (text.StartsWith("Core (Tctl") || text == "CPU Package" || text.StartsWith("Package") || float.IsNaN(cpuT))) cpuT = val;
                else if (hw == "gpu" && grp == "Temperatures" && (text is "GPU Core" or "GPU VR SoC" || float.IsNaN(gpuT))) { if (text == "GPU Core" || float.IsNaN(gpuT)) gpuT = val; }
                else if (hw == "cpu" && grp == "Powers" && text.StartsWith("Package")) pw = val;
                else if (hw == "gpu" && grp == "Load" && text == "GPU Core") gpuL = val;
            }
        }
        if (n.TryGetProperty("Children", out var ch)) foreach (var c in ch.EnumerateArray()) Walk(c, hw, grp, ref cpuT, ref gpuT, ref pw, ref gpuL);
    }

    static string Short(string s) => s.Replace("AMD ", "").Replace("with Radeon Graphics", "").Replace("(TM)", "").Replace("NVIDIA ", "").Trim();
    static float Num(string s)
    {
        if (string.IsNullOrEmpty(s)) return float.NaN;
        var digits = new string(s.TakeWhile(ch => char.IsDigit(ch) || ch == '.' || ch == ',' || ch == '-').ToArray()).Replace(',', '.');
        return float.TryParse(digits, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : float.NaN;
    }
    static float Clamp(float x) => float.IsNaN(x) || float.IsInfinity(x) || x < 0 ? 0 : x > 100 ? 100 : x;   // a counter with no value yet reads NaN

    public void Dispose() => q.Dispose();

    [StructLayout(LayoutKind.Sequential)] struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
}

