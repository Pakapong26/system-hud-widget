// Network speed, ping and disk space for the bottom strip. Speed comes from the interface byte counters (no traffic is made);
// ping is one ICMP echo every 5 s to a public resolver (1.1.1.1 by default, HUD_PING_HOST to change); disks are the local
// fixed drives (size and free space only, nothing on them is read), refreshed every 30 s, system drive first.
using System.Net.NetworkInformation;

namespace HudWidget;

sealed class NetDisk
{
    public double DownBps, UpBps;                  // bytes per second
    long pingMs = -1;                              // -1 = no reply yet / timed out; written by the ping task
    public long PingMs => Interlocked.Read(ref pingMs);
    public sealed record Disk(string Name, double TotalGb, double FreeGb) { public double Used => TotalGb > 0 ? 1 - FreeGb / TotalGb : 0; }
    public List<Disk> Disks = new();
    public readonly string PingHost = Environment.GetEnvironmentVariable("HUD_PING_HOST") ?? "1.1.1.1";

    // whole-PC power: only measurable while running on battery (the battery's discharge rate); NaN on AC power
    public double SysPowerW = double.NaN; public bool OnAc = true;
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct SYSTEM_BATTERY_STATE { public byte AcOnLine, BatteryPresent, Charging, Discharging, Spare1, Spare2, Spare3, Tag; public uint MaxCapacity, RemainingCapacity; public int Rate; public uint EstimatedTime, DefaultAlert1, DefaultAlert2; }
    [System.Runtime.InteropServices.DllImport("powrprof.dll")]
    static extern uint CallNtPowerInformation(int level, IntPtr inBuf, uint inLen, out SYSTEM_BATTERY_STATE outBuf, uint outLen);
    void ReadBattery()
    {
        try
        {
            if (CallNtPowerInformation(5, IntPtr.Zero, 0, out var b, (uint)System.Runtime.InteropServices.Marshal.SizeOf<SYSTEM_BATTERY_STATE>()) != 0) return;   // 5 = SystemBatteryState
            OnAc = b.AcOnLine != 0 || b.BatteryPresent == 0;
            SysPowerW = !OnAc && b.Discharging != 0 && b.Rate < 0 && b.Rate != int.MinValue ? -b.Rate / 1000.0 : double.NaN;   // Rate is mW, negative while discharging
        }
        catch { }
    }

    long lastRx = -1, lastTx = -1; DateTime lastAt;
    int tick, pinging;                              // pinging: 1 while a ping task runs

    public void Update()
    {
        try
        {
            long rx = 0, tx = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = ni.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (lastRx >= 0)
            {
                double dt = Math.Max(0.2, (now - lastAt).TotalSeconds);
                DownBps = Math.Max(0, (rx - lastRx) / dt); UpBps = Math.Max(0, (tx - lastTx) / dt);
            }
            lastRx = rx; lastTx = tx; lastAt = now;
        }
        catch { }

        if (tick % 30 == 0)
            try
            {
                var sys = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
                Disks = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                    .OrderBy(d => !d.Name.Equals(sys, StringComparison.OrdinalIgnoreCase)).ThenBy(d => d.Name)
                    .Select(d => new Disk(d.Name.TrimEnd('\\'), d.TotalSize / 1e9, d.TotalFreeSpace / 1e9)).ToList();
            }
            catch { }

        if (tick % 5 == 0) ReadBattery();
        if (tick % 5 == 0 && Interlocked.CompareExchange(ref pinging, 1, 0) == 0)
            _ = Task.Run(async () =>
            {
                try { using var p = new Ping(); var r = await p.SendPingAsync(PingHost, 2000); Interlocked.Exchange(ref pingMs, r.Status == IPStatus.Success ? r.RoundtripTime : -1); }
                catch { Interlocked.Exchange(ref pingMs, -1); }
                finally { Interlocked.Exchange(ref pinging, 0); }
            });
        tick++;
    }

    public static string Rate(double bps) => bps >= 1e6 ? $"{bps / 1e6:0.0} MB/s" : bps >= 1e3 ? $"{bps / 1e3:0} KB/s" : $"{bps:0} B/s";
}
