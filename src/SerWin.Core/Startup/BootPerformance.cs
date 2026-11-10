using System.Diagnostics.Eventing.Reader;

namespace SerWin.Core.Startup;

/// <summary>Un démarrage mesuré par Windows (événement 100).</summary>
/// <param name="BootTime">Durée totale jusqu'à ce que le bureau soit utilisable.</param>
/// <param name="MainPath">Partie avant l'ouverture de session.</param>
/// <param name="PostBoot">Partie après l'ouverture de session (programmes au démarrage).</param>
public sealed record BootMeasure(DateTime At, TimeSpan BootTime, TimeSpan MainPath, TimeSpan PostBoot);

/// <summary>Un programme que Windows a accusé de ralentir le démarrage (événement 101).</summary>
public sealed record BootDegradation(DateTime At, string FileName, string FriendlyName, TimeSpan Total, TimeSpan Degradation);

/// <summary>
/// Journal « Microsoft-Windows-Diagnostics-Performance/Operational » : Windows y mesure chaque démarrage et nomme les
/// programmes qui l'ont ralenti. Lisible uniquement en administrateur.
/// </summary>
public static class BootPerformance
{
    public const string LogName = "Microsoft-Windows-Diagnostics-Performance/Operational";

    /// <summary>Derniers démarrages, du plus récent au plus ancien ; null si le journal est illisible (pas administrateur).</summary>
    public static List<BootMeasure>? ReadBoots(int max = 10)
    {
        var list = new List<BootMeasure>();
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, "*[System[(EventID=100)]]") { ReverseDirection = true });
            using var selector = new EventLogPropertySelector(
            [
                "Event/EventData/Data[@Name='BootTime']",
                "Event/EventData/Data[@Name='MainPathBootTime']",
                "Event/EventData/Data[@Name='BootPostBootTime']",
            ]);
            for (var e = reader.ReadEvent(); e is not null && list.Count < max; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e is not EventLogRecord r) continue;
                    var v = r.GetPropertyValues(selector);
                    list.Add(new BootMeasure(e.TimeCreated ?? DateTime.MinValue, Ms(v[0]), Ms(v[1]), Ms(v[2])));
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Programmes accusés de ralentir le démarrage depuis <paramref name="since"/> ; null si illisible.</summary>
    public static List<BootDegradation>? ReadDegradations(DateTime since)
    {
        var list = new List<BootDegradation>();
        var sinceUtc = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName,
                $"*[System[(EventID=101) and TimeCreated[@SystemTime>='{sinceUtc}']]]") { ReverseDirection = true });
            using var selector = new EventLogPropertySelector(
            [
                "Event/EventData/Data[@Name='FileName']",
                "Event/EventData/Data[@Name='FriendlyName']",
                "Event/EventData/Data[@Name='TotalTime']",
                "Event/EventData/Data[@Name='DegradationTime']",
            ]);
            for (var e = reader.ReadEvent(); e is not null && list.Count < 500; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e is not EventLogRecord r) continue;
                    var v = r.GetPropertyValues(selector);
                    if (v[0] is not string file || string.IsNullOrWhiteSpace(file)) continue;
                    list.Add(new BootDegradation(e.TimeCreated ?? DateTime.MinValue, file.Trim(), (v[1] as string)?.Trim() ?? file, Ms(v[2]), Ms(v[3])));
                }
            }
            return list;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static TimeSpan Ms(object? value) => value switch
    {
        uint u => TimeSpan.FromMilliseconds(u),
        int i => TimeSpan.FromMilliseconds(i),
        ulong ul => TimeSpan.FromMilliseconds(ul),
        long l => TimeSpan.FromMilliseconds(l),
        _ => TimeSpan.Zero,
    };
}
