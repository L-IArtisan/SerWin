using System.Diagnostics.Eventing.Reader;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SerWin.Core.Services;

namespace SerWin.Core.Events;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceEventKind
{
    /// <summary>7040 : type de démarrage modifié.</summary>
    StartModeChanged,
    /// <summary>7036 : passé en cours d'exécution (rarement journalisé sous Windows 11).</summary>
    Started,
    /// <summary>7036 : arrêté.</summary>
    Stopped,
    /// <summary>7034 : arrêt inattendu (plantage).</summary>
    Crashed,
    /// <summary>7031 : arrêt inattendu, une action de récupération va être exécutée.</summary>
    CrashedWithRecovery,
    /// <summary>7045 : nouveau service installé.</summary>
    Installed,
}

/// <summary>Événement du Gestionnaire de contrôle des services (journal Système).</summary>
public sealed record ServiceEvent
{
    public DateTime At { get; init; }
    public int EventId { get; init; }
    public ServiceEventKind Kind { get; init; }
    /// <summary>Nom technique (clé SCM) quand l'événement le fournit.</summary>
    public string? ServiceName { get; init; }
    public string? DisplayName { get; init; }
    public ServiceStartMode? OldMode { get; init; }
    public ServiceStartMode? NewMode { get; init; }
    /// <summary>Libellés bruts (traduits par Windows) en cas de type non reconnu.</summary>
    public string? RawOld { get; init; }
    public string? RawNew { get; init; }
    /// <summary>Compte ayant fait la modification (7040) : SID et nom résolu.</summary>
    public string? UserSid { get; init; }
    public string? Account { get; init; }
    /// <summary>7031 : action de récupération (texte Windows) et nombre d'occurrences.</summary>
    public string? RecoveryAction { get; init; }
    public int? CrashCount { get; init; }
    /// <summary>7045 : chemin de l'exécutable installé.</summary>
    public string? ImagePath { get; init; }

    public bool IsBySystem => UserSid is "S-1-5-18" or "S-1-5-19" or "S-1-5-20";

    public bool Concerns(ServiceInfo s) =>
        (ServiceName is not null && ServiceName.Equals(s.Name, StringComparison.OrdinalIgnoreCase))
        || (ServiceName is null && DisplayName is not null && DisplayName.Equals(s.DisplayName, StringComparison.CurrentCultureIgnoreCase))
        || (Kind == ServiceEventKind.Installed && DisplayName is not null
            && (DisplayName.Equals(s.Name, StringComparison.OrdinalIgnoreCase) || DisplayName.Equals(s.DisplayName, StringComparison.CurrentCultureIgnoreCase)));
}

/// <summary>Exécution de tâche planifiée (journal Microsoft-Windows-TaskScheduler/Operational).</summary>
public sealed record TaskRunEvent(DateTime At, int EventId, string TaskPath, string? Action);

/// <summary>Installation de mise à jour Windows (WindowsUpdateClient, journal Système).</summary>
public sealed record UpdateEvent(DateTime At, int EventId, string Title);

/// <summary>Lecture des journaux d'événements utiles à la chronologie « Qui l'a relancé ? » (spec §5.5).</summary>
public static class ServiceEventReader
{
    public const string TaskSchedulerLog = "Microsoft-Windows-TaskScheduler/Operational";

    private const string ScmQuery =
        "*[System[Provider[@Name='Service Control Manager'] and (EventID=7040 or EventID=7036 or EventID=7031 or EventID=7034 or EventID=7045)]]";

    private static readonly Dictionary<string, string?> AccountCache = [];

    /// <summary>Tous les événements SCM du journal Système, du plus récent au plus ancien.</summary>
    public static List<ServiceEvent> ReadServiceEvents(DateTime? since = null)
    {
        var query = since is { } s
            ? ScmQuery[..^2] + $" and TimeCreated[@SystemTime>='{s.ToUniversalTime():o}']]]"
            : ScmQuery;
        var result = new List<ServiceEvent>();
        foreach (var x in Read("System", query))
            if (Parse(x) is { } e) result.Add(e);
        return result;
    }

    public static List<TaskRunEvent> ReadTaskRuns(DateTime from, DateTime to)
    {
        var q = $"*[System[(EventID=100 or EventID=200 or EventID=129) and {TimeRange(from, to)}]]";
        var result = new List<TaskRunEvent>();
        foreach (var x in Read(TaskSchedulerLog, q))
        {
            var data = EventData(x);
            var task = data.GetValueOrDefault("TaskName");
            if (task is null) continue;
            result.Add(new TaskRunEvent(TimeOf(x), IdOf(x), task, data.GetValueOrDefault("ActionName") ?? data.GetValueOrDefault("Path")));
        }
        return result;
    }

    public static List<UpdateEvent> ReadUpdateInstalls(DateTime from, DateTime to)
    {
        var q = $"*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=43) and {TimeRange(from, to)}]]";
        var result = new List<UpdateEvent>();
        foreach (var x in Read("System", q))
            result.Add(new UpdateEvent(TimeOf(x), IdOf(x), EventData(x).GetValueOrDefault("updateTitle") ?? ""));
        return result;
    }

    // ── Journal du Planificateur (désactivé par défaut) ──────
    public static bool? IsTaskSchedulerLogEnabled()
    {
        try
        {
            using var cfg = new EventLogConfiguration(TaskSchedulerLog);
            return cfg.IsEnabled;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Active le journal opérationnel du Planificateur (action admin unique, spec §5.5).</summary>
    public static void EnableTaskSchedulerLog()
    {
        using var cfg = new EventLogConfiguration(TaskSchedulerLog) { IsEnabled = true };
        cfg.SaveChanges();
    }

    // ── Analyse ──────────────────────────────────────────────
    internal static ServiceEvent? Parse(XElement x)
    {
        var id = IdOf(x);
        var p = EventDataList(x);
        string? P(int i) => i < p.Count ? p[i] : null;
        var binaryName = DecodeBinary(x);

        return id switch
        {
            7040 => new ServiceEvent
            {
                At = TimeOf(x), EventId = id, Kind = ServiceEventKind.StartModeChanged,
                DisplayName = P(0), RawOld = P(1), RawNew = P(2), ServiceName = P(3),
                OldMode = ParseStartMode(P(1)), NewMode = ParseStartMode(P(2)),
                UserSid = UserOf(x), Account = ResolveAccount(UserOf(x)),
            },
            7036 => ParseStateChange(x, P(0), P(1), binaryName),
            7034 => new ServiceEvent
            {
                At = TimeOf(x), EventId = id, Kind = ServiceEventKind.Crashed,
                DisplayName = P(0), ServiceName = binaryName, CrashCount = int.TryParse(P(1), out var c) ? c : null,
            },
            7031 => new ServiceEvent
            {
                At = TimeOf(x), EventId = id, Kind = ServiceEventKind.CrashedWithRecovery,
                DisplayName = P(0), ServiceName = binaryName, CrashCount = int.TryParse(P(1), out var c2) ? c2 : null,
                RecoveryAction = P(4),
            },
            7045 => new ServiceEvent
            {
                At = TimeOf(x), EventId = id, Kind = ServiceEventKind.Installed,
                DisplayName = EventData(x).GetValueOrDefault("ServiceName"), ImagePath = EventData(x).GetValueOrDefault("ImagePath"),
                NewMode = ParseStartMode(EventData(x).GetValueOrDefault("StartType")),
                UserSid = UserOf(x), Account = ResolveAccount(UserOf(x)),
            },
            _ => null,
        };
    }

    private static ServiceEvent ParseStateChange(XElement x, string? display, string? stateText, string? binary)
    {
        // Le binaire de 7036 contient « nom/état » (4 = en cours, 1 = arrêté).
        string? name = null;
        bool? running = null;
        if (binary is not null)
        {
            var parts = binary.Split('/');
            name = parts[0];
            if (parts.Length > 1) running = parts[1].Trim() == "4";
        }
        running ??= stateText is not null && (stateText.Contains("cours", StringComparison.OrdinalIgnoreCase) || stateText.Contains("running", StringComparison.OrdinalIgnoreCase));
        return new ServiceEvent
        {
            At = TimeOf(x), EventId = 7036, Kind = running == true ? ServiceEventKind.Started : ServiceEventKind.Stopped,
            DisplayName = display, ServiceName = name,
        };
    }

    /// <summary>
    /// Convertit le libellé traduit par Windows (« Démarrage à la demande », « auto start »…) en type de démarrage.
    /// Retourne null si la langue n'est pas reconnue (le libellé brut est alors affiché).
    /// </summary>
    public static ServiceStartMode? ParseStartMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.ToLowerInvariant();
        if (s.Contains("désactiv") || s.Contains("disabled") || s.Contains("deaktiv") || s.Contains("deshabil") || s.Contains("disabilit")) return ServiceStartMode.Disabled;
        if (s.Contains("demand") || s.Contains("manuel") || s.Contains("manual") || s.Contains("bedarf")) return ServiceStartMode.Manual;
        if (s.Contains("auto")) return ServiceStartMode.Automatic;
        if (s.Contains("boot")) return ServiceStartMode.Boot;
        if (s.Contains("system") || s.Contains("systèm")) return ServiceStartMode.System;
        return null;
    }

    public static string? ResolveAccount(string? sid)
    {
        if (string.IsNullOrEmpty(sid)) return null;
        lock (AccountCache)
        {
            if (AccountCache.TryGetValue(sid, out var cached)) return cached;
            string? name;
            try
            {
                name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            }
            catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException or SystemException)
            {
                name = sid;
            }
            AccountCache[sid] = name;
            return name;
        }
    }

    // ── Outils XML ───────────────────────────────────────────
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static IEnumerable<XElement> Read(string log, string query)
    {
        var q = new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true };
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(q);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? rec;
                try
                {
                    rec = reader.ReadEvent();
                }
                catch (EventLogException)
                {
                    yield break;
                }
                if (rec is null) yield break;
                using (rec)
                {
                    XElement? x = null;
                    try
                    {
                        x = XElement.Parse(rec.ToXml());
                    }
                    catch (Exception ex) when (ex is EventLogException or System.Xml.XmlException)
                    {
                    }
                    if (x is not null) yield return x;
                }
            }
        }
    }

    private static string TimeRange(DateTime from, DateTime to) =>
        $"TimeCreated[@SystemTime>='{from.ToUniversalTime():o}' and @SystemTime<='{to.ToUniversalTime():o}']";

    private static int IdOf(XElement x) => (int?)x.Element(Ns + "System")?.Element(Ns + "EventID") ?? 0;

    private static DateTime TimeOf(XElement x) =>
        DateTime.Parse((string?)x.Element(Ns + "System")?.Element(Ns + "TimeCreated")?.Attribute("SystemTime") ?? "",
            null, System.Globalization.DateTimeStyles.RoundtripKind).ToLocalTime();

    private static string? UserOf(XElement x) => (string?)x.Element(Ns + "System")?.Element(Ns + "Security")?.Attribute("UserID");

    private static List<string?> EventDataList(XElement x) =>
        x.Element(Ns + "EventData")?.Elements(Ns + "Data").Select(d => (string?)d).ToList() ?? [];

    private static Dictionary<string, string?> EventData(XElement x)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in x.Element(Ns + "EventData")?.Elements(Ns + "Data") ?? [])
            if ((string?)e.Attribute("Name") is { } n) d[n] = (string?)e;
        foreach (var e in x.Element(Ns + "UserData")?.Descendants() ?? [])
            if (!e.HasElements) d.TryAdd(e.Name.LocalName, (string?)e);
        return d;
    }

    private static string? DecodeBinary(XElement x)
    {
        var hex = (string?)x.Element(Ns + "EventData")?.Element(Ns + "Binary");
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0) return null;
        try
        {
            var s = Encoding.Unicode.GetString(Convert.FromHexString(hex)).TrimEnd('\0', ' ');
            return s.Length == 0 ? null : s;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
