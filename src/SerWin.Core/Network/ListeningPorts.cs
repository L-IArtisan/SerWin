using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SerWin.Core.Interop;

namespace SerWin.Core.Network;

/// <summary>Un programme qui attend des connexions (TCP) ou des messages (UDP) sur un port de ce PC.</summary>
/// <param name="OwnerModule">Service exact pour un svchost, exécutable sinon (fourni par Windows).</param>
/// <param name="Addresses">Adresses locales sur lesquelles le port est ouvert.</param>
public sealed record ListeningPort(string Protocol, int Port, int ProcessId, string? OwnerModule, IReadOnlyList<IPAddress> Addresses)
{
    /// <summary>
    /// Joignable depuis le réseau : ouvert sur toutes les interfaces (0.0.0.0, ::) ou sur une adresse non locale.
    /// Le pare-feu de Windows peut tout de même bloquer l'accès.
    /// </summary>
    public bool ReachableFromNetwork => Addresses.Any(a => !IPAddress.IsLoopback(a));

    public string Key => $"{Protocol}|{Port}|{ProcessId}";
}

/// <summary>Ports ouverts en écoute (TCP LISTEN et UDP), sans droits administrateur.</summary>
public static class ListeningPorts
{
    public static List<ListeningPort> Read()
    {
        var raw = new List<(string Proto, IPAddress Addr, int Port, int Pid, string? Module)>();
        ReadTcp(NetNative.AF_INET, raw);
        ReadTcp(NetNative.AF_INET6, raw);
        ReadUdp(NetNative.AF_INET, raw);
        ReadUdp(NetNative.AF_INET6, raw);
        return raw
            .GroupBy(r => (r.Proto, r.Port, r.Pid))
            .Select(g => new ListeningPort(g.Key.Proto, g.Key.Port, g.Key.Pid,
                g.Select(x => x.Module).FirstOrDefault(m => m is not null), g.Select(x => x.Addr).Distinct().ToList()))
            .OrderBy(p => p.Port).ThenBy(p => p.Protocol)
            .ToList();
    }

    /// <summary>Plage des ports RPC dynamiques de Windows (49152-65535).</summary>
    public static bool IsDynamicRpc(int port) => port >= 49152;

    private static void ReadTcp(int family, List<(string, IPAddress, int, int, string?)> list)
    {
        var rowSize = family == NetNative.AF_INET ? NetNative.Row4Size : NetNative.Row6Size;
        WithTable(family, NetNative.TCP_TABLE_OWNER_MODULE_LISTENER, tcp: true, rowSize, row =>
        {
            IPAddress addr;
            int port, pid;
            if (family == NetNative.AF_INET)
            {
                if (Marshal.ReadInt32(row, 0) != NetNative.MIB_TCP_STATE_LISTEN) return;
                addr = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                port = TcpConnections.Port(Marshal.ReadInt32(row, 8));
                pid = Marshal.ReadInt32(row, 20);
            }
            else
            {
                if (Marshal.ReadInt32(row, 48) != NetNative.MIB_TCP_STATE_LISTEN) return;
                var b = new byte[16];
                Marshal.Copy(row, b, 0, 16);
                addr = new IPAddress(b);
                port = TcpConnections.Port(Marshal.ReadInt32(row, 20));
                pid = Marshal.ReadInt32(row, 52);
            }
            list.Add(("TCP", addr, port, pid, Module(row, tcp: true, ipv6: family == NetNative.AF_INET6)));
        });
    }

    private static void ReadUdp(int family, List<(string, IPAddress, int, int, string?)> list)
    {
        var rowSize = family == NetNative.AF_INET ? NetNative.Udp4RowSize : NetNative.Udp6RowSize;
        WithTable(family, NetNative.UDP_TABLE_OWNER_MODULE, tcp: false, rowSize, row =>
        {
            IPAddress addr;
            int port, pid;
            if (family == NetNative.AF_INET)
            {
                addr = new IPAddress((uint)Marshal.ReadInt32(row, 0));
                port = TcpConnections.Port(Marshal.ReadInt32(row, 4));
                pid = Marshal.ReadInt32(row, 8);
            }
            else
            {
                var b = new byte[16];
                Marshal.Copy(row, b, 0, 16);
                addr = new IPAddress(b);
                port = TcpConnections.Port(Marshal.ReadInt32(row, 20));
                pid = Marshal.ReadInt32(row, 24);
            }
            list.Add(("UDP", addr, port, pid, Module(row, tcp: false, ipv6: family == NetNative.AF_INET6)));
        });
    }

    private static void WithTable(int family, int tableClass, bool tcp, int rowSize, Action<IntPtr> onRow)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var rc = tcp
                    ? NetNative.GetExtendedTcpTable(buffer, ref size, false, family, tableClass, 0)
                    : NetNative.GetExtendedUdpTable(buffer, ref size, false, family, tableClass, 0);
                if (rc == NetNative.NO_ERROR && buffer != IntPtr.Zero) break;
                if (rc != NetNative.ERROR_INSUFFICIENT_BUFFER && buffer != IntPtr.Zero) return;
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
            }
            if (buffer == IntPtr.Zero) return;
            var count = Marshal.ReadInt32(buffer);
            if (NetNative.TableHeaderSize + (long)count * rowSize > size) return;
            for (var i = 0; i < count; i++) onRow(buffer + NetNative.TableHeaderSize + i * rowSize);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? Module(IntPtr row, bool tcp, bool ipv6)
    {
        uint Call(IntPtr buf, ref int s) => (tcp, ipv6) switch
        {
            (true, false) => NetNative.GetOwnerModuleFromTcpEntry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buf, ref s),
            (true, true) => NetNative.GetOwnerModuleFromTcp6Entry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buf, ref s),
            (false, false) => NetNative.GetOwnerModuleFromUdpEntry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buf, ref s),
            (false, true) => NetNative.GetOwnerModuleFromUdp6Entry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buf, ref s),
        };
        var size = 0;
        _ = Call(IntPtr.Zero, ref size);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            return Call(buffer, ref size) == NetNative.NO_ERROR ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}

/// <summary>Ports bien connus de Windows : clé de libellé (« Port_445 ») et indication de risque si exposé.</summary>
public static class KnownPorts
{
    private static readonly HashSet<int> Tcp = [21, 22, 23, 80, 135, 139, 443, 445, 2869, 3389, 5040, 5357, 5985, 5986, 7680, 8080];
    private static readonly HashSet<int> Udp = [53, 67, 68, 123, 137, 138, 500, 1900, 2177, 3702, 4500, 5050, 5353, 5355, 7680];

    /// <summary>Ports qui donnent un accès à distance s'ils sont joignables : à surveiller particulièrement.</summary>
    private static readonly HashSet<int> Sensitive = [21, 22, 23, 3389, 5985, 5986];

    public static string? LabelKey(string protocol, int port) =>
        (protocol == "TCP" ? Tcp : Udp).Contains(port) ? $"Port_{protocol}_{port}"
        : ListeningPorts.IsDynamicRpc(port) && protocol == "TCP" ? "Port_TCP_Dynamic" : null;

    public static bool IsSensitive(string protocol, int port) => protocol == "TCP" && Sensitive.Contains(port);

    /// <summary>Toutes les clés de libellés (pour les tests de traduction).</summary>
    public static IEnumerable<string> AllKeys =>
        Tcp.Select(p => $"Port_TCP_{p}").Concat(Udp.Select(p => $"Port_UDP_{p}")).Append("Port_TCP_Dynamic");
}
