using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GeekModeNoVantage.PresenceBridge;

internal enum PresenceDecision { Unknown = 0, Present = 1, Absent = 2 }

internal static class Program
{
    private const string ServiceName = "GeekModeNoVantagePresence";

    private static int Main(string[] args)
    {
        if (args.Contains("--console", StringComparer.OrdinalIgnoreCase))
        {
            var seconds = ReadSeconds(args, 10);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            PresenceMonitor.Run(cancellation.Token, (state, pre, cancel) =>
                Console.WriteLine($"{DateTimeOffset.Now:O} state={state} preLock={pre} cancelLock={cancel}"));
            return 0;
        }

        return NativeService.Run(ServiceName, PresenceMonitor.Run);
    }

    private static int ReadSeconds(string[] args, int fallback)
    {
        var index = Array.FindIndex(args, value => value.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var seconds)
            ? Math.Clamp(seconds, 1, 3600) : fallback;
    }
}

internal static class PresenceMonitor
{
    private const string PreLockName = @"Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_PreLockEvent";
    private const string CancelLockName = @"Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_CancelLockEvent";
    private const string StateKey = @"SOFTWARE\GeekModeNoVantage\Presence";

    internal static void Run(CancellationToken cancellation, Action<PresenceDecision, bool, bool>? observed = null)
    {
        PresenceDecision lastState = (PresenceDecision)(-1);
        var lastHeartbeat = DateTime.MinValue;
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using var preLock = OpenQueryEvent(PreLockName);
                using var cancelLock = OpenQueryEvent(CancelLockName);
                while (!cancellation.IsCancellationRequested)
                {
                    var pre = QueryEvent(preLock);
                    var cancel = QueryEvent(cancelLock);
                    var state = (!pre, cancel) switch
                    {
                        (true, true) => PresenceDecision.Present,
                        (false, false) => PresenceDecision.Absent,
                        _ => PresenceDecision.Unknown
                    };
                    var now = DateTime.UtcNow;
                    if (state != lastState || now - lastHeartbeat >= TimeSpan.FromSeconds(1))
                    {
                        WriteState(state, now);
                        if (state != lastState) observed?.Invoke(state, pre, cancel);
                        lastState = state;
                        lastHeartbeat = now;
                    }
                    cancellation.WaitHandle.WaitOne(100);
                }
            }
            catch when (!cancellation.IsCancellationRequested)
            {
                WriteState(PresenceDecision.Unknown, DateTime.UtcNow);
                lastState = PresenceDecision.Unknown;
                cancellation.WaitHandle.WaitOne(1000);
            }
        }
        WriteState(PresenceDecision.Unknown, DateTime.UtcNow);
    }

    private static void WriteState(PresenceDecision state, DateTime timestamp)
    {
        using var key = Registry.LocalMachine.CreateSubKey(StateKey, writable: true);
        key?.SetValue("State", (int)state, RegistryValueKind.DWord);
        key?.SetValue("HeartbeatUtc", timestamp.ToFileTimeUtc(), RegistryValueKind.QWord);
    }

    private static SafeWaitHandle OpenQueryEvent(string name)
    {
        var handle = NativeMethods.OpenEvent(0x0001, false, name); // EVENT_QUERY_STATE
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenEvent failed for {name}");
        return handle;
    }

    private static bool QueryEvent(SafeWaitHandle handle)
    {
        var status = NativeMethods.NtQueryEvent(handle.DangerousGetHandle(), 0, out var information,
            Marshal.SizeOf<NativeMethods.EventBasicInformation>(), out _);
        if (status != 0) throw new InvalidOperationException($"NtQueryEvent failed: 0x{status:X8}");
        return information.EventState != 0;
    }
}

internal static class NativeService
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int ServiceStartPending = 2;
    private const int ServiceStopPending = 3;
    private const int ServiceRunning = 4;
    private const int ServiceStopped = 1;
    private const int ServiceAcceptStop = 1;
    private const int ServiceAcceptShutdown = 4;
    private static readonly NativeMethods.ServiceMainFunction ServiceMainDelegate = ServiceMain;
    private static readonly NativeMethods.HandlerFunction HandlerDelegate = Handler;
    private static CancellationTokenSource? cancellation;
    private static IntPtr statusHandle;
    private static Action<CancellationToken, Action<PresenceDecision, bool, bool>?>? worker;
    private static string serviceName = string.Empty;

    internal static int Run(string name, Action<CancellationToken, Action<PresenceDecision, bool, bool>?> serviceWorker)
    {
        serviceName = name;
        worker = serviceWorker;
        var table = new[]
        {
            new NativeMethods.ServiceTableEntry { Name = name, ServiceMain = ServiceMainDelegate },
            new NativeMethods.ServiceTableEntry()
        };
        if (!NativeMethods.StartServiceCtrlDispatcher(table))
            return Marshal.GetLastWin32Error();
        return 0;
    }

    private static void ServiceMain(int argumentCount, IntPtr arguments)
    {
        statusHandle = NativeMethods.RegisterServiceCtrlHandlerEx(serviceName, HandlerDelegate, IntPtr.Zero);
        if (statusHandle == IntPtr.Zero) return;
        SetStatus(ServiceStartPending, 0);
        cancellation = new CancellationTokenSource();
        SetStatus(ServiceRunning, ServiceAcceptStop | ServiceAcceptShutdown);
        try { worker!(cancellation.Token, null); }
        finally
        {
            SetStatus(ServiceStopPending, 0);
            SetStatus(ServiceStopped, 0);
        }
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        if (control is 1 or 5) cancellation?.Cancel(); // STOP or SHUTDOWN
        return 0;
    }

    private static void SetStatus(int currentState, int acceptedControls)
    {
        var status = new NativeMethods.ServiceStatus
        {
            ServiceType = ServiceWin32OwnProcess,
            CurrentState = currentState,
            ControlsAccepted = acceptedControls
        };
        NativeMethods.SetServiceStatus(statusHandle, ref status);
    }
}

internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventBasicInformation { internal int EventType; internal int EventState; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ServiceTableEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] internal string? Name;
        internal ServiceMainFunction? ServiceMain;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceStatus
    {
        internal int ServiceType;
        internal int CurrentState;
        internal int ControlsAccepted;
        internal int Win32ExitCode;
        internal int ServiceSpecificExitCode;
        internal int CheckPoint;
        internal int WaitHint;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void ServiceMainFunction(int argumentCount, IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int HandlerFunction(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool StartServiceCtrlDispatcher([In] ServiceTableEntry[] serviceTable);
    [DllImport("advapi32.dll", EntryPoint = "RegisterServiceCtrlHandlerExW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr RegisterServiceCtrlHandlerEx(string serviceName, HandlerFunction handler, IntPtr context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetServiceStatus(IntPtr statusHandle, ref ServiceStatus status);
    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle OpenEvent(uint desiredAccess, bool inheritHandle, string name);
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryEvent(IntPtr eventHandle, int informationClass,
        out EventBasicInformation information, int informationLength, out int returnLength);
}
