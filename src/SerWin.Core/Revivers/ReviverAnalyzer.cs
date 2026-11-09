using System.Management;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SerWin.Core.Services;
using SerWin.Core.Tasks;

namespace SerWin.Core.Revivers;

/// <summary>Les sept familles de mécanismes de relance de la spec (§5.5).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviverKind
{
    Trigger,
    Recovery,
    Dependent,
    Task,
    RepairService,
    Policy,
    Known,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviverStrength
{
    /// <summary>Mécanisme en place et actif : il relancera le service.</summary>
    Active,
    /// <summary>Peut relancer le service sous conditions (à la demande, après une mise à jour…).</summary>
    Possible,
    /// <summary>Présent mais neutralisé (tâche désactivée, dépendant désactivé, stratégie absente).</summary>
    Inactive,
}

/// <summary>Mécanisme connu, issu de la base de connaissances (type + cible), passé par l'application.</summary>
public sealed record KnownReviver(string Type, string Target, string? Note = null);

public sealed record ReviverFinding
{
    public ReviverKind Kind { get; init; }
    public ReviverStrength Strength { get; init; }
    /// <summary>Objet principal : service dépendant, chemin de tâche, clé de stratégie, service réparateur…</summary>
    public string Subject { get; init; } = "";
    public string? Detail { get; init; }
    public ServiceTrigger? Trigger { get; init; }
    public ScheduledTaskInfo? Task { get; init; }
    public TaskLinkReason? TaskReason { get; init; }
}

/// <summary>Contexte de gestion de la machine (domaine, MDM), calculé une fois.</summary>
public sealed record ManagementContext(bool DomainJoined, bool MdmEnrolled)
{
    public static ManagementContext Detect()
    {
        var domain = false;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT PartOfDomain FROM Win32_ComputerSystem");
            foreach (var o in s.Get())
                using (o)
                    domain |= o["PartOfDomain"] is true;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }

        var mdm = false;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Enrollments");
            foreach (var sub in k?.GetSubKeyNames() ?? [])
            {
                using var e = k!.OpenSubKey(sub);
                // « Deploy / Cloud / Local Authority » sont des fournisseurs internes présents sur tout Windows 11 :
                // seul un vrai serveur de gestion (ex. « MS DM Server » pour Intune) signifie que le PC est géré.
                if (e?.GetValue("ProviderID") is string provider && provider.Length > 0
                    && !provider.EndsWith("Authority", StringComparison.OrdinalIgnoreCase)) mdm = true;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
        return new ManagementContext(domain, mdm);
    }
}

/// <summary>
/// Analyse statique « qui pourrait le relancer ? » (spec §5.5) :
/// triggers, récupération, dépendants, tâches, réparateurs, stratégies, mécanismes connus.
/// </summary>
public static class ReviverAnalyzer
{
    /// <summary>Services « réparateurs » connus et les services qu'ils remettent en état.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RepairServices = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["WaaSMedicSvc"] = ["wuauserv", "UsoSvc", "BITS", "DoSvc", "WaaSMedicSvc"],
        ["SecurityHealthService"] = ["WinDefend", "mpssvc", "wscsvc", "WdNisSvc"],
    };

    public static List<ReviverFinding> Analyze(
        ServiceInfo target,
        IReadOnlyCollection<ServiceInfo> allServices,
        TaskIndex tasks,
        IReadOnlyCollection<KnownReviver> known,
        ManagementContext management)
    {
        var findings = new List<ReviverFinding>();
        var byName = allServices.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

        // 1. Déclencheurs du service.
        foreach (var t in target.Triggers.Where(t => t.Action == TriggerAction.Start))
            findings.Add(new ReviverFinding
            {
                Kind = ReviverKind.Trigger,
                Strength = target.StartMode == ServiceStartMode.Disabled ? ReviverStrength.Inactive : ReviverStrength.Active,
                Subject = t.Kind.ToString(),
                Trigger = t,
            });

        // 2. Actions de récupération : relance après un plantage (pas après un arrêt volontaire).
        if (target.Recovery is { } rec)
        {
            var restarts = rec.Actions.Count(a => a.Type == RecoveryActionType.Restart);
            if (restarts > 0)
                findings.Add(new ReviverFinding
                {
                    Kind = ReviverKind.Recovery,
                    Strength = target.StartMode == ServiceStartMode.Disabled ? ReviverStrength.Inactive : ReviverStrength.Possible,
                    Subject = restarts.ToString(),
                    Detail = rec.ResetPeriodSeconds.ToString(),
                });
        }

        // 3. Services dépendants : démarrer l'un d'eux démarre ce service.
        foreach (var depName in target.DependentServices)
        {
            if (!byName.TryGetValue(depName, out var dep)) continue;
            var strength = dep.StartMode switch
            {
                ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed or ServiceStartMode.Boot or ServiceStartMode.System => ReviverStrength.Active,
                ServiceStartMode.Disabled => ReviverStrength.Inactive,
                _ => ReviverStrength.Possible,
            };
            if (target.StartMode == ServiceStartMode.Disabled) strength = ReviverStrength.Inactive;
            findings.Add(new ReviverFinding { Kind = ReviverKind.Dependent, Strength = strength, Subject = dep.Name, Detail = dep.StartMode.ToString() });
        }

        // 4. Tâches planifiées liées.
        var patterns = known.Where(k => k.Type == "task").Select(k => k.Target);
        foreach (var link in tasks.FindLinked(target, patterns))
            findings.Add(new ReviverFinding
            {
                Kind = ReviverKind.Task,
                Strength = !link.Task.Enabled ? ReviverStrength.Inactive : link.Task.HasTriggers ? ReviverStrength.Active : ReviverStrength.Possible,
                Subject = link.Task.Path,
                Task = link.Task,
                TaskReason = link.Reason,
            });

        // 5. Services réparateurs (WaaSMedicSvc…), connus en dur ou décrits par la fiche.
        var repairers = RepairServices.Where(r => r.Value.Contains(target.Name, StringComparer.OrdinalIgnoreCase)).Select(r => r.Key)
            .Concat(known.Where(k => k.Type == "service").Select(k => k.Target))
            .Where(n => !n.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in repairers)
        {
            byName.TryGetValue(name, out var svc);
            findings.Add(new ReviverFinding
            {
                Kind = ReviverKind.RepairService,
                Strength = svc is null || svc.StartMode == ServiceStartMode.Disabled ? ReviverStrength.Inactive : ReviverStrength.Active,
                Subject = name,
                Detail = known.FirstOrDefault(k => k.Type == "service" && k.Target.Equals(name, StringComparison.OrdinalIgnoreCase))?.Note,
            });
        }

        // 6. Stratégies de groupe / MDM.
        foreach (var k in known.Where(k => k.Type == "policy"))
        {
            if (k.Target.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            {
                var values = ReadPolicyValues(k.Target[5..]);
                findings.Add(new ReviverFinding
                {
                    Kind = ReviverKind.Policy,
                    Strength = values is null ? ReviverStrength.Inactive : ReviverStrength.Active,
                    Subject = k.Target,
                    Detail = values,
                });
            }
            else
            {
                var managed = k.Target == "mdm" ? management.MdmEnrolled : management.DomainJoined || management.MdmEnrolled;
                findings.Add(new ReviverFinding
                {
                    Kind = ReviverKind.Policy,
                    Strength = managed ? ReviverStrength.Active : ReviverStrength.Inactive,
                    Subject = k.Target,
                });
            }
        }
        if (target.Triggers.Any(t => t.Kind == TriggerKind.GroupPolicy) && (management.DomainJoined || management.MdmEnrolled)
            && known.All(k => k.Type != "policy"))
            findings.Add(new ReviverFinding { Kind = ReviverKind.Policy, Strength = ReviverStrength.Possible, Subject = "domain-gpo" });

        // 7. Mécanismes connus de la base (mises à jour Windows, logiciels tiers).
        foreach (var k in known.Where(k => k.Type is "update" or "software"))
            findings.Add(new ReviverFinding { Kind = ReviverKind.Known, Strength = ReviverStrength.Possible, Subject = k.Target, Detail = k.Note ?? k.Type });

        return findings
            .OrderBy(f => f.Strength)
            .ThenBy(f => f.Kind)
            .ToList();
    }

    /// <summary>Valeurs d'une clé de stratégie (« Nom=valeur; … »), ou null si la clé n'existe pas.</summary>
    private static string? ReadPolicyValues(string subKey)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(subKey);
            if (k is null) return null;
            var names = k.GetValueNames();
            return names.Length == 0 ? "" : string.Join("; ", names.Take(8).Select(n => $"{n}={k.GetValue(n)}"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }
}
