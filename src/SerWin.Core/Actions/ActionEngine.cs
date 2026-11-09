using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;
using SerWin.Core.Tasks;

namespace SerWin.Core.Actions;

/// <summary>
/// Moteur d'actions (spec §5.2-5.3) : aperçu → sauvegarde → application → vérification → historique.
/// Toute modification passe par ici ; la liste rouge y est appliquée sans exception.
/// </summary>
public sealed class ActionEngine(
    IServiceControl control,
    HistoryStore history,
    BackupStore backups,
    Func<ServiceInfo, IEnumerable<ScheduledTaskInfo>>? linkedTasks = null)
{
    public static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(30);

    private static readonly HashSet<ServiceStartMode> SettableModes =
        [ServiceStartMode.Automatic, ServiceStartMode.AutomaticDelayed, ServiceStartMode.Manual, ServiceStartMode.Disabled];

    public HistoryStore History => history;
    public BackupStore Backups => backups;

    // ── Aperçu ───────────────────────────────────────────────
    public ActionPlan Plan(ActionRequest request)
    {
        var target = control.Read(request.Service);
        if (target is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };

        var safety = SafetyLists.ForService(target.TemplateName);
        if (safety == SafetyLevel.Red) return new ActionPlan { Request = request, Safety = safety, Blocked = BlockReason.RedList };

        var changes = new List<ServiceChange>();
        var dependents = new List<string>();

        switch (request.Kind)
        {
            case ActionKind.Start:
                if (IsRunningOrPending(target.State)) break;
                // Un service désactivé ne peut pas démarrer : on le repasse en Manuel, visiblement dans l'aperçu.
                if (target.StartMode == ServiceStartMode.Disabled)
                    changes.Add(new(target.Name, ChangeKind.StartMode, nameof(ServiceStartMode.Disabled), nameof(ServiceStartMode.Manual)));
                changes.Add(new(target.Name, ChangeKind.State, nameof(ServiceRunState.Stopped), nameof(ServiceRunState.Running)));
                break;

            case ActionKind.Stop:
                if (!IsRunningOrPending(target.State)) break;
                // EnumDependentServices renvoie les dépendants dans l'ordre d'arrêt.
                foreach (var depName in target.DependentServices)
                {
                    var dep = control.Read(depName);
                    if (dep is null || !IsRunningOrPending(dep.State)) continue;
                    if (SafetyLists.ForService(dep.TemplateName) == SafetyLevel.Red)
                        return new ActionPlan { Request = request, Safety = SafetyLevel.Red, Blocked = BlockReason.RedDependent, BlockedDetail = dep.Name };
                    if (SafetyLists.ForService(dep.TemplateName) == SafetyLevel.Orange) safety = SafetyLevel.Orange;
                    dependents.Add(dep.Name);
                    changes.Add(new(dep.Name, ChangeKind.State, nameof(ServiceRunState.Running), nameof(ServiceRunState.Stopped)));
                }
                changes.Add(new(target.Name, ChangeKind.State, nameof(ServiceRunState.Running), nameof(ServiceRunState.Stopped)));
                break;

            case ActionKind.SetStartMode:
                if (target.IsPerUserInstance)
                    return new ActionPlan { Request = request, Safety = safety, Blocked = BlockReason.PerUserInstance, BlockedDetail = target.TemplateName };
                if (request.StartMode is not { } mode || !SettableModes.Contains(mode) || !SettableModes.Contains(target.StartMode))
                    return new ActionPlan { Request = request, Safety = safety, Blocked = BlockReason.UnsupportedStartMode };
                if (mode != target.StartMode)
                    changes.Add(new(target.Name, ChangeKind.StartMode, target.StartMode.ToString(), mode.ToString()));
                break;

            default:
                throw new ArgumentException($"Action {request.Kind} non planifiable directement", nameof(request));
        }

        return new ActionPlan { Request = request, Safety = safety, Changes = changes, DependentsToStop = dependents };
    }

    /// <summary>
    /// Suggestions « Chez toi » (spec §5.7) : désactive chaque service coché et, si demandé, l'arrête tout de suite.
    /// Les services bloqués (liste rouge, instances par utilisateur…) sont écartés et listés dans <paramref name="skipped"/>.
    /// </summary>
    public ActionPlan PlanDisableMany(IEnumerable<string> services, bool stopNow, out List<(string Service, BlockReason Reason)> skipped)
    {
        skipped = [];
        var changes = new List<ServiceChange>();
        var dependents = new List<string>();
        var safety = SafetyLevel.None;
        foreach (var name in services.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var disable = Plan(new ActionRequest(name, ActionKind.SetStartMode, ServiceStartMode.Disabled));
            if (disable.IsBlocked)
            {
                skipped.Add((name, disable.Blocked));
                continue;
            }
            var stop = stopNow ? Plan(new ActionRequest(name, ActionKind.Stop)) : null;
            if (stop is { IsBlocked: true })
            {
                skipped.Add((name, stop.Blocked));
                continue;
            }
            if (disable.Safety > safety) safety = disable.Safety;
            if (stop is not null && stop.Safety > safety) safety = stop.Safety;
            changes.AddRange(disable.Changes);
            foreach (var c in stop?.Changes ?? [])
                if (!changes.Contains(c)) changes.Add(c);
            dependents.AddRange(stop?.DependentsToStop ?? []);
        }
        return new ActionPlan
        {
            Request = new ActionRequest("", ActionKind.ApplySuggestions),
            Safety = safety,
            Changes = changes,
            DependentsToStop = dependents.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Aperçu d'une annulation : changements inverses, dans l'ordre inverse.</summary>
    public ActionPlan PlanUndo(Guid entryId)
    {
        var entry = history.Get(entryId);
        var request = new ActionRequest(entry?.Service ?? "", ActionKind.Undo);
        if (entry is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };
        if (entry.IsUndone) return new ActionPlan { Request = request, Blocked = BlockReason.AlreadyUndone };
        if (entry.Action == ActionKind.TerminateProcess) return new ActionPlan { Request = request, Blocked = BlockReason.NotUndoable };

        var inverse = entry.Changes.AsEnumerable().Reverse().Select(c => c.Inverse()).ToList();
        var red = inverse.FirstOrDefault(c => SafetyLists.ForService(TemplateOf(c.Service)) == SafetyLevel.Red);
        if (red is not null)
            return new ActionPlan { Request = request, Safety = SafetyLevel.Red, Blocked = BlockReason.RedList, BlockedDetail = red.Service };

        var safety = inverse.Any(c => SafetyLists.ForService(TemplateOf(c.Service)) == SafetyLevel.Orange) ? SafetyLevel.Orange : SafetyLevel.None;
        return new ActionPlan { Request = request, Safety = safety, Changes = inverse, UndoOf = entryId };
    }

    /// <summary>
    /// « Tout restaurer » (spec §5.4) : ramène le type de démarrage de chaque service à celui de la photographie.
    /// Les services de la liste rouge et les instances par utilisateur (noms changeants) sont ignorés.
    /// </summary>
    public ActionPlan PlanRestoreAll(SystemSnapshot snapshot)
    {
        var current = control.ReadAll().ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var changes = new List<ServiceChange>();
        var safety = SafetyLevel.None;
        foreach (var snap in snapshot.Services)
        {
            if (snap.IsPerUserInstance || !SettableModes.Contains(snap.StartMode)) continue;
            if (SafetyLists.ForService(snap.TemplateName) == SafetyLevel.Red) continue;
            if (!current.TryGetValue(snap.Name, out var now) || !SettableModes.Contains(now.StartMode)) continue;
            if (now.StartMode == snap.StartMode) continue;
            if (SafetyLists.ForService(snap.TemplateName) == SafetyLevel.Orange) safety = SafetyLevel.Orange;
            changes.Add(new(snap.Name, ChangeKind.StartMode, now.StartMode.ToString(), snap.StartMode.ToString()));
        }
        return new ActionPlan { Request = new ActionRequest("", ActionKind.RestoreAll), Safety = safety, Changes = changes };
    }

    // ── Exécution ────────────────────────────────────────────
    public ActionResult Execute(ActionPlan plan)
    {
        if (plan.IsBlocked) throw new InvalidOperationException($"Action bloquée : {plan.Blocked}");
        if (plan.IsNoOp) return new ActionResult();

        // 1. Sauvegarde complète de chaque service touché, avant toute modification.
        var services = plan.Changes.Select(c => c.Service).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var label = plan.Request.Kind switch
        {
            ActionKind.RestoreAll => "restore-all",
            ActionKind.ApplySuggestions => "suggestions",
            _ => $"{plan.Request.Kind}_{plan.Request.Service}",
        };
        var infos = services.Select(control.Read).OfType<ServiceInfo>().ToList();
        var tasks = linkedTasks is null ? [] : infos.SelectMany(linkedTasks).ToList();
        var backupFile = backups.Save(infos, label, tasks);

        // 2. Relevé de l'état actuel (pour détecter les modifications extérieures lors d'une annulation).
        var drifted = new List<ServiceChange>();
        if (plan.Request.Kind == ActionKind.Undo)
        {
            foreach (var c in plan.Changes)
                if (!Matches(c, c.Before, out var actual)) drifted.Add(c with { Before = actual! });
        }

        // 3. Application dans un ordre sûr : types de démarrage d'abord (un service désactivé ne peut pas démarrer),
        //    puis états, puis désactivations.
        var applied = new List<ServiceChange>();
        ServiceActionException? failure = null;
        foreach (var change in Order(plan.Changes))
        {
            try
            {
                Apply(change);
                applied.Add(change);
            }
            catch (ServiceActionException ex)
            {
                failure = ex;
                break;
            }
        }

        // 4. Vérification par relecture.
        var notEffective = applied.Where(c => c.Kind != ChangeKind.State && !Matches(c, c.After, out _)).ToList();

        // 5. Historique (seulement si quelque chose a été modifié).
        HistoryEntry? entry = null;
        if (applied.Count > 0)
        {
            entry = new HistoryEntry
            {
                Action = plan.Request.Kind,
                Service = plan.Request.Service,
                StartMode = plan.Request.StartMode?.ToString(),
                Changes = applied,
                BackupFile = backupFile,
                UndoOf = plan.UndoOf,
                Error = failure?.Code,
                ErrorMessage = failure?.Message,
            };
            history.Add(entry);
            if (plan.UndoOf is { } undone && failure is null) history.MarkUndone(undone, entry.Id);
        }

        return new ActionResult
        {
            Entry = entry,
            Error = failure?.Code,
            ErrorService = failure?.Service,
            ErrorMessage = failure?.Message,
            NotEffective = notEffective,
            Drifted = drifted,
        };
    }

    /// <summary>
    /// Ordre sûr : déverrouillage ACL d'abord (sinon Windows ne peut plus écrire Start), puis types de démarrage qui
    /// réactivent, états, triggers / récupération / tâches, désactivations, et verrouillage ACL en tout dernier.
    /// </summary>
    internal static IEnumerable<ServiceChange> Order(IEnumerable<ServiceChange> changes)
    {
        var list = changes.ToList();
        var disable = nameof(ServiceStartMode.Disabled);
        return list.Where(c => c.Kind == ChangeKind.RegistryAcl && !AclLock.IsLocked(c.After))
            .Concat(list.Where(c => c.Kind == ChangeKind.StartMode && c.After != disable))
            .Concat(list.Where(c => c.Kind == ChangeKind.State))
            .Concat(list.Where(c => c.Kind is ChangeKind.Triggers or ChangeKind.Recovery or ChangeKind.TaskEnabled or ChangeKind.EventLog))
            .Concat(list.Where(c => c.Kind == ChangeKind.StartMode && c.After == disable))
            .Concat(list.Where(c => c.Kind == ChangeKind.RegistryAcl && AclLock.IsLocked(c.After)));
    }

    private void Apply(ServiceChange change)
    {
        switch (change.Kind)
        {
            case ChangeKind.StartMode:
                control.SetStartMode(change.Service, Enum.Parse<ServiceStartMode>(change.After));
                break;
            case ChangeKind.State when change.After == nameof(ServiceRunState.Running):
                control.Start(change.Service, StateTimeout);
                break;
            case ChangeKind.State:
                control.Stop(change.Service, StateTimeout);
                break;
            case ChangeKind.Triggers:
                control.SetTriggers(change.Service, ChangeCodec.Triggers(change.After));
                break;
            case ChangeKind.Recovery:
                control.SetRecovery(change.Service, ChangeCodec.Recovery(change.After));
                break;
            case ChangeKind.TaskEnabled:
                control.SetTaskEnabled(change.Target!, bool.Parse(change.After));
                break;
            case ChangeKind.RegistryAcl:
                control.SetRegistryAcl(change.Service, change.After);
                break;
            case ChangeKind.EventLog:
                control.SetEventLog(change.Target!, change.After);
                break;
        }
    }

    private string? CurrentValue(ServiceChange c)
    {
        if (c.Kind == ChangeKind.TaskEnabled) return control.IsTaskEnabled(c.Target!)?.ToString();
        if (c.Kind == ChangeKind.EventLog) return control.GetEventLog(c.Target!);
        var s = control.Read(c.Service);
        if (s is null) return null;
        return c.Kind switch
        {
            ChangeKind.StartMode => s.StartMode.ToString(),
            ChangeKind.State => NormalizeState(s.State),
            ChangeKind.Triggers => ChangeCodec.Triggers(s.Triggers),
            ChangeKind.Recovery => ChangeCodec.Recovery(s.Recovery),
            ChangeKind.RegistryAcl => s.RegistryKeySddl is { } sddl ? AclLock.Normalize(sddl) : null,
            _ => null,
        };
    }

    /// <summary>
    /// Compare l'état réel à la valeur attendue, selon la nature du changement (comparaison sémantique pour les triggers,
    /// la récupération et l'ACL). Une valeur illisible est considérée comme conforme : on ne signale que ce qu'on constate.
    /// </summary>
    private bool Matches(ServiceChange c, string expected, out string? actual)
    {
        actual = CurrentValue(c);
        if (actual is null) return true;
        return c.Kind switch
        {
            ChangeKind.Triggers => ChangeCodec.TriggerKey(ChangeCodec.Triggers(actual)) == ChangeCodec.TriggerKey(ChangeCodec.Triggers(expected)),
            ChangeKind.Recovery => ChangeCodec.RecoveryKey(ChangeCodec.Recovery(actual)) == ChangeCodec.RecoveryKey(ChangeCodec.Recovery(expected)),
            ChangeKind.RegistryAcl => AclLock.IsLocked(actual) == AclLock.IsLocked(expected),
            ChangeKind.TaskEnabled => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            ChangeKind.EventLog => EventLogMatches(actual, expected),
            _ => actual == expected,
        };
    }

    // ── v0.6 : neutralisation complète et verrouillage ACL ────
    /// <summary>
    /// Neutralisation complète (spec §5.2, Avancé) : désactivation + arrêt + tâches liées désactivées
    /// + triggers supprimés + récupération « aucune action ». Chaque élément est sauvegardé et annulable.
    /// </summary>
    public ActionPlan PlanNeutralize(string name)
    {
        var request = new ActionRequest(name, ActionKind.Neutralize);
        var target = control.Read(name);
        if (target is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };
        var disable = Plan(new ActionRequest(name, ActionKind.SetStartMode, ServiceStartMode.Disabled));
        if (disable.IsBlocked) return new ActionPlan { Request = request, Safety = disable.Safety, Blocked = disable.Blocked, BlockedDetail = disable.BlockedDetail };
        var stop = Plan(new ActionRequest(name, ActionKind.Stop));
        if (stop.IsBlocked) return new ActionPlan { Request = request, Safety = stop.Safety, Blocked = stop.Blocked, BlockedDetail = stop.BlockedDetail };

        var changes = new List<ServiceChange>(disable.Changes);
        changes.AddRange(stop.Changes);
        if (target.Triggers.Count > 0)
            changes.Add(new(target.Name, ChangeKind.Triggers, ChangeCodec.Triggers(target.Triggers), ChangeCodec.Triggers([])));
        if (ChangeCodec.RecoveryKey(target.Recovery) != "none")
            changes.Add(new(target.Name, ChangeKind.Recovery, ChangeCodec.Recovery(target.Recovery), ChangeCodec.Recovery(ChangeCodec.NoRecovery(target.Recovery))));
        foreach (var task in linkedTasks?.Invoke(target) ?? [])
            if (task.Enabled)
                changes.Add(new(target.Name, ChangeKind.TaskEnabled, "True", "False", task.Path));

        return new ActionPlan
        {
            Request = request,
            Safety = (SafetyLevel)Math.Max((int)disable.Safety, (int)stop.Safety),
            Changes = changes,
            DependentsToStop = stop.DependentsToStop,
        };
    }

    /// <summary>
    /// Verrouillage ACL (spec §5.2, Expert) : désactive le service si besoin, puis interdit l'écriture de la valeur Start.
    /// </summary>
    public ActionPlan PlanLock(string name)
    {
        var request = new ActionRequest(name, ActionKind.LockAcl);
        var target = control.Read(name);
        if (target is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };
        var disable = Plan(new ActionRequest(name, ActionKind.SetStartMode, ServiceStartMode.Disabled));
        if (disable.IsBlocked) return new ActionPlan { Request = request, Safety = disable.Safety, Blocked = disable.Blocked, BlockedDetail = disable.BlockedDetail };
        if (target.RegistryKeySddl is not { } sddl) return new ActionPlan { Request = request, Blocked = BlockReason.AclUnreadable };
        if (AclLock.IsLocked(sddl)) return new ActionPlan { Request = request, Safety = disable.Safety };

        var changes = new List<ServiceChange>(disable.Changes)
        {
            new(target.Name, ChangeKind.RegistryAcl, AclLock.Normalize(sddl), AclLock.Lock(sddl)),
        };
        return new ActionPlan { Request = request, Safety = disable.Safety, Changes = changes };
    }

    public ActionPlan PlanUnlock(string name)
    {
        var request = new ActionRequest(name, ActionKind.UnlockAcl);
        var target = control.Read(name);
        if (target is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };
        if (target.RegistryKeySddl is not { } sddl) return new ActionPlan { Request = request, Blocked = BlockReason.AclUnreadable };
        if (!AclLock.IsLocked(sddl)) return new ActionPlan { Request = request };
        return new ActionPlan
        {
            Request = request,
            Safety = SafetyLists.ForService(target.TemplateName),
            Changes = [new(target.Name, ChangeKind.RegistryAcl, AclLock.Normalize(sddl), AclLock.Unlock(sddl))],
        };
    }

    /// <summary>Windows arrondit la taille d'un journal au multiple de 64 Ko : on compare l'état et la taille à 64 Ko près.</summary>
    private static bool EventLogMatches(string actual, string expected) =>
        Network.EventLogSetting.Parse(actual) is { } a && Network.EventLogSetting.Parse(expected) is { } e
        && a.Enabled == e.Enabled && Math.Abs(a.MaxSize - e.MaxSize) < 65536;

    // ── v1.1 : journal DNS (page Réseau) ──────────────────────
    /// <summary>
    /// Active (ou désactive) le journal du client DNS de Windows, avec une taille suffisante pour garder plusieurs jours
    /// de requêtes. Réglage sauvegardé dans l'historique et annulable comme le reste.
    /// </summary>
    public ActionPlan PlanDnsLog(bool enable)
    {
        var request = new ActionRequest("", ActionKind.ConfigureDnsLog);
        var before = Network.EventLogSetting.Parse(control.GetEventLog(Network.DnsLog.LogName));
        if (before is null) return new ActionPlan { Request = request, Blocked = BlockReason.NotFound };
        var after = enable
            ? new Network.EventLogSetting(true, Math.Max(before.MaxSize, Network.DnsLog.RecommendedMaxSize))
            : before with { Enabled = false };
        if (after == before) return new ActionPlan { Request = request };
        return new ActionPlan
        {
            Request = request,
            Changes = [new("", ChangeKind.EventLog, before.Encode(), after.Encode(), Network.DnsLog.LogName)],
        };
    }

    private static string NormalizeState(ServiceRunState s) =>
        IsRunningOrPending(s) ? nameof(ServiceRunState.Running) : nameof(ServiceRunState.Stopped);

    private static bool IsRunningOrPending(ServiceRunState s) =>
        s is ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.ContinuePending
            or ServiceRunState.Paused or ServiceRunState.PausePending;

    private static string TemplateOf(string name) =>
        new ServiceInfo { Name = name, ServiceType = name.Contains('_') ? 0xE0u : 0u }.TemplateName;
}
