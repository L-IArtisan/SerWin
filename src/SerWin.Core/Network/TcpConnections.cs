using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using SerWin.Core.Interop;

namespace SerWin.Core.Network;

/// <summary>Portée d'une adresse distante.</summary>
public enum AddressScope
{
    /// <summary>La machine elle-même (127.0.0.1, ::1).</summary>
    Loopback,
    /// <summary>Réseau local : box, NAS, imprimante, autres PC (adresses privées, lien local).</summary>
    LocalNetwork,
    /// <summary>Internet.</summary>
    Internet,
}

/// <summary>Connexion TCP établie vers une adresse distante.</summary>
/// <param name="OwnerModule">Nom du module propriétaire : le nom du service pour un svchost, l'exécutable sinon.</param>
/// <param name="Since">Heure d'ouverture de la connexion, si Windows la fournit.</param>
public sealed record TcpConnection(
    int ProcessId,
    string? OwnerModule,
    IPAddress RemoteAddress,
    int RemotePort,
    int LocalPort,
    DateTime? Since)
{
    public AddressScope Scope => NetAddress.Classify(RemoteAddress);

    /// <summary>Identité stable d'une connexion entre deux relevés (un même couple d'extrémités peut être réutilisé).</summary>
    public string Key => $"{ProcessId}|{RemoteAddress}|{RemotePort}|{LocalPort}|{Since?.Ticks}";
}

public static class NetAddress
{
    /// <summary>Ramène une adresse IPv4 encapsulée en IPv6 (::ffff:a.b.c.d) à sa forme IPv4.</summary>
    public static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public static AddressScope Classify(IPAddress address)
    {
        var a = Normalize(address);
        if (IPAddress.IsLoopback(a)) return AddressScope.Loopback;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] switch
            {
                10 => AddressScope.LocalNetwork,
                172 when b[1] is >= 16 and <= 31 => AddressScope.LocalNetwork,
                192 when b[1] == 168 => AddressScope.LocalNetwork,
                169 when b[1] == 254 => AddressScope.LocalNetwork,          // lien local (APIPA)
                100 when b[1] is >= 64 and <= 127 => AddressScope.LocalNetwork, // CGNAT, souvent la box de l'opérateur
                0 => AddressScope.Loopback,
                _ => AddressScope.Internet,
            };
        }
        if (a.IsIPv6LinkLocal || a.IsIPv6SiteLocal) return AddressScope.LocalNetwork;
        var bytes = a.GetAddressBytes();
        if ((bytes[0] & 0xFE) == 0xFC) return AddressScope.LocalNetwork; // fc00::/7 (adresses uniques locales)
        if (a.Equals(IPAddress.IPv6Any)) return AddressScope.Loopback;
        return AddressScope.Internet;
    }
}

/// <summary>Lecture des connexions TCP établies (IPv4 et IPv6).</summary>
public static class TcpConnections
{
    public static List<TcpConnection> ReadEstablished()
    {
        var list = new List<TcpConnection>();
        Read(NetNative.AF_INET, list);
        Read(NetNative.AF_INET6, list);
        return list;
    }

    private static void Read(int family, List<TcpConnection> list)
    {
        var size = 0;
        var buffer = IntPtr.Zero;
        try
        {
            // La table peut grossir entre deux appels : on réessaie quelques fois.
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var rc = NetNative.GetExtendedTcpTable(buffer, ref size, false, family, NetNative.TCP_TABLE_OWNER_MODULE_CONNECTIONS, 0);
                if (rc == NetNative.NO_ERROR && buffer != IntPtr.Zero) break;
                if (rc != NetNative.ERROR_INSUFFICIENT_BUFFER && buffer != IntPtr.Zero) return;
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                size += 4096;
                buffer = Marshal.AllocHGlobal(size);
            }
            if (buffer == IntPtr.Zero) return;

            var count = Marshal.ReadInt32(buffer);
            var rowSize = family == NetNative.AF_INET ? NetNative.Row4Size : NetNative.Row6Size;
            if (NetNative.TableHeaderSize + (long)count * rowSize > size) return;
            for (var i = 0; i < count; i++)
            {
                var row = buffer + NetNative.TableHeaderSize + i * rowSize;
                var c = family == NetNative.AF_INET ? ParseRow4(row) : ParseRow6(row);
                if (c is not null) list.Add(c);
            }
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    private static TcpConnection? ParseRow4(IntPtr row)
    {
        if (Marshal.ReadInt32(row, 0) != NetNative.MIB_TCP_STATE_ESTAB) return null;
        var localPort = Port(Marshal.ReadInt32(row, 8));
        var remote = new IPAddress((uint)Marshal.ReadInt32(row, 12));
        var remotePort = Port(Marshal.ReadInt32(row, 16));
        var pid = Marshal.ReadInt32(row, 20);
        var since = FileTime(Marshal.ReadInt64(row, 24));
        return new TcpConnection(pid, OwnerModule(row, ipv6: false), remote, remotePort, localPort, since);
    }

    private static TcpConnection? ParseRow6(IntPtr row)
    {
        if (Marshal.ReadInt32(row, 48) != NetNative.MIB_TCP_STATE_ESTAB) return null;
        var localPort = Port(Marshal.ReadInt32(row, 20));
        var bytes = new byte[16];
        Marshal.Copy(row + 24, bytes, 0, 16);
        var remote = NetAddress.Normalize(new IPAddress(bytes));
        var remotePort = Port(Marshal.ReadInt32(row, 44));
        var pid = Marshal.ReadInt32(row, 52);
        var since = FileTime(Marshal.ReadInt64(row, 56));
        return new TcpConnection(pid, OwnerModule(row, ipv6: true), remote, remotePort, localPort, since);
    }

    /// <summary>Les ports sont stockés en ordre réseau dans les 16 bits de poids faible.</summary>
    internal static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static DateTime? FileTime(long ft)
    {
        if (ft <= 0) return null;
        try
        {
            return DateTime.FromFileTime(ft);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>Nom du module propriétaire (service hébergé par svchost, sinon exécutable). Null si Windows le refuse.</summary>
    private static string? OwnerModule(IntPtr row, bool ipv6)
    {
        var size = 0;
        _ = ipv6
            ? NetNative.GetOwnerModuleFromTcp6Entry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, IntPtr.Zero, ref size)
            : NetNative.GetOwnerModuleFromTcpEntry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, IntPtr.Zero, ref size);
        if (size <= 0) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            var rc = ipv6
                ? NetNative.GetOwnerModuleFromTcp6Entry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buffer, ref size)
                : NetNative.GetOwnerModuleFromTcpEntry(row, NetNative.TCPIP_OWNER_MODULE_INFO_BASIC, buffer, ref size);
            return rc == NetNative.NO_ERROR ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer)) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
