using System.Diagnostics;
using System.Text.Json;

namespace LenovoSlimControl;

internal sealed record HpdRpcState(
    ulong Advertised,
    bool WakeOnApproach,
    bool LockOnLeave,
    bool PauseVideo,
    bool? CameraAssist,
    bool? ApproachAutoAdjust,
    bool? LeaveAutoAdjust,
    int? SensorType,
    int? InterfaceVersion,
    string Source);

/// <summary>
/// Runs cyjin-yl/vantage-cli as a separate AGPL-3.0 program. Keeping the
/// native NDR/RPC implementation out of this process also contains RPC faults.
/// </summary>
internal static class VantageCliBackend
{
    private static readonly object CacheLock = new();
    private static HpdRpcState? cachedState;
    private static string? cachedError;
    private static DateTime cacheTime;

    private static string Executable => Path.Combine(
        AppContext.BaseDirectory, "third_party", "vantage-cli", "vantage.exe");

    internal static bool TryRead(out HpdRpcState? state, out string? error)
    {
        lock (CacheLock)
        {
            if (DateTime.UtcNow - cacheTime < TimeSpan.FromSeconds(5))
            {
                state = cachedState;
                error = cachedError;
                return state is not null;
            }
        }
        try
        {
            var json = Run(["--json", "sense", "status"]);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            state = new HpdRpcState(
                GetUInt64(root, "advertised") ?? 0,
                GetBool(root, "wake_on_approach") ?? false,
                GetBool(root, "lock_on_leave") ?? false,
                GetBool(root, "pause_video") ?? false,
                GetBool(root, "camera_assist"),
                GetBool(root, "approach_auto_adjust"),
                GetBool(root, "leave_auto_adjust"),
                GetInt32(root, "sensor_type"),
                GetInt32(root, "interface_version"),
                GetString(root, "source") ?? "hpd-rpc");
            error = null;
            StoreCache(state, null);
            return true;
        }
        catch (Exception ex)
        {
            state = null;
            error = ex.Message;
            StoreCache(null, error);
            return false;
        }
    }

    internal static void SetWakeOnApproach(bool enabled) =>
        Mutate("sense", "wake-on-approach", Toggle(enabled));

    internal static void SetLockOnLeave(bool enabled) =>
        Mutate("sense", "lock-on-leave", Toggle(enabled));

    internal static void SetPauseVideo(bool enabled) =>
        Mutate("sense", "pause-video", Toggle(enabled));

    internal static void SetCameraAssist(bool enabled) =>
        Mutate("sense", "camera-assist", Toggle(enabled));

    internal static void SetLeaveAutoAdjust(bool enabled) =>
        Mutate("sense", "leave-auto-adjust", Toggle(enabled));

    internal static void SetApproachDistance(DistanceSensitivity distance, bool wakeEnabled)
    {
        if (distance == DistanceSensitivity.Automatic)
        {
            Mutate("sense", "approach-auto-adjust", "on");
            return;
        }

        Mutate("sense", "approach-auto-adjust", "off");
        var raw = distance switch
        {
            DistanceSensitivity.Near => "0",
            DistanceSensitivity.Middle => "1",
            DistanceSensitivity.Far => "2",
            _ => throw new ArgumentOutOfRangeException(nameof(distance))
        };
        Mutate("sense", "wake-on-approach", Toggle(wakeEnabled), "--distance", raw);
    }

    internal static void SetLockDelay(LockDelay delay, bool lockEnabled) =>
        Mutate("sense", "lock-on-leave", Toggle(lockEnabled), "--wait", ((int)delay - 1).ToString());

    private static void Mutate(params string[] arguments)
    {
        Run([.. arguments, "--yes", "--json"]);
        lock (CacheLock) cacheTime = default;
    }

    private static string Run(IReadOnlyList<string> arguments)
    {
        if (!File.Exists(Executable))
            throw new FileNotFoundException(
                "The SmartSense RPC backend is missing. Reinstall Geek Mode No Vantage.", Executable);

        var start = new ProcessStartInfo
        {
            FileName = Executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start the SmartSense RPC backend.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The SmartSense RPC request timed out.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        return output;
    }

    private static string Toggle(bool enabled) => enabled ? "on" : "off";
    private static void StoreCache(HpdRpcState? state, string? error)
    {
        lock (CacheLock)
        {
            cachedState = state;
            cachedError = error;
            cacheTime = DateTime.UtcNow;
        }
    }
    private static bool? GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
    private static int? GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static ulong? GetUInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetUInt64(out var number) ? number : null;
    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
