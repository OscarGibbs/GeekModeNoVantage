using System.Drawing;

namespace LenovoSlimControl;

internal sealed class DashboardForm : Form
{
    private readonly Label tof = new() { AutoSize = true, Font = new Font("Segoe UI", 14, FontStyle.Bold) };
    private readonly Label zeroTouch = new() { AutoSize = true };
    private readonly Label power = new() { AutoSize = true };
    private readonly Label services = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly Action refresh;

    internal DashboardForm(Action refresh)
    {
        this.refresh = refresh;
        Text = "Geek Mode No Vantage";
        ClientSize = new Size(430, 260);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;

        var title = new Label { Text = "Geek Mode No Vantage", Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true };
        var subtitle = new Label
        {
            Text = "IdeaPad Pro 5 14IAH10 • local controls only",
            ForeColor = SystemColors.GrayText, AutoSize = true
        };
        var hint = new Label
        {
            Text = "Right-click the tray icon to change Zero Touch and power settings.",
            AutoSize = true, MaximumSize = new Size(380, 0)
        };
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(20), FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        layout.Controls.AddRange([title, subtitle, Spacer(8), tof, zeroTouch, power, services, Spacer(8), hint]);
        Controls.Add(layout);
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        Shown += (_, _) => refresh();
    }

    internal void UpdateState(ZeroTouchState z, PowerModeState p, (int Running, int Total) s)
    {
        tof.Text = z.ServiceRunning ? "● ToF sensor online" : "○ ToF sensor offline";
        tof.ForeColor = z.ServiceRunning ? Color.FromArgb(30, 150, 80) : Color.Gray;
        zeroTouch.Text = $"Login {(z.Login ? "On" : "Off")}  •  Lock {(z.Lock ? "On" : "Off")}  •  Video {(z.VideoPlayback ? "On" : "Off")}";
        power.Text = $"Power mode: {p.Mode}" + (p.GeekSupported ? "  •  Geek available" : string.Empty);
        services.Text = $"Lenovo services: {s.Running} of {s.Total} running";
    }

    private static Control Spacer(int height) => new Panel { Width = 1, Height = height };
}
