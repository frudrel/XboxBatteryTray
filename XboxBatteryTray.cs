// Xbox controller battery level in the Windows notification area.
// Reads the controller's battery report through Windows.Gaming.Input (works over
// USB, including charge status), the level Windows tracks for Bluetooth controllers
// (also when they're off, as "last known"), and falls back to XInput.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Xbox Battery Tray")]

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var mutex = new System.Threading.Mutex(true, "XboxBatteryTray_SingleInstance", out created))
        {
            if (!created) return;
            Native.SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.Run(new TrayContext());
        }
    }
}

class BatteryStatus
{
    public bool Connected;
    public int Percent = -1;      // -1 = unknown
    public string Name = "Xbox controller";
    public bool Wired;            // plugged in by USB
    public bool Charging;
    public bool Full;             // plugged in and done charging
}

class TrayContext : ApplicationContext
{
    const int PollMs = 30000;
    const string SettingsKey = @"Software\XboxBatteryTray";
    static readonly int[] LowChoices = { 5, 10, 15, 20, 25 };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "XboxBatteryTray";

    readonly NotifyIcon tray = new NotifyIcon();
    readonly Timer timer = new Timer();
    readonly Control invoker = new Control();
    readonly ToolStripMenuItem startupItem;
    readonly ToolStripMenuItem statusItem = new ToolStripMenuItem { Enabled = false };
    readonly ToolStripMenuItem estimateItem = new ToolStripMenuItem { Enabled = false };
    readonly RateTracker drain = new RateTracker("", false);
    readonly RateTracker charge = new RateTracker("Charge", true);
    readonly ToolStripMenuItem lowAlertMenu = new ToolStripMenuItem("Low battery alert");
    Icon currentIcon;
    int lowThreshold = 10;        // 0 = off
    bool lowWarned, fullNotified = true;

    public TrayContext()
    {
        invoker.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(estimateItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh now", null, delegate { Update(); });
        startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
        startupItem.Checked = IsStartupEnabled();
        menu.Items.Add(startupItem);
        LoadLowThreshold();
        foreach (int choice in LowChoices)
        {
            int value = choice;
            lowAlertMenu.DropDownItems.Add(new ToolStripMenuItem("At " + value + "%", null, delegate { SetLowThreshold(value); }) { Tag = value });
        }
        lowAlertMenu.DropDownItems.Add(new ToolStripMenuItem("Off", null, delegate { SetLowThreshold(0); }) { Tag = 0 });
        UpdateLowAlertChecks();
        menu.Items.Add(lowAlertMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, delegate { ExitThread(); });

        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Update(); };

        // When started at login the taskbar may not be ready yet, and Windows silently
        // drops the icon. Re-add it a few times early on, and again after waking up.
        ReAddIconAfter(5, 15, 30, 60, 120);
        SystemEvents.PowerModeChanged += (s, e) =>
        {
            if (e.Mode == PowerModes.Resume)
                try { invoker.BeginInvoke(new MethodInvoker(() => ReAddIconAfter(5, 30))); } catch { }
        };

        // Refresh right away when a controller is plugged in, unplugged or turned on/off.
        BatteryReader.WatchControllers(() =>
        {
            try { invoker.BeginInvoke(new MethodInvoker(() => { System.Threading.Thread.Sleep(500); Update(); })); }
            catch { }
        });

        timer.Interval = PollMs;
        timer.Tick += delegate { Update(); };
        timer.Start();
        Update();
    }

    void Update()
    {
        BatteryStatus st;
        try { st = BatteryReader.Read(); }
        catch { st = new BatteryStatus(); }

        drain.Add(st.Connected && !st.Wired && st.Percent >= 0, st.Percent);
        charge.Add(st.Connected && st.Charging && st.Percent >= 0, st.Percent);

        string text, estimate;
        if (st.Connected && st.Wired)
        {
            if (st.Percent < 0) { text = st.Name + ": plugged in"; estimate = "Battery level unavailable"; }
            else if (st.Full) { text = st.Name + ": fully charged"; estimate = "Ready to unplug"; }
            else if (st.Charging)
            {
                text = st.Name + ": " + st.Percent + "% charging";
                double? h = charge.HoursLeft(st.Percent);
                estimate = h.HasValue ? "Full in about " + FormatHours(h.Value) : "Estimating time to full…";
            }
            else { text = st.Name + ": " + st.Percent + "% (plugged in)"; estimate = "Not charging"; }
        }
        else if (st.Connected && st.Percent >= 0)
        {
            text = st.Name + ": " + st.Percent + "%";
            double? h = drain.HoursLeft(st.Percent);
            estimate = h.HasValue ? "About " + FormatHours(h.Value) + " left" : "Estimating time left…";
        }
        else if (st.Connected) { text = st.Name + ": battery unknown"; estimate = "No estimate"; }
        else if (st.Percent >= 0)
        {
            text = "Not connected (last: " + st.Percent + "%)";
            double? h = drain.HoursLeft(st.Percent);
            estimate = h.HasValue ? "About " + FormatHours(h.Value) + " of play left" : "No estimate yet";
        }
        else { text = "No Xbox controller found"; estimate = "No estimate"; }

        statusItem.Text = text;
        estimateItem.Text = estimate;
        string tip = text + "\n" + estimate;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;

        SetIcon(IconPainter.Draw(st));
        CheckLowBattery(st);

        // Only announce "fully charged" when we saw it charging first.
        if (st.Charging) fullNotified = false;
        if (st.Full && !fullNotified)
        {
            tray.ShowBalloonTip(5000, "Controller charged", st.Name + " is fully charged.", ToolTipIcon.Info);
            fullNotified = true;
        }
        if (!st.Wired) fullNotified = true;
    }

    void ReAddIconAfter(params int[] seconds)
    {
        foreach (int s in seconds)
        {
            var t = new Timer { Interval = s * 1000 };
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                tray.Visible = false;
                tray.Visible = true;
            };
            t.Start();
        }
    }

    // Warns once per discharge; re-arms after charging, a battery swap or a rise well above the threshold.
    void CheckLowBattery(BatteryStatus st)
    {
        if (lowThreshold > 0 && st.Connected && !st.Wired && st.Percent >= 0 && st.Percent <= lowThreshold)
        {
            if (!lowWarned)
            {
                double? h = drain.HoursLeft(st.Percent);
                tray.ShowBalloonTip(10000, "Controller battery low",
                    st.Name + " is at " + st.Percent + "%." + (h.HasValue ? " About " + FormatHours(h.Value) + " left." : "") +
                    " Plug it in or swap the batteries.", ToolTipIcon.Warning);
                lowWarned = true;
            }
        }
        else if (st.Wired || st.Percent > lowThreshold + 5)
        {
            lowWarned = false;
        }
    }

    void LoadLowThreshold()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
            {
                object v = k != null ? k.GetValue("LowAlert") : null;
                if (v is int) lowThreshold = (int)v;
            }
        }
        catch { }
    }

    void SetLowThreshold(int value)
    {
        lowThreshold = value;
        lowWarned = false;
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                k.SetValue("LowAlert", value, RegistryValueKind.DWord);
        }
        catch { }
        UpdateLowAlertChecks();
        Update();
    }

    void UpdateLowAlertChecks()
    {
        foreach (ToolStripMenuItem item in lowAlertMenu.DropDownItems)
            item.Checked = (int)item.Tag == lowThreshold;
    }

    static string FormatHours(double hours)
    {
        int minutes = (int)Math.Round(hours * 60);
        if (minutes < 60) return Math.Max(1, minutes) + " min";
        if (minutes < 600) return (minutes / 60) + "h " + (minutes % 60) + "m";
        return (int)Math.Round(hours) + " hours";
    }

    void SetIcon(Icon icon)
    {
        tray.Icon = icon;
        if (currentIcon != null)
        {
            Native.DestroyIcon(currentIcon.Handle);
            currentIcon.Dispose();
        }
        currentIcon = icon;
    }

    static bool IsStartupEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(RunName) != null;
    }

    void ToggleStartup()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (IsStartupEnabled()) key.DeleteValue(RunName, false);
            else key.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
        }
        startupItem.Checked = IsStartupEnabled();
    }

    protected override void ExitThreadCore()
    {
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        base.ExitThreadCore();
    }
}

static class IconPainter
{
    static readonly Color Green = Color.FromArgb(70, 210, 90);
    static readonly Color Yellow = Color.FromArgb(240, 200, 40);
    static readonly Color Red = Color.FromArgb(240, 70, 60);
    static readonly Color Blue = Color.FromArgb(80, 170, 255);
    static readonly Color Grey = Color.FromArgb(140, 140, 140);
    static readonly Color BoltColor = Color.FromArgb(255, 220, 50);
    const int LowColor = 20;

    public static Icon Draw(BatteryStatus st)
    {
        int size = SystemInformation.SmallIconSize.Width;
        using (var bmp = new Bitmap(size, size))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            if (st.Connected && st.Wired && st.Percent < 0)
            {
                // Plugged in, but no level available: just a big bolt.
                DrawBolt(g, new RectangleF(size * 0.15f, 0, size * 0.7f, size), BoltColor, size);
                return Icon.FromHandle(bmp.GetHicon());
            }

            Color color;
            string label;
            if (!st.Connected) { color = Grey; label = st.Percent >= 0 ? st.Percent.ToString() : "–"; }
            else if (st.Percent < 0) { color = Color.White; label = "?"; }
            else if (st.Full) { color = Green; label = "F"; }
            else if (st.Charging) { color = Color.White; label = st.Percent >= 100 ? "F" : st.Percent.ToString(); }
            else
            {
                color = st.Percent > 50 ? Green : st.Percent > LowColor ? Yellow : Red;
                label = st.Percent >= 100 ? "F" : st.Percent.ToString();
            }

            // Full: the Xbox logo instead of a number.
            bool logo = label == "F" && Logo != null;
            if (logo) color = Green;

            // Charging: a large bolt behind the number.
            if (st.Connected && st.Wired && !logo)
                DrawBolt(g, new RectangleF(size * 0.12f, 0, size * 0.76f, size), Color.FromArgb(230, BoltColor), size);

            // Fill bar along the bottom edge.
            int barH = Math.Max(2, size / 8);
            if (st.Percent >= 0)
            {
                using (var bg = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
                    g.FillRectangle(bg, 0, size - barH, size, barH);
                using (var fill = new SolidBrush(st.Charging ? Blue : color))
                    g.FillRectangle(fill, 0, size - barH, Math.Max(1, size * Math.Min(st.Percent, 100) / 100), barH);
            }

            if (logo)
            {
                int side = size - barH - 1;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(Logo, new Rectangle((size - side) / 2, 0, side, side));
                return Icon.FromHandle(bmp.GetHicon());
            }

            // Number, sized to fill the remaining space.
            float fontPx = label.Length >= 2 ? size * 0.72f : size * 0.85f;
            using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var path = new GraphicsPath())
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                var rect = new RectangleF(-2, -1, size + 4, size - barH + 1);
                path.AddString(label, font.FontFamily, (int)font.Style, fontPx, rect, sf);
                using (var outline = new Pen(Color.FromArgb(200, 0, 0, 0), Math.Max(1.5f, size / 10f)) { LineJoin = LineJoin.Round })
                    g.DrawPath(outline, path);
                using (var brush = new SolidBrush(color))
                    g.FillPath(brush, path);
            }

            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    // Embedded by build.cmd; its alpha channel is used as a stencil and filled green.
    static readonly Bitmap Logo = LoadLogo();

    static Bitmap LoadLogo()
    {
        try
        {
            using (var s = typeof(IconPainter).Assembly.GetManifestResourceStream("XboxLogo.png"))
            {
                if (s == null) return null;
                using (var source = new Bitmap(s))
                    return Tint(source, Green);
            }
        }
        catch { return null; }
    }

    static Bitmap Tint(Bitmap source, Color color)
    {
        var result = new Bitmap(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
                result.SetPixel(x, y, Color.FromArgb(source.GetPixel(x, y).A, color));
        return result;
    }

    static void DrawBolt(Graphics g, RectangleF r, Color color, int size)
    {
        // Lightning bolt in a 0..1 box.
        float[] pts = { 0.62f, 0f, 0.05f, 0.58f, 0.45f, 0.58f, 0.32f, 1f, 0.95f, 0.38f, 0.55f, 0.38f, 0.75f, 0f };
        var p = new PointF[pts.Length / 2];
        for (int i = 0; i < p.Length; i++) p[i] = new PointF(r.X + pts[i * 2] * r.Width, r.Y + pts[i * 2 + 1] * r.Height);
        using (var path = new GraphicsPath())
        {
            path.AddPolygon(p);
            using (var outline = new Pen(Color.FromArgb(220, 0, 0, 0), Math.Max(1.5f, size / 12f)) { LineJoin = LineJoin.Round })
                g.DrawPath(outline, path);
            using (var brush = new SolidBrush(color))
                g.FillPath(brush, path);
        }
    }
}

// Learns how fast the level falls (while on battery) or rises (while charging).
// Only time spent in that state counts, and the numbers are kept in the registry
// so the estimate survives restarts. A jump the other way starts a new cycle.
class RateTracker
{
    const string Key = @"Software\XboxBatteryTray";
    const double MaxGapSeconds = 120;      // longer gaps (off, unplugged, PC asleep) aren't counted
    const double MinChange = 3, MinHours = 0.25, TrustChange = 5;

    readonly string prefix;
    readonly bool rising;
    double changed, seconds, learnedRate;  // learnedRate in % per hour, from earlier cycles
    int prevPercent = -1;
    DateTime lastSample = DateTime.MinValue;

    public RateTracker(string prefix, bool rising)
    {
        this.prefix = prefix;
        this.rising = rising;
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(Key))
            {
                if (k == null) return;
                changed = ReadDouble(k, "Dropped");
                seconds = ReadDouble(k, "Seconds");
                learnedRate = ReadDouble(k, "LearnedRate");
                prevPercent = (int)ReadDouble(k, "Prev", -1);
            }
        }
        catch { }
    }

    public void Add(bool active, int percent)
    {
        if (!active) { lastSample = DateTime.MinValue; return; }

        var now = DateTime.UtcNow;
        int delta = rising ? percent - prevPercent : prevPercent - percent;
        if (prevPercent >= 0 && delta < -2)
        {
            changed = 0; seconds = 0; prevPercent = percent;
        }
        else if (prevPercent < 0)
        {
            prevPercent = percent;
        }
        else
        {
            double gap = (now - lastSample).TotalSeconds;
            if (lastSample != DateTime.MinValue && gap > 0 && gap <= MaxGapSeconds) seconds += gap;
            if (delta > 0) { changed += delta; prevPercent = percent; }
        }
        lastSample = now;

        if (changed >= TrustChange && seconds / 3600 >= MinHours) learnedRate = CycleRate();
        Save();
    }

    public double? HoursLeft(int percent)
    {
        double rate = 0;
        bool cycleUsable = changed >= MinChange && seconds / 3600 >= MinHours;
        if (cycleUsable && (changed >= TrustChange || learnedRate <= 0)) rate = CycleRate();
        else if (learnedRate > 0) rate = learnedRate;
        if (rate <= 0) return null;
        return (rising ? 100 - percent : percent) / rate;
    }

    double CycleRate() { return changed / (seconds / 3600); }

    void Save()
    {
        try
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                k.SetValue(prefix + "Dropped", changed.ToString("R", inv));
                k.SetValue(prefix + "Seconds", seconds.ToString("R", inv));
                k.SetValue(prefix + "LearnedRate", learnedRate.ToString("R", inv));
                k.SetValue(prefix + "Prev", prevPercent.ToString(inv));
            }
        }
        catch { }
    }

    double ReadDouble(RegistryKey k, string name, double fallback = 0)
    {
        double v;
        var s = k.GetValue(prefix + name) as string;
        return s != null && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? v : fallback;
    }
}

static class BatteryReader
{
    // Battery level Windows stores on Bluetooth device nodes (what Settings > Bluetooth shows).
    static readonly Native.DEVPROPKEY BatteryKey = new Native.DEVPROPKEY(new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);
    static readonly Native.DEVPROPKEY IsConnectedKey = new Native.DEVPROPKEY(new Guid("83DA6326-97A6-4088-9453-A1923F573B29"), 15);
    static readonly Native.DEVPROPKEY FriendlyNameKey = new Native.DEVPROPKEY(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    public static void WatchControllers(Action changed)
    {
        try { GamingInput.Watch(changed); }
        catch { }   // Windows.Gaming.Input unavailable; polling still works
    }

    public static BatteryStatus Read()
    {
        // USB: Windows.Gaming.Input gives the level and charge status.
        BatteryStatus gi = ReadGamingInput(false);
        if (gi != null) return gi;

        // Bluetooth: use the level Windows shows in Settings. Windows.Gaming.Input
        // misreports Bluetooth controllers (e.g. 100/1000 when the controller is full).
        BatteryStatus best = null;
        foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
        {
            foreach (var st in ReadBluetooth(enumerator))
            {
                if (best == null || (st.Connected && !best.Connected)) best = st;
            }
        }
        if (best != null && best.Connected) return best;

        // Xbox Wireless Adapter: no Bluetooth device, so fall back to the other APIs.
        gi = ReadGamingInput(true);
        if (gi != null) return gi;

        var xi = ReadXInput();
        if (xi != null) return xi;
        return best ?? new BatteryStatus();
    }

    static BatteryStatus ReadGamingInput(bool wireless)
    {
        try { return GamingInput.Read(wireless); }
        catch { return null; }
    }

    static System.Collections.Generic.IEnumerable<BatteryStatus> ReadBluetooth(string enumerator)
    {
        var list = new System.Collections.Generic.List<BatteryStatus>();
        IntPtr set = Native.SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
        if (set == Native.INVALID_HANDLE_VALUE) return list;
        try
        {
            var data = new Native.SP_DEVINFO_DATA();
            data.cbSize = Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
            for (int i = 0; Native.SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                byte[] nameBuf = GetProp(set, ref data, FriendlyNameKey);
                if (nameBuf == null) continue;
                string name = System.Text.Encoding.Unicode.GetString(nameBuf).TrimEnd('\0');
                if (name.IndexOf("Xbox", StringComparison.OrdinalIgnoreCase) < 0) continue;

                byte[] batt = GetProp(set, ref data, BatteryKey);
                if (batt == null || batt.Length < 1) continue;
                byte[] conn = GetProp(set, ref data, IsConnectedKey);

                list.Add(new BatteryStatus
                {
                    Name = name,
                    Percent = batt[0],
                    Connected = conn != null && conn.Length > 0 && conn[0] != 0,
                });
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return list;
    }

    static byte[] GetProp(IntPtr set, ref Native.SP_DEVINFO_DATA data, Native.DEVPROPKEY key)
    {
        int type, required;
        var k = key;
        Native.SetupDiGetDevicePropertyW(set, ref data, ref k, out type, null, 0, out required, 0);
        if (required <= 0) return null;
        var buf = new byte[required];
        if (!Native.SetupDiGetDevicePropertyW(set, ref data, ref k, out type, buf, buf.Length, out required, 0)) return null;
        return buf;
    }

    static BatteryStatus ReadXInput()
    {
        for (int i = 0; i < 4; i++)
        {
            var state = new byte[16];
            try { if (Native.XInputGetState(i, state) != 0) continue; }
            catch (DllNotFoundException) { return null; }

            var info = new byte[2];
            if (Native.XInputGetBatteryInformation(i, 0, info) != 0) continue;
            var st = new BatteryStatus { Connected = true, Name = "Xbox controller " + (i + 1) };
            switch (info[0])
            {
                case 1: st.Wired = true; break;                      // BATTERY_TYPE_WIRED
                case 2: case 3:                                      // ALKALINE / NIMH
                    st.Percent = new[] { 5, 33, 66, 100 }[Math.Min((int)info[1], 3)]; break;
            }
            return st;
        }
        return null;
    }
}

// Kept in its own class so a missing WinRT type only fails these calls.
static class GamingInput
{
    public static void Watch(Action changed)
    {
        Windows.Gaming.Input.Gamepad.GamepadAdded += (s, e) => changed();
        Windows.Gaming.Input.Gamepad.GamepadRemoved += (s, e) => changed();
    }

    public static BatteryStatus Read(bool wireless)
    {
        BatteryStatus best = null;
        foreach (var pad in Windows.Gaming.Input.Gamepad.Gamepads)
        {
            if (pad.IsWireless != wireless) continue;
            var report = pad.TryGetBatteryReport();
            if (report == null) continue;

            var raw = Windows.Gaming.Input.RawGameController.FromGameController(pad);
            var st = new BatteryStatus { Connected = true, Wired = !pad.IsWireless };
            if (raw != null && !string.IsNullOrEmpty(raw.DisplayName))
                st.Name = raw.DisplayName.Replace("Xbox One Game Controller", "Xbox controller");

            int? full = report.FullChargeCapacityInMilliwattHours, remain = report.RemainingCapacityInMilliwattHours;
            var status = report.Status;
            if (status != Windows.System.Power.BatteryStatus.NotPresent && full.HasValue && full.Value > 0 && remain.HasValue)
            {
                st.Percent = Math.Max(0, Math.Min(100, (int)Math.Round(remain.Value * 100.0 / full.Value)));
                st.Charging = status == Windows.System.Power.BatteryStatus.Charging;
                st.Full = st.Wired && (st.Percent >= 100 || (status == Windows.System.Power.BatteryStatus.Idle && st.Percent >= 95));
            }
            else if (!st.Wired)
            {
                continue;   // wireless with no report: let Bluetooth/XInput answer instead
            }

            // Prefer a pad with a battery reading.
            if (best == null || (best.Percent < 0 && st.Percent >= 0)) best = st;
        }
        return best;
    }
}

static class Native
{
    public const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DEVPROPKEY
    {
        public Guid fmtid; public int pid;
        public DEVPROPKEY(Guid g, int p) { fmtid = g; pid = p; }
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string enumerator, IntPtr hwnd, int flags);
    [DllImport("setupapi.dll")]
    public static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll")]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out int propType, byte[] buffer, int bufferSize, out int requiredSize, int flags);

    [DllImport("xinput1_4.dll")] public static extern int XInputGetState(int index, byte[] state);
    [DllImport("xinput1_4.dll")] public static extern int XInputGetBatteryInformation(int index, byte devType, byte[] info);

    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
