using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace LenovoSlimControl;

internal enum ZeroTouchFeature { Login, Lock }
internal sealed record ZeroTouchState(bool Login, bool Lock, bool ServiceRunning);

internal static class ZeroTouchControl
{
    private const string HpdKey = @"SYSTEM\CurrentControlSet\Services\SmartSense\Parameters\HPD";

    internal static ZeroTouchState Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(HpdKey, false);
        return new(ReadBool(key, "Approach"), ReadBool(key, "PresentLeave"),
            LenovoState.IsServiceRunning("SmartSense"));
    }

    internal static void Request(ZeroTouchFeature feature, bool enabled)
    {
        var name = feature switch
        {
            ZeroTouchFeature.Login => "Approach",
            ZeroTouchFeature.Lock => "PresentLeave",
            _ => throw new ArgumentOutOfRangeException(nameof(feature))
        };
        if (IsAdministrator()) { Apply(name, enabled); return; }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!,
            Arguments = $"--set-zero-touch {name} {(enabled ? 1 : 0)}",
            UseShellExecute = true,
            Verb = "runas"
        });
        process?.WaitForExit();
        if (process is null || process.ExitCode != 0)
            throw new InvalidOperationException("The SmartSense setting could not be changed.");
    }

    internal static void Apply(string valueName, bool enabled)
    {
        if (valueName is not ("Approach" or "PresentLeave")) Environment.Exit(2);
        using (var key = Registry.LocalMachine.OpenSubKey(HpdKey, writable: true))
        {
            if (key is null) Environment.Exit(3);
            key.SetValue(valueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        }
        RunSc("stop SmartSense");
        RunSc("start SmartSense");
    }

    private static bool ReadBool(RegistryKey? key, string name) => key?.GetValue(name) is int value && value != 0;
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static void RunSc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"), Arguments = arguments,
            UseShellExecute = false, CreateNoWindow = true
        });
        process?.WaitForExit(10_000);
    }
}
