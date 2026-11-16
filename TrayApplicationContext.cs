using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LenovoSlimControl;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip menu = new();
    private readonly DashboardForm dashboard;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 3000 };
    private readonly PowerModeWatcher powerWatcher;
    private bool menuOpen;
    private bool? lastTofOnline;

    internal TrayApplicationContext()
    {
        dashboard = new DashboardForm(RefreshState);
        powerWatcher = new PowerModeWatcher(() =>
        {
            PowerModeControl.NotifyExternalModeChange();
            if (!menuOpen && dashboard.IsHandleCreated)
                dashboard.BeginInvoke(async () => { await Task.Delay(300); RefreshState(); });
        });
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
        tray.DoubleClick += (_, _) => ShowDashboard();
        menu.Opening += (_, _) => menuOpen = true;
        menu.Closed += (_, _) =>
        {
            menuOpen = false;
            RefreshState();
        };
        timer.Tick += (_, _) =>
        {
            if (!menuOpen) RefreshState();
        };
        timer.Start();
        powerWatcher.Start();
        RefreshState();
    }

    private void RefreshState()
    {
        var zero = ZeroTouchControl.Read();
        var power = PowerModeControl.Read();
        var services = LenovoState.CountServices();
        tray.Text = $"Geek Mode No Vantage — ToF {(zero.ServiceRunning ? "online" : "offline")} — {ModeName(power.Mode)}";
        if (lastTofOnline != zero.ServiceRunning)
        {
            var oldIcon = tray.Icon;
            tray.Icon = CreateStatusIcon(zero.ServiceRunning ? Color.FromArgb(48, 190, 105) : Color.Gray);
            oldIcon?.Dispose();
            lastTofOnline = zero.ServiceRunning;
        }

        menu.Items.Clear();
        menu.Items.Add(new ToolStripMenuItem(zero.ServiceRunning ? "● ToF sensor online" : "○ ToF sensor offline") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        AddZeroTouch("Zero Touch Login", ZeroTouchFeature.Login, zero.Login);
        AddZeroTouch("Zero Touch Lock", ZeroTouchFeature.Lock, zero.Lock);
        AddZeroTouch("Zero Touch Video Playback", ZeroTouchFeature.VideoPlayback, zero.VideoPlayback);
        menu.Items.Add(new ToolStripSeparator());
        var modes = new ToolStripMenuItem($"Power mode: {ModeName(power.Mode)}") { Enabled = power.Supported };
        AddMode(modes, "Intelligent Cooling", LenovoPowerMode.IntelligentCooling, power);
        AddMode(modes, "Battery Saving", LenovoPowerMode.BatterySaving, power);
        AddMode(modes, "Performance", LenovoPowerMode.Performance, power);
        AddMode(modes, "Geek", LenovoPowerMode.Geek, power, power.GeekSupported);
        menu.Items.Add(modes);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add($"Lenovo services: {services.Running}/{services.Total} running", null, (_, _) => ShowDashboard());
        menu.Items.Add("Open", null, (_, _) => ShowDashboard());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        dashboard.UpdateState(zero, power, services);
    }

    private void AddZeroTouch(string text, ZeroTouchFeature feature, bool value)
    {
        var item = new ToolStripMenuItem(text) { Checked = value, CheckOnClick = false };
        item.Click += (_, _) => ChangeZeroTouch(feature, !value);
        menu.Items.Add(item);
    }

    private void AddMode(ToolStripMenuItem parent, string text, LenovoPowerMode mode,
        PowerModeState state, bool enabled = true)
    {
        var item = new ToolStripMenuItem(text) { Checked = state.Mode == mode, Enabled = enabled };
        item.Click += (_, _) => ChangeMode(mode);
        parent.DropDownItems.Add(item);
    }

    private void ChangeZeroTouch(ZeroTouchFeature feature, bool enabled)
    {
        try { ZeroTouchControl.Request(feature, enabled); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ChangeMode(LenovoPowerMode mode)
    {
        try { PowerModeControl.Set(mode); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowDashboard()
    {
        RefreshState();
        dashboard.Show();
        dashboard.WindowState = FormWindowState.Normal;
        dashboard.Activate();
    }

    private static void ShowError(Exception ex) =>
        MessageBox.Show(ex.Message, "Geek Mode No Vantage", MessageBoxButtons.OK, MessageBoxIcon.Error);

    private static string ModeName(LenovoPowerMode mode) => mode switch
    {
        LenovoPowerMode.IntelligentCooling => "Intelligent Cooling",
        LenovoPowerMode.BatterySaving => "Battery Saving",
        LenovoPowerMode.Performance => "Performance",
        LenovoPowerMode.Geek => "Geek",
        _ => "Unknown"
    };

    private static Icon CreateStatusIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        using var pen = new Pen(Color.White, 2);
        graphics.FillEllipse(brush, 3, 3, 26, 26);
        graphics.DrawEllipse(pen, 4, 4, 24, 24);
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    protected override void ExitThreadCore()
    {
        timer.Stop();
        powerWatcher.Dispose();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        dashboard.Dispose();
        base.ExitThreadCore();
    }
}
