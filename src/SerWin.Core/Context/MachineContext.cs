using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SerWin.Core.Revivers;

namespace SerWin.Core.Context;

/// <summary>Valeur d'un fait détecté : nombre ou booléen. Null = inconnu (détection impossible).</summary>
public sealed record Fact(int? Count, string? Detail = null)
{
    public bool? IsPresent => Count is null ? null : Count > 0;
    public static Fact Unknown { get; } = new((int?)null);
    public static Fact Of(bool present, string? detail = null) => new(present ? 1 : 0, detail);
}

/// <summary>
/// Contexte de la machine (spec §5.7) : matériel et usages qui rendent un service utile ou non.
/// Chaque clé correspond à une règle « usefulIf » des fiches.
/// </summary>
public sealed class MachineContext
{
    public static class Keys
    {
        public const string Printers = "printers";
        public const string Bluetooth = "bluetooth";
        public const string Wifi = "wifi";
        public const string Touch = "touch";
        public const string DomainJoined = "domainJoined";
        public const string MdmEnrolled = "mdmEnrolled";
        public const string XboxApps = "xboxApps";
        public const string Hypervisor = "hypervisor";
        public const string Laptop = "laptop";
        public const string Hdd = "hdd";
        public const string Insider = "insider";
        public const string ThirdPartyAntivirus = "thirdPartyAntivirus";
        public const string FaxModem = "faxModem";
        public const string SmartCardReader = "smartCardReader";
        public const string OfflineMaps = "offlineMaps";

        public static readonly IReadOnlyList<string> All =
        [
            Printers, Bluetooth, Wifi, Touch, DomainJoined, MdmEnrolled, XboxApps, Hypervisor, Laptop, Hdd,
            Insider, ThirdPartyAntivirus, FaxModem, SmartCardReader, OfflineMaps,
        ];
    }

    public Dictionary<string, Fact> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Fact this[string key] => Facts.GetValueOrDefault(key) ?? Fact.Unknown;

    /// <summary>Contexte construit à la main (tests).</summary>
    public static MachineContext From(IDictionary<string, Fact> facts)
    {
        var c = new MachineContext();
        foreach (var (k, v) in facts) c.Facts[k] = v;
        return c;
    }

    /// <summary>Détection complète (≈ 1 à 3 s, sans droits administrateur, sans réseau).</summary>
    public static MachineContext Detect(ManagementContext? management = null)
    {
        var c = new MachineContext();
        var pnp = PnpClasses();

        c.Facts[Keys.Printers] = Safe(DetectPrinters);
        c.Facts[Keys.Bluetooth] = pnp is null ? Fact.Unknown : CountPnp(pnp, "Bluetooth", n => !n.Contains("Enumerator", StringComparison.OrdinalIgnoreCase));
        c.Facts[Keys.Wifi] = Safe(DetectWifi);
        c.Facts[Keys.Touch] = Fact.Of(GetSystemMetrics(SM_MAXIMUMTOUCHES) > 0);
        c.Facts[Keys.FaxModem] = pnp is null ? Fact.Unknown : CountPnp(pnp, "Modem", _ => true);
        c.Facts[Keys.SmartCardReader] = pnp is null ? Fact.Unknown : CountPnp(pnp, "SmartCardReader", _ => true);

        management ??= ManagementContext.Detect();
        c.Facts[Keys.DomainJoined] = Fact.Of(management.DomainJoined);
        c.Facts[Keys.MdmEnrolled] = Fact.Of(management.MdmEnrolled);

        var (hypervisor, laptop) = ComputerSystem();
        c.Facts[Keys.Hypervisor] = hypervisor;
        c.Facts[Keys.Laptop] = laptop;
        c.Facts[Keys.Hdd] = Safe(DetectSystemDiskIsHdd);
        c.Facts[Keys.XboxApps] = Safe(DetectXboxApps);
        c.Facts[Keys.Insider] = Safe(DetectInsider);
        c.Facts[Keys.ThirdPartyAntivirus] = Safe(DetectThirdPartyAntivirus);
        c.Facts[Keys.OfflineMaps] = Safe(DetectOfflineMaps);
        return c;
    }

    private static Fact Safe(Func<Fact> detect)
    {
        try
        {
            return detect();
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException
                                       or System.Security.SecurityException or IOException or NetworkInformationException)
        {
            return Fact.Unknown;
        }
    }

    // ── Imprimantes : lues dans le Registre (fonctionne même Spouleur arrêté) ──
    private static readonly string[] VirtualPrinterHints =
        ["PDF", "XPS", "OneNote", "Fax", "Send To", "Envoyer vers", "Document Writer"];

    private static Fact DetectPrinters()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
        if (k is null) return new Fact(0);
        var physical = k.GetSubKeyNames()
            .Where(n => !VirtualPrinterHints.Any(h => n.Contains(h, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return new Fact(physical.Count, string.Join(", ", physical));
    }

    private static Fact DetectWifi()
    {
        // Les adaptateurs virtuels « Wi-Fi Direct » existent sur toute machine Wi-Fi : on ne compte que les cartes réelles.
        var wifi = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new Fact(wifi.Count, string.Join(", ", wifi.Select(w => w.Description)));
    }

    // ── Périphériques Plug and Play ──────────────────────────
    private static List<(string Class, string Name)>? PnpClasses()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PNPClass, Name FROM Win32_PnPEntity WHERE PNPClass='Bluetooth' OR PNPClass='Modem' OR PNPClass='SmartCardReader'");
            var list = new List<(string, string)>();
            foreach (var o in s.Get())
                using (o)
                    list.Add(((string?)o["PNPClass"] ?? "", (string?)o["Name"] ?? ""));
            return list;
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Fact CountPnp(List<(string Class, string Name)> pnp, string cls, Func<string, bool> keep)
    {
        var matches = pnp.Where(p => p.Class.Equals(cls, StringComparison.OrdinalIgnoreCase) && keep(p.Name)).Select(p => p.Name).ToList();
        return new Fact(matches.Count, string.Join(", ", matches.Take(3)));
    }

    // ── Système : hyperviseur, portable ──────────────────────
    private static (Fact Hypervisor, Fact Laptop) ComputerSystem()
    {
        Fact hv = Fact.Unknown, laptop = Fact.Unknown;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT HypervisorPresent, PCSystemType FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
            {
                using (o)
                {
                    if (o["HypervisorPresent"] is bool b) hv = Fact.Of(b);
                    // PCSystemType 2 = mobile (portable / tablette).
                    if (o["PCSystemType"] is ushort t) laptop = Fact.Of(t == 2 || HasBattery());
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            laptop = Fact.Of(HasBattery());
        }
        return (hv, laptop);
    }

    private static bool HasBattery() => GetSystemPowerStatus(out var s) && (s.BatteryFlag & 128) == 0 && s.BatteryFlag != 255;

    // ── Disque système : SSD ou disque dur mécanique ─────────
    private static Fact DetectSystemDiskIsHdd()
    {
        var letter = Path.GetPathRoot(Environment.SystemDirectory)![0];
        uint? disk = null;
        using (var p = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT DiskNumber FROM MSFT_Partition WHERE DriveLetter='{letter}'"))
            foreach (var o in p.Get())
                using (o) disk = Convert.ToUInt32(o["DiskNumber"]);
        if (disk is null) return Fact.Unknown;
        using var d = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", $"SELECT MediaType, FriendlyName FROM MSFT_PhysicalDisk WHERE DeviceId='{disk}'");
        foreach (var o in d.Get())
        {
            using (o)
            {
                // MediaType : 3 = HDD, 4 = SSD, 0 = non spécifié.
                var media = Convert.ToUInt16(o["MediaType"]);
                return media switch
                {
                    3 => new Fact(1, (string?)o["FriendlyName"]),
                    4 => new Fact(0, (string?)o["FriendlyName"]),
                    _ => Fact.Unknown,
                };
            }
        }
        return Fact.Unknown;
    }

    // ── Applications Xbox (Xbox, Gaming Services) ────────────
    private static readonly string[] XboxPackages = ["Microsoft.GamingApp_", "Microsoft.GamingServices_", "Microsoft.XboxApp_"];

    private static Fact DetectXboxApps()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        var found = (k?.GetSubKeyNames() ?? [])
            .Where(n => XboxPackages.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.Split('_')[0]).Distinct().ToList();
        return new Fact(found.Count, string.Join(", ", found));
    }

    // ── Programme Windows Insider ────────────────────────────
    private static Fact DetectInsider()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsSelfHost\Applicability");
        var branch = k?.GetValue("BranchName") as string;
        var enabled = k?.GetValue("EnablePreviewBuilds") is int e && e != 0;
        return Fact.Of(enabled || !string.IsNullOrEmpty(branch), branch);
    }

    // ── Antivirus tiers enregistré auprès du Centre de sécurité ──
    private static Fact DetectThirdPartyAntivirus()
    {
        using var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct");
        var names = new List<string>();
        foreach (var o in s.Get())
            using (o)
                if (o["displayName"] is string n && !n.Contains("Defender", StringComparison.OrdinalIgnoreCase)) names.Add(n);
        return new Fact(names.Count, string.Join(", ", names));
    }

    private static Fact DetectOfflineMaps()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "MapData");
        if (!Directory.Exists(dir)) return new Fact(0);
        // Le dossier contient toujours des journaux et un petit cache : une vraie carte pèse des dizaines de Mo.
        var bytes = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        return new Fact(bytes > 20L * 1024 * 1024 ? 1 : 0, $"{bytes / 1048576} Mo");
    }

    // ── Win32 ────────────────────────────────────────────────
    private const int SM_MAXIMUMTOUCHES = 95;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
