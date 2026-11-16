namespace LenovoSlimControl;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0].Equals("--probe-power", StringComparison.OrdinalIgnoreCase))
        {
            var state = PowerModeControl.Read();
            File.WriteAllText(args[1],
                $"supported={state.Supported}\ngeekSupported={state.GeekSupported}\nmode={state.Mode}\nraw=0x{state.RawMode:X8}\nextended=0x{state.Capabilities:X8}\n");
            return;
        }

        if (args.Length == 3 && args[0].Equals("--set-zero-touch", StringComparison.OrdinalIgnoreCase))
        {
            ZeroTouchControl.ApplyFeature(args[1], args[2] == "1");
            return;
        }
        if (args.Length == 2 && args[0].Equals("--set-distance", StringComparison.OrdinalIgnoreCase))
        {
            ZeroTouchControl.ApplyDistance(args[1]);
            return;
        }
        if (args.Length == 2 && args[0].Equals("--set-lock-delay", StringComparison.OrdinalIgnoreCase))
        {
            ZeroTouchControl.ApplyLockDelay(args[1]);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}
