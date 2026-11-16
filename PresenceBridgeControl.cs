using Microsoft.Win32;

namespace LenovoSlimControl;

internal enum PresenceDecision { Unknown = 0, Present = 1, Absent = 2 }

internal sealed record PresenceBridgeState(PresenceDecision Decision, bool Online);

internal static class PresenceBridgeControl
{
    private const string StateKey = @"SOFTWARE\GeekModeNoVantage\Presence";

    internal static PresenceBridgeState Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(StateKey, false);
            if (key?.GetValue("State") is not int raw || key.GetValue("HeartbeatUtc") is not long heartbeat)
                return new(PresenceDecision.Unknown, false);
            var age = DateTime.UtcNow - DateTime.FromFileTimeUtc(heartbeat);
            if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(5))
                return new(PresenceDecision.Unknown, false);
            var decision = Enum.IsDefined(typeof(PresenceDecision), raw)
                ? (PresenceDecision)raw : PresenceDecision.Unknown;
            return new(decision, true);
        }
        catch
        {
            return new(PresenceDecision.Unknown, false);
        }
    }
}
