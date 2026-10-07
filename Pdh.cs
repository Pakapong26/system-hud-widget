// Minimal PDH wrapper: one query collects every counter at once (cheap), and wildcard counters pick up new instances by themselves.
using System.Runtime.InteropServices;

namespace HudWidget;

sealed class PdhQuery : IDisposable
{
    IntPtr query;
    public PdhQuery() { if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero; }

    public IntPtr Add(string path) { if (query == IntPtr.Zero) return IntPtr.Zero; return PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) == 0 ? c : IntPtr.Zero; }

    public bool Collect() => query != IntPtr.Zero && PdhCollectQueryData(query) == 0;

    public static double Value(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return double.NaN;
        return PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, out _, out var v) == 0 && (v.CStatus == 0 || v.CStatus == 1) ? v.doubleValue : double.NaN;
    }

    // every instance of a wildcard counter: (name, value)
    public static List<(string name, double value)> Array(IntPtr counter)
    {
        var list = new List<(string, double)>();
        if (counter == IntPtr.Zero) return list;
        uint size = 0, count = 0;
        int st = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
        if (st != unchecked((int)0x800007D2) || size == 0) return list;             // PDH_MORE_DATA
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buf) != 0) return list;
            int item = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var it = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buf + i * item);
                if (it.FmtValue.CStatus == 0 || it.FmtValue.CStatus == 1) list.Add((Marshal.PtrToStringUni(it.szName) ?? "", it.FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return list;
    }

    public void Dispose() { if (query != IntPtr.Zero) { PdhCloseQuery(query); query = IntPtr.Zero; } }

    const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
    [StructLayout(LayoutKind.Sequential)] struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }
    [StructLayout(LayoutKind.Sequential)] struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhOpenQuery(string src, IntPtr user, out IntPtr q);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhAddEnglishCounter(IntPtr q, string path, IntPtr user, out IntPtr c);
    [DllImport("pdh.dll")] static extern int PdhCollectQueryData(IntPtr q);
    [DllImport("pdh.dll")] static extern int PdhGetFormattedCounterValue(IntPtr c, uint fmt, out uint type, out PDH_FMT_COUNTERVALUE v);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern int PdhGetFormattedCounterArray(IntPtr c, uint fmt, ref uint size, out uint count, IntPtr buf);
    [DllImport("pdh.dll")] static extern int PdhCloseQuery(IntPtr q);
}
