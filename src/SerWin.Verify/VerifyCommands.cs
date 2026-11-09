using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SerWin.Core.Events;
using SerWin.Core.Platform;
using SerWin.Core.Revivers;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;
using SerWin.Core.Tasks;
using SerWin.Knowledge;

namespace SerWin.Verify;

/// <summary>Configuration observée d'un service documenté (sortie de « capture »).</summary>
public sealed record ServiceCapture
{
    public required string Name { get; init; }
    public bool Exists { get; init; }
    public string? StartType { get; init; }
    public List<string> Triggers { get; init; } = [];
    public string? Account { get; init; }
    public List<string> Dependencies { get; init; } = [];
    public string? Binary { get; init; }
    public List<string> LinkedTasks { get; init; } = [];
    /// <summary>Contrôles des affirmations de la fiche : vide = tout concorde.</summary>
    public List<string> Discrepancies { get; init; } = [];
}

public sealed record CaptureFile
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public required OsInfo Os { get; init; }
    public string EditionFamily => Os.EditionFamily;
    public bool Elevated { get; init; }
    /// <summary>Machine non modifiée (Windows Sandbox ou --clean) : seuls ces relevés font foi pour les types de démarrage d'usine.</summary>
    public bool CleanMachine { get; init; }
    public string KnowledgeVersion { get; init; } = "";
    public List<ServiceCapture> Services { get; init; } = [];
}

public static class VerifyCommands
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ── capture ──────────────────────────────────────────────
    /// <summary>Relève la configuration réelle de chaque service documenté et contrôle les affirmations des fiches.</summary>
    public static CaptureFile Capture(KnowledgeBase kb, bool clean)
    {
        var services = new ServiceReader().ReadAll().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var tasks = TaskIndex.Load();
        var captures = new List<ServiceCapture>();

        foreach (var entry in kb.Services.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var info = new[] { entry.Name }.Concat(entry.Aliases).Select(n => services.GetValueOrDefault(n)).FirstOrDefault(s => s is not null);
            if (info is null)
            {
                captures.Add(new ServiceCapture { Name = entry.Name, Exists = false, Discrepancies = ["service absent de cette machine"] });
                continue;
            }

            var patterns = entry.Revivers.Where(r => r.Type == "task").Select(r => r.Target).ToList();
            var linked = tasks.FindLinked(info, patterns);
            captures.Add(new ServiceCapture
            {
                Name = entry.Name,
                Exists = true,
                StartType = info.StartMode.ToString(),
                Triggers = info.Triggers.Select(TriggerCatalog.Describe).ToList(),
                Account = info.Account,
                Dependencies = info.Dependencies,
                Binary = info.ServiceDll ?? ExecutablePath.Extract(info.BinaryPath),
                LinkedTasks = linked.Select(l => l.Task.Path).ToList(),
                Discrepancies = Check(entry, info, services, tasks, tasks.UnreadableFolders > 0 || !OsInfo.IsElevated()),
            });
        }

        return new CaptureFile
        {
            Os = OsInfo.Detect(),
            Elevated = OsInfo.IsElevated(),
            CleanMachine = clean || IsWindowsSandbox(),
            KnowledgeVersion = kb.Metadata.Version,
            Services = captures,
        };
    }

    /// <summary>Vérifie ce que la fiche affirme : réparateurs existants, tâches présentes, triggers cohérents.</summary>
    internal static List<string> Check(KnowledgeEntry entry, ServiceInfo info, IReadOnlyDictionary<string, ServiceInfo> all,
        TaskIndex tasks, bool tasksIncomplete)
    {
        var issues = new List<string>();
        foreach (var r in entry.Revivers)
        {
            switch (r.Type)
            {
                case "service" when !all.ContainsKey(r.Target):
                    issues.Add($"réparateur « {r.Target} » introuvable");
                    break;
                case "task":
                    var rx = TaskIndex_Glob(r.Target);
                    if (!tasks.Tasks.Any(t => rx.IsMatch(t.Path)))
                        issues.Add(tasksIncomplete
                            ? $"tâche « {r.Target} » non vérifiable sans droits administrateur"
                            : $"aucune tâche « {r.Target} »");
                    break;
                case "trigger" when !info.Triggers.Any(t => TriggerCatalog.MatchesKnowledgeTarget(t, r.Target)):
                    issues.Add($"trigger « {r.Target} » annoncé mais absent");
                    break;
            }
        }
        return issues;
    }

    private static System.Text.RegularExpressions.Regex TaskIndex_Glob(string glob) =>
        new("^" + System.Text.RegularExpressions.Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static void Save(CaptureFile capture, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(capture, WriteOptions));

    public static CaptureFile Load(string path) =>
        JsonSerializer.Deserialize<CaptureFile>(File.ReadAllText(path), SnapshotStore.JsonOptions)
        ?? throw new InvalidDataException("Fichier de capture illisible");

    /// <summary>Windows Sandbox s'exécute sous le compte WDAGUtilityAccount.</summary>
    public static bool IsWindowsSandbox() => Environment.UserName.Equals("WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase);

    // ── apply ────────────────────────────────────────────────
    /// <summary>
    /// Reporte une capture dans les fiches : defaults["Édition:Build"] et, si aucun écart, statut « config-verified ».
    /// Un statut « behavior-observed » n'est jamais rétrogradé.
    /// </summary>
    public static List<string> Apply(CaptureFile capture, string knowledgeDir, bool dryRun)
    {
        var report = new List<string>();
        var key = $"{capture.Os.EditionFamily}:{capture.Os.Build}";
        foreach (var c in capture.Services)
        {
            var file = Path.Combine(knowledgeDir, "services", c.Name + ".json");
            if (!File.Exists(file) || !c.Exists)
            {
                report.Add($"  {c.Name,-34} ignoré (absent)");
                continue;
            }

            var root = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var defaults = root["defaults"] as JsonObject ?? new JsonObject();
            root["defaults"] = defaults;
            // Sur une machine possiblement modifiée, on conserve un éventuel type d'usine déjà connu.
            var previousStart = (defaults[key] as JsonObject)?["startType"]?.GetValue<string>();
            var entryDefaults = new JsonObject();
            if ((capture.CleanMachine ? c.StartType : previousStart) is { } startType) entryDefaults["startType"] = startType;
            entryDefaults["triggers"] = new JsonArray(c.Triggers.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
            entryDefaults["account"] = c.Account;
            defaults[key] = entryDefaults;

            var verification = root["verification"] as JsonObject ?? new JsonObject { ["status"] = "draft" };
            var status = (string?)verification["status"] ?? "draft";
            string outcome;
            if (c.Discrepancies.Count > 0)
            {
                outcome = "écarts : " + string.Join(" ; ", c.Discrepancies);
            }
            else if (status == "behavior-observed")
            {
                outcome = "défauts enregistrés (statut « comportement observé » conservé)";
            }
            else
            {
                verification["status"] = "config-verified";
                verification["build"] = capture.Os.FullBuild;
                verification["edition"] = capture.Os.EditionFamily;
                verification["date"] = capture.CapturedAt.ToString("yyyy-MM-dd");
                outcome = "🔍 configuration vérifiée";
            }
            root["verification"] = verification;

            if (!dryRun) File.WriteAllText(file, root.ToJsonString(WriteOptions) + Environment.NewLine);
            report.Add($"  {c.Name,-34} {outcome}");
        }
        return report;
    }

    // ── compare ──────────────────────────────────────────────
    /// <summary>Compare la machine aux défauts connus pour son édition/build (détecte les anciens « optimiseurs »).</summary>
    public static List<string> Compare(KnowledgeBase kb)
    {
        var os = OsInfo.Detect();
        var key = $"{os.EditionFamily}:{os.Build}";
        var services = new ServiceReader().ReadAll().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        var known = 0;
        foreach (var e in kb.Services.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!e.Defaults.TryGetValue(key, out var d) || d.StartType is null) continue;
            known++;
            if (!services.TryGetValue(e.Name, out var s)) continue;
            if (!string.Equals(s.StartMode.ToString(), d.StartType, StringComparison.OrdinalIgnoreCase))
                lines.Add($"  {e.Name,-34} défaut {d.StartType,-17} actuel {s.StartMode}");
        }
        lines.Insert(0, $"Défauts connus pour {key} : {known} fiche(s). Écarts : {lines.Count}.");
        return lines;
    }

    // ── who ──────────────────────────────────────────────────
    public static void Who(KnowledgeBase kb, string name)
    {
        var all = new ServiceReader().ReadAll();
        var svc = all.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (svc is null)
        {
            Console.WriteLine($"Service « {name} » introuvable.");
            return;
        }
        var entry = kb.FindService(svc.TemplateName);
        var known = entry?.Revivers.Select(r => new KnownReviver(r.Type, r.Target, r.Note?.Resolve("fr"))).ToList() ?? [];
        var tasks = TaskIndex.Load();

        Console.WriteLine($"{svc.DisplayName} ({svc.Name}) — {svc.StartMode}, {svc.State}");
        Console.WriteLine($"Tâches lues : {tasks.Tasks.Count} ({tasks.UnreadableFolders} dossier(s) illisible(s) sans droits admin)");
        Console.WriteLine("\nQui pourrait le relancer :");
        foreach (var f in ReviverAnalyzer.Analyze(svc, all, tasks, known, ManagementContext.Detect()))
        {
            var what = f.Trigger is { } t ? TriggerCatalog.Describe(t) : f.Subject;
            Console.WriteLine($"  [{f.Strength,-8}] {f.Kind,-13} {what}{(f.Detail is null ? "" : " — " + f.Detail)}{(f.TaskReason is { } r ? $" ({r})" : "")}");
        }

        var taskLog = ServiceEventReader.IsTaskSchedulerLogEnabled() == true;
        Console.WriteLine($"\nQui l'a réellement relancé (journal du Planificateur {(taskLog ? "actif" : "désactivé")}) :");
        var events = ServiceEventReader.ReadServiceEvents();
        foreach (var entryT in TimelineBuilder.Build(svc, events, [], taskLog, max: 15))
        {
            var e = entryT.Event;
            var change = e.Kind == ServiceEventKind.StartModeChanged ? $" {e.OldMode?.ToString() ?? e.RawOld} → {e.NewMode?.ToString() ?? e.RawNew}" : "";
            Console.WriteLine($"  {e.At:g}  {e.Kind}{change}{(e.Account is null ? "" : $" par {e.Account}")}");
            foreach (var c in entryT.Clues) Console.WriteLine($"      ↳ {c.Kind} {c.At:T} {c.Subject}{(c.Detail is null ? "" : " — " + c.Detail)}");
        }
    }
}
