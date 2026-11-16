using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;

namespace LenovoSlimControl;

internal enum ZeroTouchFeature { Login, Lock, VideoPlayback, CameraAssist, LeaveAutoAdjust }
internal enum DistanceSensitivity { Automatic, Near, Middle, Far }
internal enum LockDelay { Fast = 1, Medium = 2, Slow = 3 }

internal sealed record ZeroTouchState(
    bool Login,
    bool Lock,
    bool VideoPlayback,
    bool ServiceRunning,
    DistanceSensitivity Distance,
    LockDelay LockDelay,
    int CalibratedDistance,
    bool RpcAvailable,
    string? RpcError,
    ulong Advertised,
    bool? CameraAssist,
    bool? LeaveAutoAdjust,
    int? SensorType,
    int? InterfaceVersion);

internal static class ZeroTouchControl
{
    private const string HpdKey = @"SYSTEM\CurrentControlSet\Services\SmartSense\Parameters\HPD";
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
        var rpcAvailable = VantageCliBackend.TryRead(out var rpc, out var rpcError);
        return new(
            rpc?.WakeOnApproach ?? ReadBool(key, "Approach"),
            rpc?.LockOnLeave ?? ReadBool(key, "PresentLeave"),
            rpc?.PauseVideo ?? ReadInt(key, "Video", 1) == 2,
            LenovoState.IsServiceRunning("SmartSense"),
            rpc?.ApproachAutoAdjust == true ? DistanceSensitivity.Automatic : distance,
            delay,
            ReadInt(key, "AdjustDistance", 0),
            rpcAvailable,
            rpcError,
            rpc?.Advertised ?? unchecked((uint)ReadInt(key, "Capability", 0)),
            rpc?.CameraAssist ?? (ReadInt(key, "CameraDetect", 1) == 2),
            rpc?.LeaveAutoAdjust ?? (ReadInt(key, "LeaveAutoAdjust", 1) == 2),
            rpc?.SensorType ?? ReadNullableInt(key, "SensorType"),
            rpc?.InterfaceVersion ?? ReadNullableInt(key, "Version"));
    }

    internal static void Request(ZeroTouchFeature feature, bool enabled) =>
        RunElevated("--set-zero-touch", feature.ToString(), enabled ? "1" : "0");

    internal static void RequestDistance(DistanceSensitivity distance) =>
        RunElevated("--set-distance", distance.ToString(), string.Empty);

    internal static void RequestLockDelay(LockDelay delay) =>
        RunElevated("--set-lock-delay", ((int)delay).ToString(), string.Empty);

    internal static void ApplyFeature(string featureName, bool enabled)
    {
        switch (featureName)
        {
            case nameof(ZeroTouchFeature.Login):
                VantageCliBackend.SetWakeOnApproach(enabled);
                break;
            case nameof(ZeroTouchFeature.Lock):
                VantageCliBackend.SetLockOnLeave(enabled);
                break;
            case nameof(ZeroTouchFeature.VideoPlayback):
                VantageCliBackend.SetPauseVideo(enabled);
                break;
            case nameof(ZeroTouchFeature.CameraAssist):
                VantageCliBackend.SetCameraAssist(enabled);
                break;
            case nameof(ZeroTouchFeature.LeaveAutoAdjust):
                VantageCliBackend.SetLeaveAutoAdjust(enabled);
                break;
            default:
                Environment.Exit(2);
                break;
        }
    }

    internal static void ApplyDistance(string distanceName)
    {
        if (!Enum.TryParse<DistanceSensitivity>(distanceName, out var distance)) Environment.Exit(2);
        var state = Read();
        VantageCliBackend.SetApproachDistance(distance, state.Login);
    }

    internal static void ApplyLockDelay(string value)
    {
        if (!int.TryParse(value, out var delay) || delay is < 1 or > 3) Environment.Exit(2);
        var state = Read();
        VantageCliBackend.SetLockDelay((LockDelay)delay, state.Lock);
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

    private static bool ReadBool(RegistryKey? key, string name) => ReadInt(key, name, 0) != 0;
    private static int ReadInt(RegistryKey? key, string name, int fallback) => key?.GetValue(name) is int value ? value : fallback;
    private static int? ReadNullableInt(RegistryKey? key, string name) => key?.GetValue(name) is int value ? value : null;
    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
