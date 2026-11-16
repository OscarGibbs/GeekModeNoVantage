using System.Diagnostics;

namespace LenovoSlimControl;

internal static class LenovoState
{
    internal static bool IsServiceRunning(string serviceName) =>
        RunSc($"query {serviceName}").Contains("RUNNING", StringComparison.OrdinalIgnoreCase);

    internal static (int Running, int Total) CountServices()
    {
        var output = RunSc("query state= all");
        var total = 0;
        var running = 0;
        var isLenovo = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                var name = line["SERVICE_NAME:".Length..].Trim();
                isLenovo = name.Contains("Lenovo", StringComparison.OrdinalIgnoreCase) ||
                           name is "SmartSense" or "ImControllerService";
            }
            else if (line.StartsWith("DISPLAY_NAME:", StringComparison.OrdinalIgnoreCase))
            {
                isLenovo |= line.Contains("Lenovo", StringComparison.OrdinalIgnoreCase);
                if (isLenovo) total++;
            }
            else if (isLenovo && line.StartsWith("STATE", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) running++;
                isLenovo = false;
            }
        }
        return (running, total);
    }

    private static string RunSc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (process is null) return string.Empty;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5_000);
        return output;
    }
}
