using System.Runtime.InteropServices;

namespace SerWin.Core.Interop;

/// <summary>
/// iphlpapi : table des connexions TCP avec processus propriétaire et module (nom du service pour un svchost).
/// Lecture seule, sans droits administrateur.
/// </summary>
internal static class NetNative
{
    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    public const int TCP_TABLE_OWNER_MODULE_CONNECTIONS = 7;
    public const int TCPIP_OWNER_MODULE_INFO_BASIC = 0;
    public const uint NO_ERROR = 0;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const int MIB_TCP_STATE_ESTAB = 5;

    /// <summary>MIB_TCPROW_OWNER_MODULE : 160 octets (6 DWORD, LARGE_INTEGER aligné, 16 ULONGLONG).</summary>
    public const int Row4Size = 160;

    /// <summary>MIB_TCP6ROW_OWNER_MODULE : 192 octets.</summary>
    public const int Row6Size = 192;

    /// <summary>Les lignes commencent après dwNumEntries, aligné sur 8 octets.</summary>
    public const int TableHeaderSize = 8;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    public const int TCP_TABLE_OWNER_MODULE_LISTENER = 6;
    public const int UDP_TABLE_OWNER_MODULE = 2;
    public const int MIB_TCP_STATE_LISTEN = 2;

    /// <summary>MIB_UDPROW_OWNER_MODULE : 160 octets ; MIB_UDP6ROW_OWNER_MODULE : 176 octets.</summary>
    public const int Udp4RowSize = 160;
    public const int Udp6RowSize = 176;

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, bool bOrder, int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetOwnerModuleFromUdpEntry(IntPtr pUdpEntry, int infoClass, IntPtr pBuffer, ref int pdwSize);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetOwnerModuleFromUdp6Entry(IntPtr pUdpEntry, int infoClass, IntPtr pBuffer, ref int pdwSize);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetOwnerModuleFromTcpEntry(IntPtr pTcpEntry, int infoClass, IntPtr pBuffer, ref int pdwSize);

    [DllImport("iphlpapi.dll", SetLastError = false)]
    public static extern uint GetOwnerModuleFromTcp6Entry(IntPtr pTcpEntry, int infoClass, IntPtr pBuffer, ref int pdwSize);
}
