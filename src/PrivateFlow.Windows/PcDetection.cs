using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// Reads the facts the Models section's recommendation rests on from this PC only; nothing is sent anywhere. The GPU's memory comes
// from nvidia-smi, the tool whose presence decides, in the app and in setup-engine.ps1 alike, whether there is an NVIDIA GPU.
internal static class PcDetection
{
    public static async Task<PcFacts> DetectAsync()
    {
        var nvidia = SpeechSetup.HasNvidiaGpu;
        var (graphics, total, free) = nvidia ? await NvidiaAsync() : (null, null, null);
        var drive = Path.GetPathRoot(Settings.Root) ?? Settings.Root;
        long diskFree;
        try { diskFree = new DriveInfo(drive).AvailableFreeSpace; } catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { diskFree = 0; }
        return new(graphics ?? DisplayAdapter() ?? "Unknown", nvidia, total, free, ProcessorName(), Environment.ProcessorCount, InstalledMemory(), drive.TrimEnd('\\'), diskFree);
    }
    private static async Task<(string? Name, long? Total, long? Free)> NvidiaAsync()
    {
        try
        {
            var start = new ProcessStartInfo("nvidia-smi", "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            var parts = output.Split('\n')[0].Split(',', StringSplitOptions.TrimEntries);
            return parts.Length == 3 && long.TryParse(parts[1], out var total) && long.TryParse(parts[2], out var free)
                ? (parts[0], total * 1024 * 1024, free * 1024 * 1024) : (parts[0].Length > 0 ? parts[0] : null, null, null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return (null, null, null); }
    }
    private static string ProcessorName()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown";
    }
    private static long InstalledMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) ? (long)status.TotalPhysical : 0;
    }
    private static string? DisplayAdapter()
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        return EnumDisplayDevices(null, 0, ref device, 0) && device.String.Length > 0 ? device.String : null;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string String;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice displayDevice, uint flags);
}
