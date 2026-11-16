using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

if (args.Contains("--watch-events", StringComparer.OrdinalIgnoreCase))
{
    const string preLockName = @"Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_PreLockEvent";
    const string cancelLockName = @"Global\8EB97783-4442-41CC-B0E0-85B79FE8BBA5_CancelLockEvent";
    var secondsIndex = Array.FindIndex(args, value => value.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
    var seconds = secondsIndex >= 0 && secondsIndex + 1 < args.Length && int.TryParse(args[secondsIndex + 1], out var parsed)
        ? parsed : 60;
    var logIndex = Array.FindIndex(args, value => value.Equals("--log", StringComparison.OrdinalIgnoreCase));
    using var log = logIndex >= 0 && logIndex + 1 < args.Length
        ? new StreamWriter(Path.GetFullPath(args[logIndex + 1]), append: false) { AutoFlush = true }
        : null;
    var output = (TextWriter?)log ?? Console.Out;
    using var preLock = OpenQueryEvent(preLockName);
    using var cancelLock = OpenQueryEvent(cancelLockName);
    var deadline = DateTime.UtcNow.AddSeconds(seconds);
    output.WriteLine($"{DateTimeOffset.Now:O} watching Lenovo presence decisions for {seconds}s");
    var previousPreLock = QueryEvent(preLock);
    var previousCancelLock = QueryEvent(cancelLock);
    using var hpdKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SmartSense\Parameters\HPD");
    var previousDistance = hpdKey?.GetValue("AdjustDistance") as int?;
    output.WriteLine($"{DateTimeOffset.Now:O} initial PRE_LOCK={previousPreLock.State} ({previousPreLock.Kind}) " +
                     $"CANCEL_LOCK={previousCancelLock.State} ({previousCancelLock.Kind}) ADJUST_DISTANCE={previousDistance}");
    while (DateTime.UtcNow < deadline)
    {
        Thread.Sleep(50);
        var currentPreLock = QueryEvent(preLock);
        var currentCancelLock = QueryEvent(cancelLock);
        var currentDistance = hpdKey?.GetValue("AdjustDistance") as int?;
        if (currentPreLock.State != previousPreLock.State)
            output.WriteLine($"{DateTimeOffset.Now:O} PRE_LOCK={currentPreLock.State}");
        if (currentCancelLock.State != previousCancelLock.State)
            output.WriteLine($"{DateTimeOffset.Now:O} CANCEL_LOCK={currentCancelLock.State}");
        if (currentDistance != previousDistance)
            output.WriteLine($"{DateTimeOffset.Now:O} ADJUST_DISTANCE={currentDistance}");
        previousPreLock = currentPreLock;
        previousCancelLock = currentCancelLock;
        previousDistance = currentDistance;
    }
    output.WriteLine($"{DateTimeOffset.Now:O} event watch finished");
    return 0;
}

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: HpdProbe <Lenovo.Vantage.SmartSenseRpcClient.dll>");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
Environment.CurrentDirectory = assemblyDirectory;
NativeMethods.SetDllDirectory(assemblyDirectory);
AppDomain.CurrentDomain.AssemblyResolve += (_, eventArgs) =>
{
    var dependency = new AssemblyName(eventArgs.Name).Name + ".dll";
    var candidate = Path.Combine(assemblyDirectory, dependency);
    return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
};

var assembly = Assembly.LoadFrom(assemblyPath);
Console.WriteLine($"Assembly: {assembly.FullName}");
var callbackCount = 0;
if (args.Contains("--watch", StringComparer.OrdinalIgnoreCase))
{
    var duration = TimeSpan.FromSeconds(60);
    var secondsIndex = Array.FindIndex(args, value => value.Equals("--seconds", StringComparison.OrdinalIgnoreCase));
    if (secondsIndex >= 0 && secondsIndex + 1 < args.Length && int.TryParse(args[secondsIndex + 1], out var seconds))
        duration = TimeSpan.FromSeconds(seconds);

    var hpdType = assembly.GetType("SmartSenseHsaRpcClient.HumanPresenceDetection", throwOnError: true)!;
    using var hpd = (IDisposable)Activator.CreateInstance(hpdType)!;
    var updateEvent = hpdType.GetEvent("SettingUpdated")!;
    var handler = BuildHandler(updateEvent.EventHandlerType!, OnSettingUpdated);
    updateEvent.AddEventHandler(hpd, handler);
    Console.WriteLine($"{DateTimeOffset.Now:O} callback watch started for {duration.TotalSeconds:0}s");
    DumpObject("initial", hpdType.GetMethod("GetAllSetting")!.Invoke(hpd, null));
    if (args.Contains("--pulse-distance", StringComparer.OrdinalIgnoreCase))
    {
        var distanceType = assembly.GetType("SmartSenseHsaRpcClient.ApproachDistanceType", throwOnError: true)!;
        var setter = hpdType.GetMethod("SetApproachDistance")!;
        await Task.Delay(1000);
        Console.WriteLine($"{DateTimeOffset.Now:O} setting distance Middle via Lenovo client");
        setter.Invoke(hpd, [Enum.Parse(distanceType, "Middle")]);
        await Task.Delay(2000);
        Console.WriteLine($"{DateTimeOffset.Now:O} restoring distance Far via Lenovo client");
        setter.Invoke(hpd, [Enum.Parse(distanceType, "Far")]);
    }
    await Task.Delay(duration);
    updateEvent.RemoveEventHandler(hpd, handler);
    Console.WriteLine($"{DateTimeOffset.Now:O} callback watch finished; callbacks={callbackCount}");
    return 0;
}

foreach (var type in assembly.GetTypes().OrderBy(type => type.FullName))
{
    Console.WriteLine($"\nTYPE {type.FullName}");
    foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        Console.WriteLine($"  CTOR {constructor}");
    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  METHOD {method}");
    foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  FIELD {field.FieldType.FullName} {field.Name}");
    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  PROPERTY {property.PropertyType.FullName} {property.Name}");
    foreach (var eventInfo in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        Console.WriteLine($"  EVENT {eventInfo.EventHandlerType?.FullName} {eventInfo.Name}");
}
return 0;

void OnSettingUpdated(object? sender, object? setting)
{
    Interlocked.Increment(ref callbackCount);
    DumpObject("callback", setting);
}

static Delegate BuildHandler(Type eventHandlerType, Action<object?, object?> callback)
{
    var invoke = eventHandlerType.GetMethod("Invoke")!;
    var parameters = invoke.GetParameters()
        .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
        .ToArray();
    var body = Expression.Call(
        Expression.Constant(callback),
        callback.GetType().GetMethod("Invoke")!,
        Expression.Convert(parameters[0], typeof(object)),
        Expression.Convert(parameters[1], typeof(object)));
    return Expression.Lambda(eventHandlerType, body, parameters).Compile();
}

static void DumpObject(string label, object? value)
{
    Console.WriteLine($"{DateTimeOffset.Now:O} {label}");
    if (value is null)
    {
        Console.WriteLine("  <null>");
        return;
    }
    foreach (var field in value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"  {field.Name}={field.GetValue(value) ?? "<null>"}");
}

static SafeWaitHandle OpenQueryEvent(string name)
{
    const uint EventQueryState = 0x0001;
    var handle = NativeMethods.OpenEvent(EventQueryState, false, name);
    if (handle.IsInvalid)
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), $"OpenEvent query failed for {name}");
    return handle;
}

static (bool State, string Kind) QueryEvent(SafeWaitHandle handle)
{
    var status = NativeMethods.NtQueryEvent(
        handle.DangerousGetHandle(), 0, out var information,
        Marshal.SizeOf<NativeMethods.EventBasicInformation>(), out _);
    if (status != 0) throw new InvalidOperationException($"NtQueryEvent failed with NTSTATUS 0x{status:X8}");
    return (information.EventState != 0, information.EventType == 0 ? "manual-reset" : "auto-reset");
}

static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct EventBasicInformation
    {
        internal int EventType;
        internal int EventState;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetDllDirectory(string path);

    [DllImport("kernel32.dll", EntryPoint = "OpenEventW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeWaitHandle OpenEvent(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("ntdll.dll")]
    internal static extern int NtQueryEvent(IntPtr eventHandle, int eventInformationClass,
        out EventBasicInformation eventInformation, int eventInformationLength, out int returnLength);
}
