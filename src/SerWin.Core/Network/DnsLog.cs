using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Net;

namespace SerWin.Core.Network;

/// <summary>Réglage d'un journal d'événements : activé et taille maximale. Sérialisé pour l'historique et l'annulation.</summary>
public sealed record EventLogSetting(bool Enabled, long MaxSize)
{
    public string Encode() => $"enabled={Enabled};max={MaxSize.ToString(CultureInfo.InvariantCulture)}";

    public static EventLogSetting? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        bool? enabled = null;
        long? max = null;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "enabled" && bool.TryParse(kv[1], out var e)) enabled = e;
            if (kv[0] == "max" && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) max = m;
        }
        return enabled is { } en && max is { } mx ? new EventLogSetting(en, mx) : null;
    }
}

/// <summary>Requête DNS faite par un programme, relevée dans le journal du client DNS de Windows.</summary>
/// <param name="ProcessId">Processus qui a demandé la résolution (0 si inconnu).</param>
/// <param name="Addresses">Adresses obtenues (IPv4 normalisées).</param>
/// <param name="Aliases">Noms intermédiaires (CNAME), utiles pour reconnaître un hébergeur ou un CDN.</param>
/// <param name="Status">0 = succès ; sinon code d'erreur DNS (9003 = nom inexistant…).</param>
public sealed record DnsQuery(DateTime Time, int ProcessId, string Name, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> Aliases, int Status)
{
    public bool Succeeded => Status == 0;
}

/// <summary>
/// Journal « Microsoft-Windows-DNS-Client/Operational » : désactivé par défaut, lisible sans droits administrateur
/// une fois activé. Chaque événement 3008 indique quel processus a demandé quel nom, et les adresses obtenues :
/// c'est ce qui permet de mettre un nom (« settings-win.data.microsoft.com ») sur une adresse IP.
/// </summary>
public static class DnsLog
{
    public const string LogName = "Microsoft-Windows-DNS-Client/Operational";

    /// <summary>Taille réglée par SerWin à l'activation : ≈ 40 000 requêtes, soit plusieurs jours d'usage courant.</summary>
    public const long RecommendedMaxSize = 20L * 1024 * 1024;

    private const int QueryCompletedEventId = 3008;

    public static EventLogSetting? ReadSetting()
    {
        try
        {
            using var config = new EventLogConfiguration(LogName);
            return new EventLogSetting(config.IsEnabled, config.MaximumSizeInBytes);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Applique un réglage (droits administrateur nécessaires).</summary>
    public static void WriteSetting(EventLogSetting setting)
    {
        using var config = new EventLogConfiguration(LogName);
        // La taille doit être réglée journal désactivé pour être prise en compte immédiatement.
        if (config.MaximumSizeInBytes != setting.MaxSize)
        {
            if (config.IsEnabled)
            {
                config.IsEnabled = false;
                config.SaveChanges();
            }
            config.MaximumSizeInBytes = setting.MaxSize;
        }
        config.IsEnabled = setting.Enabled;
        config.SaveChanges();
    }

    /// <summary>Requêtes terminées depuis <paramref name="since"/>, de la plus récente à la plus ancienne.</summary>
    public static List<DnsQuery> Read(DateTime since, int max = 50_000)
    {
        var list = new List<DnsQuery>();
        var sinceUtc = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var xpath = $"*[System[(EventID={QueryCompletedEventId}) and TimeCreated[@SystemTime>='{sinceUtc}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, xpath) { ReverseDirection = true });
            using var selector = new EventLogPropertySelector(
            [
                "Event/EventData/Data[@Name='QueryName']",
                "Event/EventData/Data[@Name='QueryStatus']",
                "Event/EventData/Data[@Name='QueryResults']",
            ]);
            for (var e = reader.ReadEvent(); e is not null && list.Count < max; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e is not EventLogRecord record) continue;
                    var values = record.GetPropertyValues(selector);
                    var name = (values[0] as string)?.Trim().TrimEnd('.');
                    if (string.IsNullOrEmpty(name)) continue;
                    var status = values[1] switch
                    {
                        uint u => (int)u,
                        int i => i,
                        string s when int.TryParse(s, out var p) => p,
                        _ => -1,
                    };
                    var (addresses, aliases) = ParseResults(values[2] as string);
                    list.Add(new DnsQuery(e.TimeCreated ?? DateTime.MinValue, e.ProcessId ?? 0, name.ToLowerInvariant(), addresses, aliases, status));
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            // Journal absent ou illisible : liste vide, l'interface explique pourquoi.
        }
        return list;
    }

    /// <summary>
    /// Analyse le champ QueryResults, ex. « type:  5 x.trafficmanager.net;::ffff:20.42.65.92;2603:1030::1; ».
    /// Les entrées « type: N nom » sont des alias (CNAME) ; les autres des adresses.
    /// </summary>
    internal static (List<IPAddress> Addresses, List<string> Aliases) ParseResults(string? results)
    {
        var addresses = new List<IPAddress>();
        var aliases = new List<string>();
        if (string.IsNullOrWhiteSpace(results)) return (addresses, aliases);
        foreach (var raw in results.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = raw[5..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) aliases.Add(parts[^1].TrimEnd('.').ToLowerInvariant());
            }
            else if (IPAddress.TryParse(raw, out var ip))
            {
                addresses.Add(NetAddress.Normalize(ip));
            }
        }
        return (addresses, aliases);
    }
}
