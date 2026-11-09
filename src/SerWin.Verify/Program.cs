using System.Text.Json;
using SerWin.Core.Platform;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;
using SerWin.Knowledge;
using SerWin.Verify;

// SerWin.Verify — outil de vérification de la base de connaissances (spec §4.3).
// Aucune connexion réseau : les résultats sont des fichiers JSON à joindre à une contribution.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.FirstOrDefault()?.ToLowerInvariant();
var rest = args.Skip(1).ToArray();
string? Opt(string name) => rest.SkipWhile(a => !a.Equals(name, StringComparison.OrdinalIgnoreCase)).Skip(1).FirstOrDefault();
bool Flag(string name) => rest.Contains(name, StringComparer.OrdinalIgnoreCase);
KnowledgeBase LoadKb() => Opt("--knowledge") is { } dir ? KnowledgeBase.LoadFromDirectory(dir) : KnowledgeBase.LoadEmbedded();

return command switch
{
    "validate" => Validate(rest.FirstOrDefault(a => !a.StartsWith("--"))),
    "dump" => Dump(rest),
    "coverage" => Coverage(),
    "os" => Os(),
    "capture" => Capture(),
    "apply" => Apply(),
    "compare" => Compare(),
    "who" => Who(),
    "ps" => Ps(),
    "context" => Context(),
    "net" => Net(),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        SerWin.Verify — vérification de la base de connaissances SerWin

        Vérifier les fiches (passage en « 🔍 Configuration vérifiée ») :
          1. Sur une machine propre (idéalement Windows Sandbox), en administrateur :
               SerWin.Verify capture [--out fichier.json] [--clean]
          2. Dans le dépôt :
               SerWin.Verify apply fichier.json [--knowledge knowledge] [--dry-run]

        Autres commandes :
          validate [dossier-knowledge]       Valide toutes les fiches (défaut : base embarquée)
          compare [--knowledge dossier]      Écarts entre cette machine et les défauts connus
          who <service>                      Qui pourrait le relancer / qui l'a relancé
          dump <service>… | --all            Configuration observée (JSON)
          coverage                           Services de cette machine sans fiche
          os                                 Édition et build détectés
          ps                                 Arbre des processus, services hébergés, signatures
          context                            Contexte détecté (matériel, usages) et verdicts « Chez toi »
          net [--hours N] [--unknown]         Échanges réseau : journal DNS + connexions TCP, fiches destinations
        """);
    return 1;
}

static int Validate(string? dir)
{
    var kb = dir is null ? KnowledgeBase.LoadEmbedded() : KnowledgeBase.LoadFromDirectory(dir);
    var issues = KnowledgeValidator.Validate(kb);
    foreach (var i in issues) Console.WriteLine(i);
    var errors = issues.Count(i => i.Severity == "error");
    Console.WriteLine($"Base {kb.Metadata.Version} : {kb.ServiceCount} fiches services, {kb.ProcessCount} fiches processus, {kb.Destinations.Count} fiches destinations — " +
                      $"{errors} erreur(s), {issues.Count - errors} avertissement(s).");
    return errors == 0 ? 0 : 2;
}

static int Dump(string[] names)
{
    var all = new ServiceReader().ReadAll();
    var selected = names.Contains("--all")
        ? all
        : all.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    Console.WriteLine(JsonSerializer.Serialize(selected, SnapshotStore.JsonOptions));
    return selected.Count > 0 ? 0 : 3;
}

static int Coverage()
{
    var kb = KnowledgeBase.LoadEmbedded();
    var all = new ServiceReader().ReadAll().Where(s => !s.IsPerUserInstance).ToList();
    var missing = all.Where(s => kb.FindService(s.Name) is null).ToList();
    foreach (var s in missing) Console.WriteLine($"{s.Name,-40} {s.StartMode,-17} {s.Publisher}");
    Console.WriteLine($"{all.Count - missing.Count}/{all.Count} services documentés.");
    return 0;
}

static int Os()
{
    var os = OsInfo.Detect();
    Console.WriteLine($"{os.FriendlyName} — {os.DisplayVersion} — build {os.FullBuild} — clé « {os.EditionFamily}:{os.Build} » — admin : {OsInfo.IsElevated()}");
    return 0;
}

int Capture()
{
    var kb = LoadKb();
    var capture = VerifyCommands.Capture(kb, Flag("--clean"));
    var path = Opt("--out") ?? $"serwin-capture-{capture.Os.EditionFamily}-{capture.Os.FullBuild}.json";
    VerifyCommands.Save(capture, path);
    var ok = capture.Services.Count(s => s.Exists && s.Discrepancies.Count == 0);
    Console.WriteLine($"{capture.Services.Count} fiches relevées sur {capture.Os.FriendlyName} build {capture.Os.FullBuild} : {ok} sans écart.");
    foreach (var s in capture.Services.Where(s => s.Discrepancies.Count > 0))
        Console.WriteLine($"  {s.Name,-34} {string.Join(" ; ", s.Discrepancies)}");
    if (!capture.Elevated)
        Console.WriteLine("⚠️  Capture sans droits administrateur : certaines tâches planifiées sont invisibles.");
    if (!capture.CleanMachine)
        Console.WriteLine("⚠️  Machine possiblement modifiée : les types de démarrage ne seront pas reportés comme « défauts » (utiliser Windows Sandbox, ou --clean si la machine est vierge).");
    Console.WriteLine($"Résultat : {Path.GetFullPath(path)}");
    return 0;
}

int Apply()
{
    var file = rest.FirstOrDefault(a => !a.StartsWith("--"));
    if (file is null) return Usage();
    var capture = VerifyCommands.Load(file);
    var dir = Opt("--knowledge") ?? "knowledge";
    var dry = Flag("--dry-run");
    Console.WriteLine($"Capture {capture.Os.FriendlyName} build {capture.Os.FullBuild} du {capture.CapturedAt:g}{(dry ? " (simulation)" : "")} :");
    foreach (var line in VerifyCommands.Apply(capture, dir, dry)) Console.WriteLine(line);
    return 0;
}

int Compare()
{
    foreach (var line in VerifyCommands.Compare(LoadKb())) Console.WriteLine(line);
    return 0;
}

int Who()
{
    var name = rest.FirstOrDefault(a => !a.StartsWith("--"));
    if (name is null) return Usage();
    VerifyCommands.Who(LoadKb(), name);
    return 0;
}

static int Ps()
{
    var sampler = new SerWin.Core.Processes.ProcessSampler();
    sampler.Sample();
    Thread.Sleep(1000);
    var procs = sampler.Sample();
    var roots = SerWin.Core.Processes.ProcessTree.Build(procs, new ServiceReader().ReadAll());
    var sw = System.Diagnostics.Stopwatch.StartNew();
    void Print(SerWin.Core.Processes.ProcessNode n, int depth)
    {
        var p = n.Process;
        var sig = SerWin.Core.Processes.SignatureVerifier.Verify(p.Path);
        var flag = !sig.IsTrusted && sig.Status != SerWin.Core.Processes.SignatureStatus.Unknown || SerWin.Core.Processes.SuspiciousLocation.IsSuspicious(p.Path) ? " ⚠️" : "";
        Console.WriteLine($"{new string(' ', depth * 2)}{p.Name} [{p.Pid}] {p.CpuPercent:0.0}% {p.PrivateBytes / 1048576} Mo {sig.Status} {sig.Signer}{(p.IsCritical ? " CRITIQUE" : "")}{flag}");
        foreach (var s in n.HostedServices) Console.WriteLine($"{new string(' ', depth * 2 + 4)}• {s.Name}");
        foreach (var c in n.Children) Print(c, depth + 1);
    }
    foreach (var r in roots) Print(r, 0);
    Console.Error.WriteLine($"{procs.Count} processus, signatures en {sw.ElapsedMilliseconds} ms");
    return 0;
}

static int Context()
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var ctx = SerWin.Core.Context.MachineContext.Detect();
    Console.WriteLine($"Contexte détecté en {sw.ElapsedMilliseconds} ms :");
    foreach (var (k, f) in ctx.Facts) Console.WriteLine($"  {k,-22} {(f.Count?.ToString() ?? "inconnu"),-8} {f.Detail}");
    Console.WriteLine("\nVerdicts « Chez toi » :");
    foreach (var e in KnowledgeBase.LoadEmbedded().Services.Where(e => e.UsefulIf.Count > 0).OrderBy(e => e.Name))
    {
        var v = SerWin.Core.Context.UsefulnessRules.Evaluate(e.UsefulIf, ctx);
        Console.WriteLine($"  {e.Name,-34} {v.Verdict,-10} {string.Join(", ", v.Checks.Select(c => $"{c.Key}{c.Expected}→{c.Satisfied?.ToString() ?? "?"}"))}");
    }
    return 0;
}

int Net()
{
    var kb = LoadKb();
    var hours = int.TryParse(Opt("--hours"), out var h) ? h : 24;
    var setting = SerWin.Core.Network.DnsLog.ReadSetting();
    Console.WriteLine(setting is null ? "Journal DNS : illisible"
        : $"Journal DNS : {(setting.Enabled ? "activé" : "désactivé")}, taille max {setting.MaxSize / 1024} Ko");

    var resolver = new SerWin.Core.Network.SenderResolver();
    resolver.Refresh(new ServiceReader().ReadAll());
    var observer = new SerWin.Core.Network.NetworkObserver(resolver);
    var queries = SerWin.Core.Network.DnsLog.Read(DateTime.Now.AddHours(-hours));
    observer.AddDns(queries);
    observer.AddCache(SerWin.Core.Network.DnsCache.Read(), DateTime.Now);
    observer.AddConnections(SerWin.Core.Network.TcpConnections.ReadEstablished(), DateTime.Now);
    Console.WriteLine($"{queries.Count} requête(s) DNS sur {hours} h, {observer.Flows.Count} échange(s) programme → destination.");
    Console.WriteLine();

    var rows = observer.Flows
        .Where(f => f.Scope != SerWin.Core.Network.AddressScope.Loopback)
        .Select(f => (Flow: f, Entry: f.HasHostName ? kb.FindDestination(f.Destination, f.Aliases) : null))
        .ToList();
    if (Flag("--unknown"))
    {
        foreach (var g in rows.Where(r => r.Entry is null && r.Flow.HasHostName).GroupBy(r => r.Flow.Destination).OrderByDescending(g => g.Sum(r => r.Flow.Queries + r.Flow.Connections)))
            Console.WriteLine($"{g.Key,-60} {string.Join(", ", g.Select(r => r.Flow.Sender.Service ?? r.Flow.Sender.ProcessName ?? r.Flow.Sender.Key).Distinct())}");
        return 0;
    }
    foreach (var r in rows.OrderBy(r => r.Entry?.Name ?? "~").ThenBy(r => r.Flow.Destination))
    {
        var sender = r.Flow.Sender.Service ?? r.Flow.Sender.ProcessName ?? r.Flow.Sender.Key;
        Console.WriteLine($"{r.Entry?.Name ?? "(sans fiche)",-28} {r.Flow.Destination,-50} {sender,-28} dns={r.Flow.Queries} tcp={r.Flow.Connections}{(r.Flow.ActiveConnections > 0 ? " ●" : "")}");
    }
    return 0;
}
