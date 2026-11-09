namespace SerWin.Core.Safety;

/// <summary>
/// Listes de sécurité codées en dur (spec §5.3), indépendantes de la base de connaissances :
/// même un service non documenté de la liste rouge reste bloqué.
/// </summary>
public static class SafetyLists
{
    /// <summary>Liste rouge : blocage absolu, aucune exception, y compris en mode Expert.</summary>
    public static readonly IReadOnlySet<string> RedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "RpcSs", "DcomLaunch", "RpcEptMapper", "LSM", "SamSs", "BrokerInfrastructure",
        "CoreMessagingRegistrar", "Power", "PlugPlay", "ProfSvc", "Schedule", "SystemEventsBroker",
        "gpsvc", "StateRepository", "Winmgmt", "EventSystem", "UserManager", "TimeBrokerSvc",
        "AudioEndpointBuilder", "CryptSvc", "KeyIso", "Appinfo",
    };

    /// <summary>Liste orange : importants mais non vitaux → avertissement renforcé + confirmation.</summary>
    public static readonly IReadOnlySet<string> OrangeServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mpssvc", "BFE", "WinDefend", "WdNisSvc", "SecurityHealthService", "wscsvc", "Sense",
        "EventLog", "Dhcp", "Dnscache", "nsi", "NlaSvc", "netprofm", "Audiosrv", "wuauserv",
        "UsoSvc", "WaaSMedicSvc", "BITS", "TrustedInstaller", "msiserver", "AppXSvc", "ClipSVC",
        "LanmanWorkstation", "Netlogon", "W32Time", "WlanSvc", "Themes", "FontCache", "VaultSvc",
        "TextInputManagementService",
    };

    /// <summary>Processus système dont l'arrêt fait planter ou rend instable Windows.</summary>
    public static readonly IReadOnlySet<string> RedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe",
        "lsass.exe", "LsaIso.exe", "svchost.exe", "fontdrvhost.exe", "dwm.exe", "Memory Compression",
        "MsMpEng.exe",
    };

    public static SafetyLevel ForService(string name)
    {
        if (RedServices.Contains(name)) return SafetyLevel.Red;
        if (OrangeServices.Contains(name)) return SafetyLevel.Orange;
        return SafetyLevel.None;
    }
}

public enum SafetyLevel
{
    None,
    Orange,
    Red,
}
