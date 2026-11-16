using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LenovoSlimControl;

internal enum LenovoPowerMode { Unknown, IntelligentCooling, BatterySaving, Performance, Geek }
internal sealed record PowerModeState(LenovoPowerMode Mode, bool Supported, bool GeekSupported, uint RawMode, uint Capabilities);

internal static class PowerModeControl
{
    private const uint IoctlSmartPower = 0x8310213C;
    private const uint GeekCapabilityMask = 0x00020001;
    private static bool geekSetByUs;
    private static long ignoreDispatcherEventsUntil;

    internal static PowerModeState Read()
    {
        try
        {
            using var handle = OpenEnergyDriver();
            var capabilities = Send(handle, 3);
            var raw = Send(handle, 2);
            var extended = Send(handle, 10);
            return new(Decode(raw), (raw & 1) != 0,
                (extended & GeekCapabilityMask) == GeekCapabilityMask, raw, extended);
        }
        catch { return new(LenovoPowerMode.Unknown, false, false, 0, 0); }
    }

    internal static bool NotifyExternalModeChange()
    {
        // Lenovo raises LENOVO_DISPATCHER_EVENT for our own service-control
        // command too. Do not mistake that acknowledgement for an Fn+Q press.
        if (Environment.TickCount64 <= Interlocked.Read(ref ignoreDispatcherEventsUntil)) return false;
        geekSetByUs = false;
        return true;
    }

    internal static void Set(LenovoPowerMode mode)
    {
        if (mode == LenovoPowerMode.Unknown) return;
        if (mode == LenovoPowerMode.Geek && SystemInformation.PowerStatus.PowerLineStatus != PowerLineStatus.Online)
            throw new InvalidOperationException("Geek Mode requires the charger.");

        var state = Read();
        if (!state.Supported) throw new InvalidOperationException("Lenovo EnergyDrv did not expose power modes.");
        if (mode == LenovoPowerMode.Geek && !state.GeekSupported)
            throw new InvalidOperationException("Lenovo EnergyDrv did not advertise Geek Mode.");

        // Modern IdeaPads expose the same Lenovo Dispatcher messages Vantage
        // uses. This keeps firmware, Fn+Q and Vantage's displayed state in sync.
        if (TrySetViaDispatcher(mode))
        {
            geekSetByUs = mode == LenovoPowerMode.Geek;
            Interlocked.Exchange(ref ignoreDispatcherEventsUntil, Environment.TickCount64 + 1500);
            Thread.Sleep(250);
            return;
        }

        using var handle = OpenEnergyDriver();
        if (state.GeekSupported || geekSetByUs) Send(handle, 0x000F100B);
        geekSetByUs = false;

        foreach (var function in new uint[] { 15, 8, 7, 6, 5, 11 })
            Send(handle, BuildSet(function, 0xF, false));
        if (mode == LenovoPowerMode.IntelligentCooling) Send(handle, 0x000001FF);
        Send(handle, mode switch
        {
            LenovoPowerMode.IntelligentCooling => BuildSet(5, 0xF, true),
            LenovoPowerMode.BatterySaving => BuildSet(11, 3, true),
            LenovoPowerMode.Performance or LenovoPowerMode.Geek => BuildSet(11, 2, true),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        });
        if (mode == LenovoPowerMode.Geek)
        {
            Send(handle, 0x001F100B);
            Send(handle, 0x001F100B);
            geekSetByUs = true;
        }
    }

    private static bool TrySetViaDispatcher(LenovoPowerMode mode)
    {
        var control = mode switch
        {
            LenovoPowerMode.IntelligentCooling => 163,
            LenovoPowerMode.BatterySaving => 164,
            LenovoPowerMode.Performance => 165,
            LenovoPowerMode.Geek => 172,
            _ => 0
        };
        if (control == 0) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "sc.exe"),
                Arguments = $"control LenovoProcessManagement {control}",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    private static LenovoPowerMode Decode(uint raw)
    {
        if ((raw & 1) == 0) return LenovoPowerMode.Unknown;
        var function = (raw >> 8) & 0xF;
        var parameter = (raw >> 12) & 0xF;
        if (geekSetByUs && (function != 11 || parameter != 2)) geekSetByUs = false;
        if (geekSetByUs && function == 11 && parameter == 2) return LenovoPowerMode.Geek;
        if (function == 11 && parameter == 3) return LenovoPowerMode.BatterySaving;
        if (function == 11 && parameter == 2) return LenovoPowerMode.Performance;
        return LenovoPowerMode.IntelligentCooling;
    }

    private static uint BuildSet(uint function, uint parameter, bool enabled) =>
        1U | (function << 12) | (parameter << 16) | (enabled ? 0x100000U : 0U);

    private static SafeFileHandle OpenEnergyDriver()
    {
        var handle = CreateFile(@"\\.\EnergyDrv", 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open Lenovo EnergyDrv");
        return handle;
    }

    private static uint Send(SafeFileHandle handle, uint command)
    {
        if (!DeviceIoControl(handle, IoctlSmartPower, ref command, sizeof(uint), out var output,
                sizeof(uint), out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"EnergyDrv command 0x{command:X8} failed");
        Thread.Sleep(25);
        return output;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode,
        ref uint input, int inputSize, out uint output, int outputSize, out int returned, IntPtr overlapped);
}
