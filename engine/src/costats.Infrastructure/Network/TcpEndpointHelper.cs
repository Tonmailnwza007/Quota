using System.Net;
using System.Runtime.InteropServices;

namespace costats.Infrastructure.Network;

public static class TcpEndpointHelper
{
    private const int AF_INET = 2; // IPv4
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int MIB_TCP_STATE_LISTEN = 2;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved = 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint State;
        public uint LocalAddr;
        public uint LocalPort;
        public uint RemoteAddr;
        public uint RemotePort;
        public uint OwningPid;
    }

    /// <summary>
    /// Gets all listening TCP loopback/local ports owned by the specified process ID.
    /// </summary>
    public static IReadOnlyList<int> GetListeningPortsForProcess(int targetPid)
    {
        if (targetPid <= 0 || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return Array.Empty<int>();
        }

        var ports = new List<int>();
        int size = 0;

        // First call to determine buffer size
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0)
        {
            return Array.Empty<int>();
        }

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var ret = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (ret != 0)
            {
                return Array.Empty<int>();
            }

            int numEntries = Marshal.ReadInt32(buffer);
            var rowPtr = IntPtr.Add(buffer, 4);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

            for (int i = 0; i < numEntries; i++)
            {
                var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                if (row.OwningPid == targetPid && row.State == MIB_TCP_STATE_LISTEN)
                {
                    // Convert network byte order to host byte order
                    int port = (int)(((row.LocalPort & 0xFF) << 8) | ((row.LocalPort >> 8) & 0xFF));
                    if (!ports.Contains(port))
                    {
                        ports.Add(port);
                    }
                }
                rowPtr = IntPtr.Add(rowPtr, rowSize);
            }
        }
        catch
        {
            // Ignore errors reading table
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return ports;
    }
}
