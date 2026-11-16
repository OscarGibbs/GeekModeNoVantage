using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace LenovoSlimControl;

internal enum ZeroTouchFeature { Login, Lock, VideoPlayback }
internal enum DistanceSensitivity { Automatic, Near, Middle, Far }
internal enum LockDelay { Fast = 1, Medium = 2, Slow = 3 }

internal sealed record ZeroTouchState(
    bool Login,
    bool Lock,
    bool VideoPlayback,
    bool ServiceRunning,
    DistanceSensitivity Distance,
    LockDelay LockDelay,
    int CalibratedDistance);

internal static class ZeroTouchControl
{
    private const string HpdKey = @"SYSTEM\CurrentControlSet\Services\SmartSense\Parameters\HPD";
    private const string AwareKey = HpdKey + @"\AWARE";

    internal static ZeroTouchState Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(HpdKey, false);
        var automatic = ReadInt(key, "AutoAdjust", 2) == 2;
        var distanceLevel = ReadInt(key, "ApproachDistance", 2);
        var distance = automatic ? DistanceSensitivity.Automatic : distanceLevel switch
        {
            1 => DistanceSensitivity.Near,
            3 => DistanceSensitivity.Far,
            _ => DistanceSensitivity.Middle
        };
        var delay = ReadInt(key, "LeaveWait", 2) switch
        {
            1 => LockDelay.Fast,
            3 => LockDelay.Slow,
            _ => LockDelay.Medium
        };
        return new(
            ReadBool(key, "Approach"),
            ReadBool(key, "PresentLeave"),
            ReadInt(key, "Video", 1) == 2,
            LenovoState.IsServiceRunning("SmartSense"),
            distance,
            delay,
            ReadInt(key, "AdjustDistance", 0));
    }

    internal static void Request(ZeroTouchFeature feature, bool enabled) =>
        RunElevated("--set-zero-touch", feature.ToString(), enabled ? "1" : "0");

    internal static void RequestDistance(DistanceSensitivity distance) =>
        RunElevated("--set-distance", distance.ToString(), string.Empty);

    internal static void RequestLockDelay(LockDelay delay) =>
        RunElevated("--set-lock-delay", ((int)delay).ToString(), string.Empty);

    internal static void ApplyFeature(string featureName, bool enabled)
    {
        using var key = OpenWritable(HpdKey);
        switch (featureName)
        {
            case nameof(ZeroTouchFeature.Login):
                key.SetValue("Approach", enabled ? 1 : 0, RegistryValueKind.DWord);
                break;
            case nameof(ZeroTouchFeature.Lock):
                key.SetValue("PresentLeave", enabled ? 1 : 0, RegistryValueKind.DWord);
                break;
            case nameof(ZeroTouchFeature.VideoPlayback):
                key.SetValue("Video", enabled ? 2 : 1, RegistryValueKind.DWord);
                using (var aware = OpenWritable(AwareKey))
                    aware.SetValue("Status", enabled ? 2 : 0, RegistryValueKind.DWord);
                break;
            default:
                Environment.Exit(2);
                break;
        }
        RestartSmartSense();
    }

    internal static void ApplyDistance(string distanceName)
    {
        if (!Enum.TryParse<DistanceSensitivity>(distanceName, out var distance)) Environment.Exit(2);
        using var key = OpenWritable(HpdKey);
        if (distance == DistanceSensitivity.Automatic)
        {
            key.SetValue("AutoAdjust", 2, RegistryValueKind.DWord);
        }
        else
        {
            key.SetValue("AutoAdjust", 1, RegistryValueKind.DWord);
            key.SetValue("ApproachDistance", distance switch
            {
                DistanceSensitivity.Near => 1,
                DistanceSensitivity.Middle => 2,
                DistanceSensitivity.Far => 3,
                _ => 2
            }, RegistryValueKind.DWord);
        }
        RestartSmartSense();
    }

    internal static void ApplyLockDelay(string value)
    {
        if (!int.TryParse(value, out var delay) || delay is < 1 or > 3) Environment.Exit(2);
        using var key = OpenWritable(HpdKey);
        key.SetValue("LeaveWait", delay, RegistryValueKind.DWord);
        RestartSmartSense();
    }

    private static void RunElevated(string command, string value, string extra)
    {
        var arguments = $"{command} {value}" + (string.IsNullOrEmpty(extra) ? string.Empty : $" {extra}");
        if (IsAdministrator())
        {
            if (command == "--set-zero-touch") ApplyFeature(value, extra == "1");
            else if (command == "--set-distance") ApplyDistance(value);
            else ApplyLockDelay(value);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath!, Arguments = arguments,
            UseShellExecute = true, Verb = "runas"
        });
        process?.WaitForExit();
        if (process is null || process.ExitCode != 0)
            throw new InvalidOperationException("The SmartSense setting could not be changed.");
    }

    private static RegistryKey OpenWritable(string path) =>
        Registry.LocalMachine.OpenSubKey(path, writable: true) ??
        throw new InvalidOperationException($"SmartSense registry key not found: {path}");
    private static bool ReadBool(RegistryKey? key, string name) => ReadInt(key, name, 0) != 0;
    private static int ReadInt(RegistryKey? key, string name, int fallback) => key?.GetValue(name) is int value ? value : fallback;
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static void RestartSmartSense()
    {
        RunSc("stop SmartSense");
        RunSc("start SmartSense");
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
