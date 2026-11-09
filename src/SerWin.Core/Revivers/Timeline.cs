using System.Text.Json.Serialization;
using SerWin.Core.Actions;
using SerWin.Core.Events;
using SerWin.Core.Services;

namespace SerWin.Core.Revivers;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClueKind
{
    /// <summary>La modification a été faite par SerWin (entrée d'historique au même moment).</summary>
    SerWin,
    /// <summary>Une tâche planifiée s'est exécutée juste avant.</summary>
    Task,
    /// <summary>Un autre service a changé au même moment (lot de modifications, réparateur…).</summary>
    OtherService,
    /// <summary>Une mise à jour Windows a été installée peu avant.</summary>
    WindowsUpdate,
    /// <summary>Un nouveau service a été installé au même moment.</summary>
    Installation,
}

/// <summary>Indice de corrélation temporelle : une cause possible, pas une preuve (la preuve viendra d'ETW en v2).</summary>
public sealed record Clue(ClueKind Kind, DateTime At, string Subject, string? Detail = null);

public sealed record TimelineEntry(ServiceEvent Event, List<Clue> Clues);

/// <summary>Chronologie « qui l'a réellement relancé ? » (spec §5.5) par corrélation temporelle.</summary>
public static class TimelineBuilder
{
    /// <summary>Fenêtre de corrélation : 2 min avant → 10 s après l'événement.</summary>
    public static readonly TimeSpan Before = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan After = TimeSpan.FromSeconds(10);
    /// <summary>Les mises à jour Windows réactivent souvent des services jusqu'au redémarrage qui suit.</summary>
    public static readonly TimeSpan UpdateWindow = TimeSpan.FromHours(2);
    private static readonly TimeSpan SerWinTolerance = TimeSpan.FromSeconds(15);

    public static List<TimelineEntry> Build(
        ServiceInfo service,
        IReadOnlyList<ServiceEvent> allEvents,
        IReadOnlyList<HistoryEntry> history,
        bool includeTaskRuns,
        Func<DateTime, DateTime, IReadOnlyList<TaskRunEvent>>? taskRuns = null,
        Func<DateTime, DateTime, IReadOnlyList<UpdateEvent>>? updates = null,
        int max = 50)
    {
        taskRuns ??= (f, t) => ServiceEventReader.ReadTaskRuns(f, t);
        updates ??= (f, t) => ServiceEventReader.ReadUpdateInstalls(f, t);

        var result = new List<TimelineEntry>();
        foreach (var e in allEvents.Where(e => e.Concerns(service)).OrderByDescending(e => e.At).Take(max))
        {
            var clues = new List<Clue>();

            // SerWin lui-même ?
            foreach (var h in history)
            {
                if ((h.At.LocalDateTime - e.At).Duration() > SerWinTolerance) continue;
                if (h.Changes.Any(c => c.Service.Equals(service.Name, StringComparison.OrdinalIgnoreCase)))
                    clues.Add(new Clue(ClueKind.SerWin, h.At.LocalDateTime, h.Action.ToString(), h.User));
            }

            // Autres services modifiés dans la même fenêtre (lot, réparateur, installation).
            foreach (var o in allEvents)
            {
                if (ReferenceEquals(o, e) || o.Concerns(service)) continue;
                if (o.At < e.At - Before || o.At > e.At + After) continue;
                if (o.Kind == ServiceEventKind.Installed)
                    clues.Add(new Clue(ClueKind.Installation, o.At, o.DisplayName ?? "?", o.ImagePath));
                else if (o.Kind is ServiceEventKind.StartModeChanged or ServiceEventKind.Started)
                    clues.Add(new Clue(ClueKind.OtherService, o.At, o.ServiceName ?? o.DisplayName ?? "?",
                        o.Kind == ServiceEventKind.StartModeChanged ? $"{o.OldMode?.ToString() ?? o.RawOld} → {o.NewMode?.ToString() ?? o.RawNew}" : null));
            }

            if (clues.All(c => c.Kind != ClueKind.SerWin))
            {
                if (includeTaskRuns)
                    foreach (var run in taskRuns(e.At - Before, e.At + After).Where(r => r.EventId == 100).DistinctBy(r => r.TaskPath))
                        clues.Add(new Clue(ClueKind.Task, run.At, run.TaskPath, run.Action));

                foreach (var u in updates(e.At - UpdateWindow, e.At + After).Where(u => u.EventId == 19).DistinctBy(u => u.Title))
                    clues.Add(new Clue(ClueKind.WindowsUpdate, u.At, u.Title));
            }

            result.Add(new TimelineEntry(e, clues.OrderBy(c => c.Kind).ThenByDescending(c => c.At).Take(12).ToList()));
        }
        return result;
    }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RevivalSource
{
    /// <summary>Un service désactivé (par SerWin ou autre) est repassé à un autre type dans le journal.</summary>
    EventLog,
    /// <summary>SerWin a désactivé le service, et il ne l'est plus.</summary>
    SerWinMismatch,
}

/// <summary>Alerte « désactivé puis relancé » (filtre §5.1, page Alertes).</summary>
public sealed record RevivalAlert(string Service, DateTime At, RevivalSource Source, ServiceStartMode? NewMode, string? Account);

public static class RevivalDetector
{
    /// <summary>
    /// Détecte les services réactivés : événement 7040 « Désactivé → autre chose » qui n'est pas dû à SerWin,
    /// ou désactivation faite par SerWin (non annulée) qui ne tient plus.
    /// </summary>
    public static List<RevivalAlert> Detect(
        IReadOnlyList<ServiceEvent> events,
        IReadOnlyList<HistoryEntry> history,
        IReadOnlyCollection<ServiceInfo> current)
    {
        var alerts = new Dictionary<string, RevivalAlert>(StringComparer.OrdinalIgnoreCase);
        var byName = current.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var e in events.Where(e => e.Kind == ServiceEventKind.StartModeChanged
                     && e.OldMode == ServiceStartMode.Disabled && e.NewMode is not null and not ServiceStartMode.Disabled
                     && e.ServiceName is not null))
        {
            var bySerWin = history.Any(h => (h.At.LocalDateTime - e.At).Duration() < TimeSpan.FromSeconds(15)
                && h.Changes.Any(c => c.Service.Equals(e.ServiceName, StringComparison.OrdinalIgnoreCase)));
            if (bySerWin) continue;
            // L'alerte ne vaut que si le service n'a pas été redésactivé depuis.
            if (byName.TryGetValue(e.ServiceName!, out var now) && now.StartMode == ServiceStartMode.Disabled) continue;
            if (!alerts.TryGetValue(e.ServiceName!, out var existing) || existing.At < e.At)
                alerts[e.ServiceName!] = new RevivalAlert(e.ServiceName!, e.At, RevivalSource.EventLog, e.NewMode, e.Account);
        }

        // Dernière décision de SerWin par service (en ignorant les entrées annulées).
        var lastDecision = new Dictionary<string, (DateTime At, string Mode)>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in history.Where(h => !h.IsUndone).OrderBy(h => h.At))
            foreach (var c in h.Changes.Where(c => c.Kind == ChangeKind.StartMode))
                lastDecision[c.Service] = (h.At.LocalDateTime, c.After);

        foreach (var (name, decision) in lastDecision)
        {
            if (decision.Mode != nameof(ServiceStartMode.Disabled)) continue;
            if (!byName.TryGetValue(name, out var now) || now.StartMode == ServiceStartMode.Disabled) continue;
            if (!alerts.ContainsKey(name))
                alerts[name] = new RevivalAlert(name, decision.At, RevivalSource.SerWinMismatch, now.StartMode, null);
        }

        return alerts.Values.OrderByDescending(a => a.At).ToList();
    }
}
