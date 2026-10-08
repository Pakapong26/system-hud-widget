// Docking between System HUD and AI Quota HUD (same file in both repos). While one widget is dragged it snaps to the
// other's edges (within 14 px); with "Move together" on, the other widget follows the drag. Windows are found by title.
using System.Runtime.InteropServices;

namespace HudWidget;

static class WidgetDock
{
    public const string HudTitle = "System HUD", QuotaTitle = "AI Quota HUD";
    const int Snap = 14;

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public static IntPtr Partner(string title) { var h = FindWindow(null, title); return h; }
    public static Rectangle? RectOf(string title)
    {
        var h = Partner(title);
        return h != IntPtr.Zero && GetWindowRect(h, out var r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;
    }

    // WM_MOVING: lParam points at the proposed window rectangle, which can be changed in place
    public static void OnMoving(IntPtr self, string partnerTitle, bool together, IntPtr lParam)
    {
        var p = Partner(partnerTitle);
        if (p == IntPtr.Zero || !GetWindowRect(p, out var pr) || !GetWindowRect(self, out var cur)) return;
        var r = Marshal.PtrToStructure<RECT>(lParam);
        if (together)
        {
            int dx = r.Left - cur.Left, dy = r.Top - cur.Top;
            if (dx != 0 || dy != 0) SetWindowPos(p, IntPtr.Zero, pr.Left + dx, pr.Top + dy, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
            return;
        }
        int w = r.Right - r.Left, h = r.Bottom - r.Top, x = r.Left, y = r.Top;
        bool vOverlap = r.Bottom > pr.Top - Snap && r.Top < pr.Bottom + Snap, hOverlap = r.Right > pr.Left - Snap && r.Left < pr.Right + Snap;
        if (vOverlap)
        {
            if (Math.Abs(r.Left - pr.Right) < Snap) x = pr.Right;              // to the right of the other
            else if (Math.Abs(r.Right - pr.Left) < Snap) x = pr.Left - w;      // to the left
            if (x != r.Left && Math.Abs(r.Top - pr.Top) < Snap) y = pr.Top;    // and line the tops up
        }
        if (hOverlap)
        {
            if (Math.Abs(r.Top - pr.Bottom) < Snap) y = pr.Bottom;             // below
            else if (Math.Abs(r.Bottom - pr.Top) < Snap) y = pr.Top - h;       // above
            if (y != r.Top && Math.Abs(r.Left - pr.Left) < Snap) x = pr.Left;  // and line the left edges up
        }
        r.Left = x; r.Top = y; r.Right = x + w; r.Bottom = y + h;
        Marshal.StructureToPtr(r, lParam, false);
    }
}
