using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace PrivateFlow.Windows;

// Windows closes this job's handle when Private Flow exits for any reason, including a crash or
// Task Manager, and then ends every helper assigned to it, such as the speech worker and llama-server.
internal static class HelperProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit; public nuint Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputOutputCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public InputOutputCounters InputOutput; public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimits information, int length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    // Never closed: the handle must live exactly as long as this process.
    private static readonly Lazy<nint> Job = new(Create);
    private static nint Create()
    {
        var job = CreateJobObject(0, null);
        if (job == 0) throw new InvalidOperationException("Windows could not create the helper process job.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
        if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
            throw new InvalidOperationException("Windows could not configure the helper process job.");
        return job;
    }
    // Ends the helper when it cannot be tied to Private Flow, so it can never outlive the app.
    public static void Attach(Process process)
    {
        if (AssignProcessToJobObject(Job.Value, process.Handle)) return;
        try { process.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        throw new InvalidOperationException("Windows could not tie the helper process to Private Flow.");
    }
}

internal static class LoopbackListeners
{
    private const int InternetProtocolVersion4 = 2;
    private const int OwnerProcessListenerTable = 3;
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(nint table, ref int size, bool sorted, int addressFamily, int tableClass, uint reserved);
    // Every process listening on 127.0.0.1 (or any IPv4 address) at the port; a port can be shared through SO_REUSEADDR.
    public static IReadOnlyList<int> OwnerProcessIds(int port)
    {
        var size = 0;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            GetExtendedTcpTable(0, ref size, false, InternetProtocolVersion4, OwnerProcessListenerTable, 0);
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, InternetProtocolVersion4, OwnerProcessListenerTable, 0) != 0) continue;
                var count = Marshal.ReadInt32(table);
                var owners = new List<int>();
                // Each row holds six 32-bit fields: state, local address, local port, remote address, remote port, owning process.
                for (var row = 0; row < count; row++)
                {
                    var offset = 4 + row * 24;
                    var address = (uint)Marshal.ReadInt32(table, offset + 4);
                    var rowPort = IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(table, offset + 8)) & 0xFFFF;
                    if (rowPort == port && (address == 0 || address == 0x0100007F)) owners.Add(Marshal.ReadInt32(table, offset + 20));
                }
                return owners;
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        throw new InvalidOperationException("Windows did not list the local listeners.");
    }
}
