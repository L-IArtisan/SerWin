using System.Management;
using System.Net;
using System.Runtime.InteropServices;

namespace SerWin.Core.Network;

/// <summary>
/// Cache DNS de Windows (WMI MSFT_DNSClientCache), lisible sans droits administrateur. Complète le journal DNS
/// pour nommer les adresses récentes ; il peut être vide si un VPN ou le navigateur résout les noms lui-même.
/// </summary>
public static class DnsCache
{
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;

    public static List<(string Name, IPAddress Address)> Read()
    {
        var list = new List<(string, IPAddress)>();
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\StandardCimv2", "SELECT Entry, Data, Type FROM MSFT_DNSClientCache");
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    if (o["Entry"] is not string name || o["Data"] is not string data) continue;
                    var type = o["Type"] is ushort t ? t : (ushort)0;
                    if (type is not (TypeA or TypeAaaa)) continue;
                    if (IPAddress.TryParse(data, out var ip)) list.Add((name.Trim().TrimEnd('.').ToLowerInvariant(), NetAddress.Normalize(ip)));
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            // Cache illisible : on s'en passe.
        }
        return list;
    }
}
