using System.Management;

namespace LenovoSlimControl;

/// <summary>
/// Lenovo's ACPI dispatcher raises this event when Fn+Q changes the firmware
/// power level. It is especially important for Geek -> Performance because
/// those modes share the same DYTC base-mode readback.
/// </summary>
internal sealed class PowerModeWatcher : IDisposable
{
    private readonly Action changed;
    private ManagementEventWatcher? watcher;

    internal PowerModeWatcher(Action changed) => this.changed = changed;

    internal void Start()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\wmi");
            var query = new WqlEventQuery("SELECT * FROM LENOVO_DISPATCHER_EVENT");
            watcher = new ManagementEventWatcher(scope, query);
            watcher.EventArrived += (_, _) => changed();
            watcher.Start();
        }
        catch
        {
            watcher?.Dispose();
            watcher = null;
        }
    }

    public void Dispose()
    {
        try { watcher?.Stop(); } catch { }
        watcher?.Dispose();
    }
}
