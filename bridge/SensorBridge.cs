// SensorBridge: reads CPU/GPU temperatures, CPU package power and GPU load through LibreHardwareMonitorLib (PawnIO driver; needs admin)
// and writes them once a second to %ProgramData%\HudWidget\sensors.txt for HudWidget. No window, no network, CPU + GPU only.
// Built with the .NET Framework 4 compiler (C# 5), next to LibreHardwareMonitorLib.dll.
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Globalization;
using LibreHardwareMonitor.Hardware;

static class SensorBridge
{
    static void Main()
    {
        bool first;
        using (var mutex = new Mutex(true, "Global\\HudWidget.SensorBridge", out first))
        {
            if (!first) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HudWidget");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "sensors.txt");
            var tmp = file + ".tmp";
            var pc = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            pc.Open();
            var inv = CultureInfo.InvariantCulture;
            while (true)
            {
                float cpuT = float.NaN, gpuT = float.NaN, pw = float.NaN, gpuL = float.NaN, ccd = float.NaN;
                string cpuName = "", gpuName = "";
                foreach (var hw in pc.Hardware)
                {
                    hw.Update();
                    bool isCpu = hw.HardwareType == HardwareType.Cpu;
                    bool isGpu = hw.HardwareType == HardwareType.GpuAmd || hw.HardwareType == HardwareType.GpuNvidia || hw.HardwareType == HardwareType.GpuIntel;
                    if (isCpu) cpuName = hw.Name; else if (isGpu && gpuName == "") gpuName = hw.Name;
                    foreach (var s in hw.Sensors)
                    {
                        if (!s.Value.HasValue) continue;
                        float v = s.Value.Value;
                        if (isCpu && s.SensorType == SensorType.Temperature)
                        {
                            if (s.Name.StartsWith("Core (Tctl") || s.Name == "CPU Package" || s.Name.StartsWith("Package")) cpuT = v;
                            else if (s.Name.StartsWith("CCD") && float.IsNaN(ccd)) ccd = v;
                        }
                        else if (isCpu && s.SensorType == SensorType.Power && s.Name.StartsWith("Package")) pw = v;
                        else if (isGpu && s.SensorType == SensorType.Temperature && (s.Name == "GPU Core" || float.IsNaN(gpuT))) gpuT = v;
                        else if (isGpu && s.SensorType == SensorType.Load && s.Name == "GPU Core") gpuL = v;
                    }
                }
                if (float.IsNaN(cpuT)) cpuT = ccd;
                var sb = new StringBuilder();
                sb.Append("t=").Append(DateTime.UtcNow.Ticks.ToString(inv)).Append('\n');
                sb.Append("cpuTemp=").Append(cpuT.ToString("0.0", inv)).Append('\n');
                sb.Append("gpuTemp=").Append(gpuT.ToString("0.0", inv)).Append('\n');
                sb.Append("cpuPower=").Append(pw.ToString("0.0", inv)).Append('\n');
                sb.Append("gpuLoad=").Append(gpuL.ToString("0.0", inv)).Append('\n');
                sb.Append("cpuName=").Append(cpuName).Append('\n');
                sb.Append("gpuName=").Append(gpuName).Append('\n');
                try { File.WriteAllText(tmp, sb.ToString()); File.Copy(tmp, file, true); } catch { }
                Thread.Sleep(1000);
            }
        }
    }
}
