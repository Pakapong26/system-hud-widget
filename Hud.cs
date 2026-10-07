// The widget window: a per-pixel-alpha layered window drawn with GDI+ once a second. Everything the owner can change is in the right-click menu
// and is saved in %APPDATA%\HudWidget\settings.txt.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace HudWidget;

sealed class HudForm : Form
{
    // ---- themes: (name, CPU accent, GPU accent) ----
    static readonly (string name, Color a1, Color a2)[] Themes =
    {
        ("Neon Cyan", Color.FromArgb(34, 211, 238), Color.FromArgb(167, 139, 250)),
        ("Matrix Green", Color.FromArgb(74, 222, 128), Color.FromArgb(190, 242, 100)),
        ("Synthwave", Color.FromArgb(244, 114, 182), Color.FromArgb(129, 140, 248)),
        ("Solar Amber", Color.FromArgb(251, 191, 36), Color.FromArgb(251, 146, 60)),
        ("Arctic Ice", Color.FromArgb(186, 230, 253), Color.FromArgb(224, 231, 255)),
        ("Crimson", Color.FromArgb(248, 113, 113), Color.FromArgb(253, 164, 175)),
        ("Toxic Lime", Color.FromArgb(163, 230, 53), Color.FromArgb(45, 212, 191)),
    };
    static readonly string[] Backgrounds = { "Glass (dark)", "Glass (tinted)", "Solid black", "None (floating)" };
    static readonly Color Amber = Color.FromArgb(251, 191, 36), Red = Color.FromArgb(248, 113, 113), Ink = Color.FromArgb(232, 246, 255), Dim = Color.FromArgb(125, 150, 172);

    // ---- settings ----
    int theme = 0, bg = 0, panelAlpha = 210;   // panel alpha 0..255 (background only)
    byte opacity = 255;                         // the whole widget
    float size = 1f;                            // 0.75 .. 1.5
    bool fahrenheit, showThreads = true, showGraph = true, showClock = true, corners = true, pinDesktop;
    bool resizing; Point resizeStart; float resizeSize0;

    readonly Sensors s = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    readonly System.Windows.Forms.Timer anim = new() { Interval = 40 };      // ~25 fps, only while a value is still gliding
    float dCpu, dGpu, dRam; float[] dThr;
    readonly Font fTitle = new("Bahnschrift SemiCondensed", 8.5f, FontStyle.Regular, GraphicsUnit.Point), fBig = new("Bahnschrift", 20f, FontStyle.Bold, GraphicsUnit.Point), fSmall = new("Bahnschrift SemiCondensed", 8f, FontStyle.Regular, GraphicsUnit.Point);                                       // the values on screen, eased toward the sensors
    readonly Queue<float> cpuHist = new(), gpuHist = new();
    float scale = 1f;
    readonly string cfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HudWidget", "settings.txt");

    Color A1 => Themes[theme].a1;
    Color A2 => Themes[theme].a2;
    int W => showThreads ? 380 : 256;
    int H => showGraph ? 214 : 172;

    public HudForm()
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        LoadSettings();
        ApplySize();
        if (Location == Point.Empty) { var wa = Screen.PrimaryScreen.WorkingArea; Location = new Point(wa.Right - Width - 24, wa.Top + 24); }
        ContextMenuStrip = BuildMenu();
        MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (InGrip(e.Location)) { resizing = true; resizeStart = Cursor.Position; resizeSize0 = size; Capture = true; return; }   // drag the bottom-right corner to resize
            ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); SaveSettings();
        };
        MouseMove += (_, e) =>
        {
            if (resizing) { size = Math.Clamp(resizeSize0 + (Cursor.Position.X - resizeStart.X) / (W * DeviceDpi / 96f), 0.6f, 2.2f); ApplySize(); Render(); }
            else Cursor = InGrip(e.Location) ? Cursors.SizeNWSE : Cursors.Default;
        };
        MouseUp += (_, _) => { if (resizing) { resizing = false; Capture = false; SaveSettings(); } };
        MouseWheel += (_, e) => { if ((ModifierKeys & Keys.Control) != 0) { size = Math.Clamp(size + (e.Delta > 0 ? 0.05f : -0.05f), 0.6f, 2.2f); ApplySize(); SaveSettings(); Render(); } };   // Ctrl+wheel resizes
        dThr = new float[s.Threads];
        timer.Tick += (_, _) =>
        {
            timer.Interval = Math.Max(200, 1000 - DateTime.Now.Millisecond + 15);  // tick just after each real second, so the clock never skips
            s.Update(); Push(cpuHist, s.CpuLoad); Push(gpuHist, Math.Max(0, s.GpuLoad));
            if (!anim.Enabled) anim.Start();
            Render();
        };
        anim.Tick += (_, _) => { if (!Step()) anim.Stop(); Render(); };
        Shown += (_, _) => { ApplyPin(); s.Update(); Render(); timer.Start(); };
        FormClosed += (_, _) => { SaveSettings(); timer.Dispose(); anim.Dispose(); s.Dispose(); };
    }

    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80000 | 0x80; return cp; } } // layered + tool window (no taskbar, no Alt-Tab)

    void ApplySize() { scale = DeviceDpi / 96f * size; Size = new Size((int)(W * scale), (int)(H * scale)); }
    bool InGrip(Point p) => p.X > Width - 16 * scale && p.Y > Height - 16 * scale;

    // Pin to desktop: owned by the desktop window (Progman) and kept at the bottom of the z-order, so it sits on the wallpaper under
    // every window and stays after Win+D, like Rainmeter's "On desktop".
    void ApplyPin()
    {
        if (!IsHandleCreated) return;
        if (pinDesktop) { TopMost = false; SetWindowLongPtr(Handle, -8, FindWindow("Progman", null)); SetWindowPos(Handle, (IntPtr)1, 0, 0, 0, 0, 0x13); }
        else SetWindowLongPtr(Handle, -8, IntPtr.Zero);
    }
    protected override void WndProc(ref Message m)
    {
        if (pinDesktop && m.Msg == 0x46)   // WM_WINDOWPOSCHANGING: stay under every other window
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(m.LParam);
            if ((wp.flags & 0x4) == 0) { wp.hwndInsertAfter = (IntPtr)1; Marshal.StructureToPtr(wp, m.LParam, false); }
        }
        base.WndProc(ref m);
    }
    [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // ---------------- menu ----------------
    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        m.Renderer = new ToolStripProfessionalRenderer(new DarkColors());
        m.ForeColor = Ink;

        var mTheme = new ToolStripMenuItem("Theme / colours");
        for (int i = 0; i < Themes.Length; i++) { int k = i; mTheme.DropDownItems.Add(Radio(Themes[i].name, () => theme == k, () => theme = k)); }

        var mBg = new ToolStripMenuItem("Background");
        for (int i = 0; i < Backgrounds.Length; i++) { int k = i; mBg.DropDownItems.Add(Radio(Backgrounds[i], () => bg == k, () => bg = k)); }
        mBg.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (label, a) in new[] { ("Background 100%", 255), ("Background 85%", 217), ("Background 70%", 178), ("Background 50%", 128), ("Background 30%", 77), ("Background 15%", 38) })
            mBg.DropDownItems.Add(Radio(label, () => panelAlpha == a, () => panelAlpha = a));
        mBg.DropDownItems.Add(new ToolStripSeparator());
        mBg.DropDownItems.Add(Check("HUD corner brackets", () => corners, v => corners = v));

        var mOp = new ToolStripMenuItem("Whole widget opacity");
        foreach (var (label, a) in new[] { ("100%", (byte)255), ("85%", (byte)217), ("70%", (byte)178), ("55%", (byte)140), ("40%", (byte)102) })
            mOp.DropDownItems.Add(Radio(label, () => opacity == a, () => opacity = a));

        var mSize = new ToolStripMenuItem("Size  (or Ctrl+wheel / drag corner)");
        foreach (var (label, f) in new[] { ("60%", 0.6f), ("75%", 0.75f), ("90%", 0.9f), ("100%", 1f), ("120%", 1.2f), ("150%", 1.5f), ("180%", 1.8f), ("220%", 2.2f) })
            mSize.DropDownItems.Add(Radio(label, () => Math.Abs(size - f) < 0.01f, () => { size = f; ApplySize(); }));

        var mShow = new ToolStripMenuItem("Show");
        mShow.DropDownItems.Add(Check("Threads + memory", () => showThreads, v => { showThreads = v; ApplySize(); }));
        mShow.DropDownItems.Add(Check("60 s history graph", () => showGraph, v => { showGraph = v; ApplySize(); }));
        mShow.DropDownItems.Add(Check("Clock", () => showClock, v => showClock = v));

        var mUnit = new ToolStripMenuItem("Temperature unit");
        mUnit.DropDownItems.Add(Radio("°C", () => !fahrenheit, () => fahrenheit = false));
        mUnit.DropDownItems.Add(Radio("°F", () => fahrenheit, () => fahrenheit = true));

        var miTop = Check("Always on top", () => TopMost && !pinDesktop, v => { pinDesktop = false; ApplyPin(); TopMost = v; });
        var miPin = Check("Pin to desktop (like Rainmeter)", () => pinDesktop, v => { pinDesktop = v; ApplyPin(); });
        var miStart = new ToolStripMenuItem("Start with Windows") { Checked = IsStartup() };
        miStart.Click += (_, _) => { ToggleStartup(); miStart.Checked = IsStartup(); };
        var miLhm = new ToolStripMenuItem("Temperature source: " + (s.LhmOnline ? "LibreHardwareMonitor" : "not running"));
        m.Opening += (_, _) => miLhm.Text = "Temperature source: " + (s.LhmOnline ? s.Source + " ✓" : "SensorBridge not running");
        miLhm.Enabled = false;

        m.Opening += (_, _) => RefreshChecks();
        foreach (var sub in new[] { mTheme, mBg, mOp, mSize, mShow, mUnit }) sub.DropDownOpening += (_, _) => RefreshChecks();
        m.Items.AddRange(new ToolStripItem[] { mTheme, mBg, mOp, mSize, mShow, mUnit, new ToolStripSeparator(), miPin, miTop, miStart, new ToolStripSeparator(), miLhm,
            new ToolStripSeparator(), new ToolStripMenuItem("Exit", null, (_, _) => Close()) });
        return m;
    }

    ToolStripMenuItem Radio(string text, Func<bool> isOn, Action set)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => { set(); SaveSettings(); Render(); RefreshChecks(); };
        checks.Add((it, isOn));
        return it;
    }

    ToolStripMenuItem Check(string text, Func<bool> isOn, Action<bool> set)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += (_, _) => { set(!isOn()); SaveSettings(); Render(); RefreshChecks(); };
        checks.Add((it, isOn));
        return it;
    }

    readonly List<(ToolStripMenuItem item, Func<bool> on)> checks = new();
    void RefreshChecks() { foreach (var (it, on) in checks) it.Checked = on(); }

    sealed class DarkColors : ProfessionalColorTable
    {
        static readonly Color bgc = Color.FromArgb(14, 20, 32), sel = Color.FromArgb(28, 52, 72), line = Color.FromArgb(40, 70, 95);
        public override Color ToolStripDropDownBackground => bgc;
        public override Color ImageMarginGradientBegin => bgc; public override Color ImageMarginGradientMiddle => bgc; public override Color ImageMarginGradientEnd => bgc;
        public override Color MenuItemSelected => sel; public override Color MenuItemBorder => line; public override Color MenuBorder => line;
        public override Color MenuItemSelectedGradientBegin => sel; public override Color MenuItemSelectedGradientEnd => sel;
        public override Color SeparatorDark => line; public override Color SeparatorLight => bgc;
        public override Color CheckBackground => sel; public override Color CheckSelectedBackground => sel; public override Color CheckPressedBackground => sel;
    }

    // ease the on-screen values toward the latest reading; returns true while anything is still moving
    bool Step()
    {
        const float k = 0.28f; bool moving = false;
        float E(float d, float t) { if (!float.IsFinite(t)) t = float.IsFinite(d) ? d : 0; if (!float.IsFinite(d)) d = t; float n = d + (t - d) * k; if (Math.Abs(t - n) < 0.15f) n = t; else moving = true; return n; }
        dCpu = E(dCpu, s.CpuLoad); dGpu = E(dGpu, Math.Max(0, s.GpuLoad));
        dRam = E(dRam, s.RamTotalGb > 0 ? 100f * s.RamUsedGb / s.RamTotalGb : 0);
        for (int i = 0; i < dThr.Length; i++) dThr[i] = E(dThr[i], s.ThreadLoad[i]);
        return moving;
    }

    // ---------------- drawing ----------------
    static void Push(Queue<float> q, float v) { q.Enqueue(v); while (q.Count > 60) q.Dequeue(); }
    static Color Heat(float t, Color cool) => float.IsNaN(t) ? cool : t >= 90 ? Red : t >= 75 ? Amber : cool;
    string Temp(float c) => float.IsNaN(c) ? (fahrenheit ? "--- °F" : "--.- °C") : fahrenheit ? $"{c * 9 / 5 + 32:0} °F" : $"{c:0.0} °C";

    public static void Snapshot(string file, string opts)
    {
        using var f = new HudForm();
        if (!string.IsNullOrEmpty(opts)) f.ApplyOpts(opts);
        f.ApplySize();
        for (int i = 0; i < 4; i++) { f.s.Update(); Push(f.cpuHist, f.s.CpuLoad); Push(f.gpuHist, Math.Max(0, f.s.GpuLoad)); Thread.Sleep(1000); }
        for (int i = 0; i < 30; i++) Push(f.cpuHist, 20 + 15 * (float)Math.Sin(i / 3.0) + f.s.CpuLoad / 3);
        f.dThr ??= new float[f.s.Threads]; while (f.Step()) { }
        using var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            using var desk = new LinearGradientBrush(new Rectangle(0, 0, f.Width, f.Height), Color.FromArgb(40, 60, 90), Color.FromArgb(90, 50, 80), 30f);
            g.FillRectangle(desk, 0, 0, f.Width, f.Height);                         // a wallpaper-like backdrop to show the transparency
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.ScaleTransform(f.scale, f.scale);
            f.Draw(g);
        }
        bmp.Save(file, ImageFormat.Png);
    }

    void ApplyOpts(string o)   // e.g. "theme=2;bg=0;alpha=128" (snapshots only)
    {
        foreach (var kv in o.Split(';').Select(x => x.Split('=')).Where(x => x.Length == 2))
            switch (kv[0]) { case "theme": theme = int.Parse(kv[1]); break; case "bg": bg = int.Parse(kv[1]); break; case "alpha": panelAlpha = int.Parse(kv[1]); break; case "threads": showThreads = kv[1] == "1"; break; case "graph": showGraph = kv[1] == "1"; break; case "size": size = float.Parse(kv[1], System.Globalization.CultureInfo.InvariantCulture); break; }
    }

    void Render()
    {
        try { RenderFrame(); } catch (Exception ex) { Program.Log(ex); }                // a bad frame is skipped, never a dialog
    }

    void RenderFrame()
    {
        using var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.ScaleTransform(scale, scale);
            Draw(g);
        }
        Blit(bmp);
    }

    void Draw(Graphics g)
    {
        int w = W, h = H;
        var r = new RectangleF(1, 1, w - 2, h - 2);
        using (var path = Round(r, 14))
        {
            if (bg == 0 || bg == 1)
            {
                var top = bg == 0 ? Color.FromArgb(panelAlpha, 9, 14, 26) : Color.FromArgb(panelAlpha, Blend(A1, Color.FromArgb(8, 12, 22), 0.16f));
                var bot = bg == 0 ? Color.FromArgb(Math.Max(0, panelAlpha - 20), 5, 8, 16) : Color.FromArgb(Math.Max(0, panelAlpha - 20), Blend(A2, Color.FromArgb(6, 8, 16), 0.12f));
                using var fill = new LinearGradientBrush(r, top, bot, 90f); g.FillPath(fill, path);
            }
            else if (bg == 2) { using var fill = new SolidBrush(Color.FromArgb(panelAlpha, 0, 0, 0)); g.FillPath(fill, path); }
            else { using var catchAll = new SolidBrush(Color.FromArgb(1, 0, 0, 0)); g.FillPath(catchAll, path); }   // nearly invisible, still catches the mouse
            if (bg != 3) { using var rim = new LinearGradientBrush(r, Color.FromArgb(150, A1), Color.FromArgb(120, A2), 0f); using var pen = new Pen(rim, 1f); g.DrawPath(pen, path); }
        }
        if (corners)
        {
            using var br = new Pen(Color.FromArgb(220, A1), 1.6f); float L = 12;
            g.DrawLines(br, new[] { new PointF(6, 6 + L), new PointF(6, 6), new PointF(6 + L, 6) });
            g.DrawLines(br, new[] { new PointF(w - 6 - L, h - 6), new PointF(w - 6, h - 6), new PointF(w - 6, h - 6 - L) });
        }



        using var ink = new SolidBrush(Ink); using var dim = new SolidBrush(Dim);
        bool floating = bg == 3;

        Text(g, "SYSTEM  //  " + (Environment.GetEnvironmentVariable("HUD_LABEL") ?? Environment.MachineName).ToUpperInvariant(), fTitle, dim, 22, 10, floating);
        if (showClock) { var clock = DateTime.Now.ToString("HH:mm:ss"); using var cb0 = new SolidBrush(Color.FromArgb(210, A1)); Text(g, clock, fTitle, cb0, w - 22 - g.MeasureString(clock, fTitle).Width, 10, floating); }

        Ring(g, new RectangleF(20, 34, 104, 104), dCpu, Heat(s.CpuTemp, A1), "CPU", fBig, fSmall, ink, dim, Temp(s.CpuTemp), $"{s.CpuGhz:0.00} GHz", floating);
        Ring(g, new RectangleF(136, 34, 104, 104), s.GpuLoad < 0 ? 0 : dGpu, Heat(s.GpuTemp, A2), "GPU", fBig, fSmall, ink, dim, Temp(s.GpuTemp), s.GpuLoad < 0 ? "n/a" : "3D load", floating);

        if (showThreads)
        {
            float x0 = 254, y0 = 42, span = 118f, gap = s.Threads > 24 ? 1f : 1.6f, bw = (span - gap * (s.Threads - 1)) / s.Threads, bh = 50;   // the bars fit any thread count
            Text(g, $"THREADS  ×{s.Threads}", fSmall, dim, x0 - 2, 24, floating);
            for (int i = 0; i < s.Threads; i++)
            {
                float x = x0 + i * (bw + gap);
                using var track = new SolidBrush(Color.FromArgb(40, A1)); g.FillRectangle(track, x, y0, bw, bh);
                float v = dThr[i] / 100f * bh; var c = dThr[i] >= 90 ? Amber : A1;
                using var lb = new LinearGradientBrush(new RectangleF(x, y0, bw, bh + 1), Color.FromArgb(245, c), Color.FromArgb(90, c), 90f);
                g.FillRectangle(lb, x, y0 + bh - v, bw, v);
            }
            float ry = 118, rw = span, ramPct = float.IsFinite(dRam) ? Math.Clamp(dRam / 100f, 0, 1) : 0;
            Text(g, "RAM", fSmall, dim, x0 - 2, ry - 16, floating);
            var ramTxt = $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0.0} GB";
            Text(g, ramTxt, fSmall, ink, x0 + rw - g.MeasureString(ramTxt, fSmall).Width + 2, ry - 16, floating);
            using (var track = new SolidBrush(Color.FromArgb(40, A2))) g.FillRectangle(track, x0, ry, rw, 6);
            using (var rb = new LinearGradientBrush(new RectangleF(x0, ry, rw, 6), A2, ramPct > 0.9 ? Red : A1, 0f)) g.FillRectangle(rb, x0, ry, Math.Max(0.1f, rw * ramPct), 6);
            if (!float.IsNaN(s.CpuPower)) Text(g, $"PKG {s.CpuPower:0.0} W", fSmall, dim, x0 - 2, ry + 10, floating);
        }

        string src = s.LhmOnline ? "TEMP · " + s.Source : "TEMP · start SensorBridge";
        using var srcBr = new SolidBrush(s.LhmOnline ? Color.FromArgb(160, A1) : Color.FromArgb(210, Amber));
        if (showGraph)
        {
            var hr = new RectangleF(20, 150, w - 40, 34);
            using (var grid = new Pen(Color.FromArgb(28, Ink), 1f)) { for (int i = 0; i <= 4; i++) g.DrawLine(grid, hr.Left, hr.Top + i * hr.Height / 4, hr.Right, hr.Top + i * hr.Height / 4); }
            Spark(g, hr, gpuHist, A2); Spark(g, hr, cpuHist, A1);
            Text(g, "LOAD · 60 s", fSmall, dim, hr.Left, hr.Bottom + 2, floating);
            Text(g, src, fSmall, srcBr, hr.Right - g.MeasureString(src, fSmall).Width, hr.Bottom + 2, floating);
        }
        else Text(g, src, fSmall, srcBr, w - 20 - g.MeasureString(src, fSmall).Width, h - 20, floating);
        using (var grip = new Pen(Color.FromArgb(110, A1), 1f)) { g.DrawLine(grip, w - 5, h - 12, w - 12, h - 5); g.DrawLine(grip, w - 5, h - 8, w - 8, h - 5); }
    }

    static void Text(Graphics g, string t, Font f, Brush b, float x, float y, bool shadow)
    {
        if (shadow) { using var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)); g.DrawString(t, f, sh, x + 1, y + 1); }
        g.DrawString(t, f, b, x, y);
    }

    void Ring(Graphics g, RectangleF r, float pct, Color c, string label, Font fBig, Font fSmall, Brush ink, Brush dim, string temp, string sub, bool shadow)
    {
        const float start = 135, sweep = 270;
        using (var track = new Pen(Color.FromArgb(shadow ? 70 : 38, c), 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(track, r, start, sweep);
        for (int i = 0; i <= 10; i++)
        {
            double a = (start + sweep * i / 10.0) * Math.PI / 180; float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, ro = r.Width / 2 + 7, ri = ro - (i % 5 == 0 ? 5 : 3);
            using var tp = new Pen(Color.FromArgb(i % 5 == 0 ? 130 : 65, c), 1f);
            g.DrawLine(tp, cx + (float)Math.Cos(a) * ri, cy + (float)Math.Sin(a) * ri, cx + (float)Math.Cos(a) * ro, cy + (float)Math.Sin(a) * ro);
        }
        if (!float.IsFinite(pct)) pct = 0; pct = Math.Clamp(pct, 0, 100);
        float sw = Math.Max(0.5f, sweep * pct / 100f);
        using (var glow = new Pen(Color.FromArgb(60, c), 13f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(glow, r, start, sw);
        using (var arc = new Pen(c, 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(arc, r, start, sw);
        var pctTxt = $"{pct:0}";
        var ps = g.MeasureString(pctTxt, fBig); float mx = r.X + r.Width / 2, my = r.Y + r.Height / 2;
        Text(g, pctTxt, fBig, ink, mx - ps.Width / 2 - 4, my - ps.Height / 2 - 8, shadow);
        Text(g, "%", fSmall, dim, mx + ps.Width / 2 - 8, my - 10, shadow);
        using var cb = new SolidBrush(c);
        var ls = g.MeasureString(label, fSmall); Text(g, label, fSmall, cb, mx - ls.Width / 2, r.Y + 18, shadow);
        var ts = g.MeasureString(temp, fSmall); Text(g, temp, fSmall, ink, mx - ts.Width / 2, my + 12, shadow);
        var ss = g.MeasureString(sub, fSmall); Text(g, sub, fSmall, dim, mx - ss.Width / 2, r.Bottom - 8, shadow);
    }

    static void Spark(Graphics g, RectangleF r, Queue<float> q, Color c)
    {
        if (q.Count < 2) return;
        var a = q.ToArray(); var pts = new PointF[a.Length];
        for (int i = 0; i < a.Length; i++) { float v = float.IsFinite(a[i]) ? Math.Clamp(a[i], 0, 100) : 0; pts[i] = new PointF(r.Right - (a.Length - 1 - i) * r.Width / 59f, r.Bottom - v / 100f * r.Height); }
        using var pen = new Pen(Color.FromArgb(225, c), 1.4f); g.DrawLines(pen, pts);
        var fillPts = pts.Concat(new[] { new PointF(pts[^1].X, r.Bottom), new PointF(pts[0].X, r.Bottom) }).ToArray();
        using var fb = new LinearGradientBrush(r, Color.FromArgb(70, c), Color.FromArgb(0, c), 90f); g.FillPolygon(fb, fillPts);
    }

    static Color Blend(Color a, Color b, float t) => Color.FromArgb((int)(b.R + (a.R - b.R) * t), (int)(b.G + (a.G - b.G) * t), (int)(b.B + (a.B - b.B) * t));

    static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    void Blit(Bitmap bmp)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc), hBmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(memDc, hBmp);
        try
        {
            var sz = new SIZE { cx = bmp.Width, cy = bmp.Height }; var src = new POINT(); var pos = new POINT { x = Left, y = Top };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = opacity, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref pos, ref sz, memDc, ref src, 0, ref blend, 2);
        }
        finally { SelectObject(memDc, old); DeleteObject(hBmp); DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); }
    }

    // ---------------- settings ----------------
    void LoadSettings()
    {
        try
        {
            var kv = File.ReadAllLines(cfgPath).Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            int I(string k, int d) => kv.TryGetValue(k, out var v) && int.TryParse(v, out var x) ? x : d;
            var pt = new Point(I("x", 0), I("y", 0));
            if (Screen.AllScreens.Any(sc => sc.WorkingArea.Contains(pt))) Location = pt;
            TopMost = I("top", 0) == 1; opacity = (byte)Math.Clamp(I("opacity", 255), 40, 255);
            theme = Math.Clamp(I("theme", 0), 0, Themes.Length - 1); bg = Math.Clamp(I("bg", 0), 0, Backgrounds.Length - 1); panelAlpha = Math.Clamp(I("alpha", 210), 0, 255);
            size = Math.Clamp(I("size", 100), 60, 220) / 100f; pinDesktop = I("pin", 0) == 1; fahrenheit = I("f", 0) == 1;
            showThreads = I("threads", 1) == 1; showGraph = I("graph", 1) == 1; showClock = I("clock", 1) == 1; corners = I("corners", 1) == 1;
        }
        catch { }
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath));
            File.WriteAllLines(cfgPath, new[] { $"x={Left}", $"y={Top}", $"top={(TopMost ? 1 : 0)}", $"opacity={opacity}", $"theme={theme}", $"bg={bg}", $"alpha={panelAlpha}",
                $"size={(int)Math.Round(size * 100)}", $"f={(fahrenheit ? 1 : 0)}", $"threads={(showThreads ? 1 : 0)}", $"graph={(showGraph ? 1 : 0)}", $"clock={(showClock ? 1 : 0)}", $"corners={(corners ? 1 : 0)}", $"pin={(pinDesktop ? 1 : 0)}" });
        }
        catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static bool IsStartup() { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("HudWidget") != null; }
    static void ToggleStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (IsStartup()) k.DeleteValue("HudWidget", false); else k.SetValue("HudWidget", "\"" + Environment.ProcessPath + "\"");
    }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
}
