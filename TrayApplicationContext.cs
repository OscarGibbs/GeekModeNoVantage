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
    private readonly Control dispatcher = new();
    private bool menuOpen;
    private bool? lastTofOnline;
    private ZeroTouchState? cachedZero;
    private int zeroRefreshRunning;
    private DateTime lastZeroRefresh;
    private LenovoPowerMode? requestedPowerMode;
    private DateTime requestedPowerModeUntil;
    private bool exiting;

    internal TrayApplicationContext()
    {
        dashboard = new DashboardForm(RefreshState);
        dispatcher.CreateControl();
        powerWatcher = new PowerModeWatcher(() =>
        {
            if (PowerModeControl.NotifyExternalModeChange()) requestedPowerMode = null;
            if (!menuOpen && !exiting)
                dispatcher.BeginInvoke(async () => { await Task.Delay(300); if (!exiting) RefreshState(); });
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
        // The RPC backend is a separate process and must never hold up the tray
        // menu. Only the first snapshot is synchronous; later ones refresh in
        // the background while the last confirmed state remains visible.
        if (cachedZero is null)
        {
            cachedZero = ZeroTouchControl.Read();
            lastZeroRefresh = DateTime.UtcNow;
        }
        var zero = cachedZero;
        var power = PowerModeControl.Read();
        if (requestedPowerMode is { } requested && DateTime.UtcNow < requestedPowerModeUntil)
            power = power with { Mode = requested };
        else
            requestedPowerMode = null;
        var services = LenovoState.CountServices();
        RenderState(zero, power, services);
        QueueZeroTouchRefresh();
    }

    private void RenderState(ZeroTouchState zero, PowerModeState power, (int Running, int Total) services)
    {
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

        if (zero.CameraAssist is not null && (zero.Advertised & 0x40) != 0)
            AddZeroTouch("Camera-assisted lock", ZeroTouchFeature.CameraAssist, zero.CameraAssist.Value);
        if (zero.LeaveAutoAdjust is not null && (zero.Advertised & 0x100) != 0)
            AddZeroTouch("Automatic leave distance", ZeroTouchFeature.LeaveAutoAdjust, zero.LeaveAutoAdjust.Value);

        var distance = new ToolStripMenuItem($"Distance sensitivity: {DistanceName(zero.Distance)}");
        AddDistance(distance, "Automatic", DistanceSensitivity.Automatic, zero.Distance);
        AddDistance(distance, "Near (0.42 m)", DistanceSensitivity.Near, zero.Distance);
        AddDistance(distance, "Middle (0.6 m)", DistanceSensitivity.Middle, zero.Distance);
        AddDistance(distance, "Far (1.2 m)", DistanceSensitivity.Far, zero.Distance);
        menu.Items.Add(distance);

        var lockDelay = new ToolStripMenuItem($"Auto-lock timer: {zero.LockDelay}");
        AddLockDelay(lockDelay, LockDelay.Fast, zero.LockDelay);
        AddLockDelay(lockDelay, LockDelay.Medium, zero.LockDelay);
        AddLockDelay(lockDelay, LockDelay.Slow, zero.LockDelay);
        menu.Items.Add(lockDelay);
        menu.Items.Add(new ToolStripSeparator());
        var sensorName = SensorName(zero.SensorType);
        var rpcText = zero.RpcAvailable
            ? $"SmartSense RPC: connected • {sensorName}"
            : "SmartSense RPC: unavailable (registry read only)";
        var rpcItem = new ToolStripMenuItem(rpcText) { Enabled = false };
        rpcItem.ToolTipText = zero.RpcError ?? $"Interface v{zero.InterfaceVersion?.ToString() ?? "?"}, capabilities 0x{zero.Advertised:X}";
        menu.Items.Add(rpcItem);
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

    private void QueueZeroTouchRefresh()
    {
        if (DateTime.UtcNow - lastZeroRefresh < TimeSpan.FromSeconds(3) ||
            Interlocked.Exchange(ref zeroRefreshRunning, 1) != 0) return;
        lastZeroRefresh = DateTime.UtcNow;
        _ = Task.Run(() =>
        {
            ZeroTouchState? state = null;
            try { state = ZeroTouchControl.Read(); }
            finally
            {
                Interlocked.Exchange(ref zeroRefreshRunning, 0);
                if (state is not null && !exiting)
                {
                    try { dispatcher.BeginInvoke(() =>
                    {
                        cachedZero = state;
                        if (!menuOpen)
                            RenderState(state, PowerModeControl.Read(), LenovoState.CountServices());
                    }); }
                    catch (InvalidOperationException) when (exiting) { }
                }
            }
        });
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
        item.Click += (_, _) =>
        {
            if (!ChangeMode(mode)) return;
            foreach (ToolStripMenuItem sibling in parent.DropDownItems.OfType<ToolStripMenuItem>())
                sibling.Checked = ReferenceEquals(sibling, item);
            parent.Text = $"Power mode: {ModeName(mode)}";
            tray.Text = $"Geek Mode No Vantage — {ModeName(mode)}";
        };
        parent.DropDownItems.Add(item);
    }

    private void AddDistance(ToolStripMenuItem parent, string text, DistanceSensitivity value, DistanceSensitivity current)
    {
        var item = new ToolStripMenuItem(text) { Checked = value == current };
        item.Click += (_, _) =>
        {
            try { ZeroTouchControl.RequestDistance(value); }
            catch (Exception ex) { ShowError(ex); }
        };
        parent.DropDownItems.Add(item);
    }

    private void AddLockDelay(ToolStripMenuItem parent, LockDelay value, LockDelay current)
    {
        var item = new ToolStripMenuItem(value.ToString()) { Checked = value == current };
        item.Click += (_, _) =>
        {
            try { ZeroTouchControl.RequestLockDelay(value); }
            catch (Exception ex) { ShowError(ex); }
        };
        parent.DropDownItems.Add(item);
    }

    private void ChangeZeroTouch(ZeroTouchFeature feature, bool enabled)
    {
        try { ZeroTouchControl.Request(feature, enabled); }
        catch (Exception ex) { ShowError(ex); }
    }

    private bool ChangeMode(LenovoPowerMode mode)
    {
        try
        {
            PowerModeControl.Set(mode);
            requestedPowerMode = mode;
            requestedPowerModeUntil = DateTime.UtcNow.AddSeconds(2);
            return true;
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return false;
        }
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

    private static string DistanceName(DistanceSensitivity distance) => distance switch
    {
        DistanceSensitivity.Near => "Near (0.42 m)",
        DistanceSensitivity.Middle => "Middle (0.6 m)",
        DistanceSensitivity.Far => "Far (1.2 m)",
        _ => "Automatic"
    };

    private static string SensorName(int? sensorType) => sensorType switch
    {
        1 => "VL53L1 ToF",
        2 => "VL53L5 ToF",
        4 => "CVDSP",
        null => "unknown sensor",
        _ => $"sensor type {sensorType}"
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
        exiting = true;
        timer.Stop();
        powerWatcher.Dispose();
        tray.Visible = false;
        tray.Icon?.Dispose();
        tray.Dispose();
        dispatcher.Dispose();
        dashboard.Dispose();
        base.ExitThreadCore();
    }
}
