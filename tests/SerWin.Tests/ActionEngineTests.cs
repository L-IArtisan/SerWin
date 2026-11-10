using SerWin.Core.Actions;
using SerWin.Core.Platform;
using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;

namespace SerWin.Tests;

/// <summary>Faux SCM en mémoire : reproduit les règles utiles (un service désactivé ne démarre pas…).</summary>
internal sealed class FakeServiceControl : IServiceControl
{
    public Dictionary<string, ServiceInfo> Services { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    /// <summary>Simule un service protégé (WaaSMedicSvc…) : accès refusé même en admin.</summary>
    public HashSet<string> Protected { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Simule un « réparateur » qui remet immédiatement le type de démarrage.</summary>
    public Dictionary<string, ServiceStartMode> Revert { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeServiceControl Add(string name, ServiceStartMode mode, ServiceRunState state, params string[] dependents)
    {
        Services[name] = new ServiceInfo { Name = name, DisplayName = name, StartMode = mode, State = state, DependentServices = [.. dependents] };
        return this;
    }

    public ServiceInfo? Read(string name) => Services.GetValueOrDefault(name);
    public IReadOnlyList<ServiceInfo> ReadAll() => Services.Values.ToList();

    // ── v0.6 ─────────────────────────────────────────────────
    public Dictionary<string, bool> Tasks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetTriggers(string name, IReadOnlyList<ServiceTrigger> triggers)
    {
        Calls.Add($"triggers {name} {triggers.Count}");
        Services[name] = Services[name] with { Triggers = [.. triggers] };
    }

    public void SetRecovery(string name, RecoveryConfig recovery)
    {
        Calls.Add($"recovery {name} {recovery.Actions.Count}");
        Services[name] = Services[name] with { Recovery = recovery };
    }

    public bool? IsTaskEnabled(string path) => Tasks.TryGetValue(path, out var e) ? e : null;

    public void SetTaskEnabled(string path, bool enabled)
    {
        Calls.Add($"task {path} {enabled}");
        Tasks[path] = enabled;
    }

    public void SetRegistryAcl(string name, string sddl)
    {
        Calls.Add($"acl {name} {(AclLock.IsLocked(sddl) ? "lock" : "unlock")}");
        Services[name] = Services[name] with { RegistryKeySddl = sddl };
    }

    // ── v1.1 ─────────────────────────────────────────────────
    public Dictionary<string, string> EventLogs { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Simule l'arrondi de Windows : la taille d'un journal est un multiple de 64 Ko.</summary>
    public bool RoundLogSize { get; set; }

    public string? GetEventLog(string log) => EventLogs.GetValueOrDefault(log);

    public Dictionary<string, string> StartupValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? GetStartupApproved(string target) => StartupValues.GetValueOrDefault(target, "absent");
    public void SetStartupApproved(string target, string value)
    {
        Calls.Add($"startup {target} {value}");
        if (value == "absent") StartupValues.Remove(target); else StartupValues[target] = value;
    }

    public void SetEventLog(string log, string setting)
    {
        Calls.Add($"eventlog {log} {setting}");
        var s = SerWin.Core.Network.EventLogSetting.Parse(setting)!;
        if (RoundLogSize) s = s with { MaxSize = (s.MaxSize + 65535) / 65536 * 65536 };
        EventLogs[log] = s.Encode();
    }

    public void SetStartMode(string name, ServiceStartMode mode)
    {
        Calls.Add($"mode {name} {mode}");
        if (Protected.Contains(name)) throw new ServiceActionException(name, ServiceActionError.AccessDenied, 5, "Accès refusé");
        // Comme le vrai SCM : la clé verrouillée refuse l'écriture de Start.
        if (Services[name].IsAclLocked) throw new ServiceActionException(name, ServiceActionError.AccessDenied, 5, "Clé verrouillée");
        Services[name] = Services[name] with { StartMode = Revert.GetValueOrDefault(name, mode) };
    }

    public void Start(string name, TimeSpan timeout)
    {
        Calls.Add($"start {name}");
        if (Services[name].StartMode == ServiceStartMode.Disabled)
            throw new ServiceActionException(name, ServiceActionError.ServiceDisabled, 1058, "Service désactivé");
        Services[name] = Services[name] with { State = ServiceRunState.Running };
    }

    public void Stop(string name, TimeSpan timeout)
    {
        Calls.Add($"stop {name}");
        if (Services[name].DependentServices.Any(d => Services.TryGetValue(d, out var s) && s.State == ServiceRunState.Running))
            throw new ServiceActionException(name, ServiceActionError.DependentsRunning, 1051, "Dépendants actifs");
        Services[name] = Services[name] with { State = ServiceRunState.Stopped };
    }
}

public sealed class ActionEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-engine-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _scm = new();
    private readonly ActionEngine _engine;

    public ActionEngineTests()
    {
        _engine = new ActionEngine(_scm, new HistoryStore(_dir), new BackupStore(_dir));
        _scm.Add("Spooler", ServiceStartMode.Automatic, ServiceRunState.Running, "Fax")
            .Add("Fax", ServiceStartMode.Manual, ServiceRunState.Running)
            .Add("SysMain", ServiceStartMode.Disabled, ServiceRunState.Stopped)
            .Add("RpcSs", ServiceStartMode.Automatic, ServiceRunState.Running)
            .Add("WinDefend", ServiceStartMode.Automatic, ServiceRunState.Running)
            .Add("WaaSMedicSvc", ServiceStartMode.Manual, ServiceRunState.Stopped)
            .Add("wuauserv", ServiceStartMode.Manual, ServiceRunState.Stopped)
            .Add("Host", ServiceStartMode.Automatic, ServiceRunState.Running, "Schedule")
            .Add("Schedule", ServiceStartMode.Automatic, ServiceRunState.Running);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData(ActionKind.Stop, null)]
    [InlineData(ActionKind.Start, null)]
    [InlineData(ActionKind.SetStartMode, ServiceStartMode.Disabled)]
    public void Red_list_is_blocked_whatever_the_action(ActionKind kind, ServiceStartMode? mode)
    {
        var plan = _engine.Plan(new ActionRequest("RpcSs", kind, mode));
        Assert.Equal(BlockReason.RedList, plan.Blocked);
        Assert.Throws<InvalidOperationException>(() => _engine.Execute(plan));
        Assert.Empty(_scm.Calls);
    }

    [Fact]
    public void Stopping_a_service_with_a_red_dependent_is_blocked()
    {
        var plan = _engine.Plan(new ActionRequest("Host", ActionKind.Stop));
        Assert.Equal(BlockReason.RedDependent, plan.Blocked);
        Assert.Equal("Schedule", plan.BlockedDetail);
    }

    [Fact]
    public void Orange_list_requires_acknowledgement()
    {
        var plan = _engine.Plan(new ActionRequest("WinDefend", ActionKind.SetStartMode, ServiceStartMode.Manual));
        Assert.Equal(SafetyLevel.Orange, plan.Safety);
        Assert.True(plan.RequiresAcknowledgement);
        Assert.False(_engine.Plan(new ActionRequest("SysMain", ActionKind.Start)).RequiresAcknowledgement);
    }

    [Fact]
    public void Stop_includes_running_dependents_first()
    {
        var plan = _engine.Plan(new ActionRequest("Spooler", ActionKind.Stop));
        Assert.Equal(["Fax"], plan.DependentsToStop);
        var result = _engine.Execute(plan);
        Assert.True(result.Success);
        Assert.Equal(["stop Fax", "stop Spooler"], _scm.Calls);
    }

    [Fact]
    public void Starting_a_disabled_service_switches_it_to_manual_first()
    {
        var plan = _engine.Plan(new ActionRequest("SysMain", ActionKind.Start));
        Assert.Equal(2, plan.Changes.Count);
        var result = _engine.Execute(plan);
        Assert.True(result.Success);
        Assert.Equal(["mode SysMain Manual", "start SysMain"], _scm.Calls);
    }

    [Fact]
    public void Noop_when_already_in_requested_state()
    {
        Assert.True(_engine.Plan(new ActionRequest("Spooler", ActionKind.Start)).IsNoOp);
        Assert.True(_engine.Plan(new ActionRequest("SysMain", ActionKind.SetStartMode, ServiceStartMode.Disabled)).IsNoOp);
        Assert.Null(_engine.Execute(_engine.Plan(new ActionRequest("Spooler", ActionKind.Start))).Entry);
    }

    [Fact]
    public void Every_modification_is_backed_up_and_recorded()
    {
        var result = _engine.Execute(_engine.Plan(new ActionRequest("Spooler", ActionKind.SetStartMode, ServiceStartMode.Manual)));
        var entry = Assert.Single(_engine.History.All());
        Assert.Equal(result.Entry!.Id, entry.Id);
        Assert.Equal(ActionKind.SetStartMode, entry.Action);
        Assert.Equal(new ServiceChange("Spooler", ChangeKind.StartMode, "Automatic", "Manual"), Assert.Single(entry.Changes));

        var backup = _engine.Backups.Load(entry.BackupFile!);
        Assert.NotNull(backup);
        // La sauvegarde contient l'état AVANT modification.
        Assert.Equal(ServiceStartMode.Automatic, Assert.Single(backup!.Services).Service.StartMode);
    }

    [Fact]
    public void Undo_restores_previous_state_in_a_safe_order()
    {
        // Désactiver puis arrêter Spooler (et Fax) en deux actions…
        _engine.Execute(_engine.Plan(new ActionRequest("Spooler", ActionKind.SetStartMode, ServiceStartMode.Disabled)));
        var stop = _engine.Execute(_engine.Plan(new ActionRequest("Spooler", ActionKind.Stop))).Entry!;
        _scm.Calls.Clear();

        // …annuler l'arrêt seul échoue proprement : Spooler est encore désactivé par la première action.
        var undoStop = _engine.Execute(_engine.PlanUndo(stop.Id));
        Assert.Equal(ServiceActionError.ServiceDisabled, undoStop.Error);
        Assert.False(_engine.History.Get(stop.Id)!.IsUndone);

        // Annuler d'abord la désactivation, puis l'arrêt : tout revient.
        var disable = _engine.History.All().First(e => e.Action == ActionKind.SetStartMode);
        Assert.True(_engine.Execute(_engine.PlanUndo(disable.Id)).Success);
        Assert.True(_engine.Execute(_engine.PlanUndo(stop.Id)).Success);
        Assert.Equal(ServiceStartMode.Automatic, _scm.Services["Spooler"].StartMode);
        Assert.Equal(ServiceRunState.Running, _scm.Services["Spooler"].State);
        Assert.Equal(ServiceRunState.Running, _scm.Services["Fax"].State);
        Assert.True(_engine.History.Get(stop.Id)!.IsUndone);
        Assert.Equal(BlockReason.AlreadyUndone, _engine.PlanUndo(stop.Id).Blocked);
    }

    [Fact]
    public void Undo_reports_changes_made_by_something_else()
    {
        var entry = _engine.Execute(_engine.Plan(new ActionRequest("wuauserv", ActionKind.SetStartMode, ServiceStartMode.Disabled))).Entry!;
        // WaaSMedicSvc repasse Windows Update en Manuel dans notre dos.
        _scm.Services["wuauserv"] = _scm.Services["wuauserv"] with { StartMode = ServiceStartMode.Automatic };
        var result = _engine.Execute(_engine.PlanUndo(entry.Id));
        var drift = Assert.Single(result.Drifted);
        Assert.Equal("Automatic", drift.Before);
    }

    [Fact]
    public void Change_reverted_immediately_is_reported_as_not_effective()
    {
        _scm.Revert["wuauserv"] = ServiceStartMode.Manual;
        var result = _engine.Execute(_engine.Plan(new ActionRequest("wuauserv", ActionKind.SetStartMode, ServiceStartMode.Disabled)));
        Assert.False(result.Success);
        Assert.Single(result.NotEffective);
    }

    [Fact]
    public void Protected_service_failure_is_reported_and_nothing_recorded()
    {
        _scm.Protected.Add("WaaSMedicSvc");
        var result = _engine.Execute(_engine.Plan(new ActionRequest("WaaSMedicSvc", ActionKind.SetStartMode, ServiceStartMode.Disabled)));
        Assert.Equal(ServiceActionError.AccessDenied, result.Error);
        Assert.Null(result.Entry);
        Assert.Empty(_engine.History.All());
    }

    [Fact]
    public void Restore_all_returns_to_snapshot_except_red_list()
    {
        var snapshot = new SystemSnapshot
        {
            Os = new OsInfo(),
            Services = _scm.ReadAll().Select(s => s with { }).ToList(),
        };
        _engine.Execute(_engine.Plan(new ActionRequest("Spooler", ActionKind.SetStartMode, ServiceStartMode.Disabled)));
        _engine.Execute(_engine.Plan(new ActionRequest("SysMain", ActionKind.SetStartMode, ServiceStartMode.Manual)));
        // Un changement extérieur sur un service rouge n'est jamais touché par la restauration.
        _scm.Services["RpcSs"] = _scm.Services["RpcSs"] with { StartMode = ServiceStartMode.Manual };

        var plan = _engine.PlanRestoreAll(snapshot);
        Assert.Equal(2, plan.Changes.Count);
        Assert.DoesNotContain(plan.Changes, c => c.Service == "RpcSs");
        Assert.True(_engine.Execute(plan).Success);
        Assert.Equal(ServiceStartMode.Automatic, _scm.Services["Spooler"].StartMode);
        Assert.Equal(ServiceStartMode.Disabled, _scm.Services["SysMain"].StartMode);

        // Et « Tout restaurer » est lui-même annulable.
        var entry = _engine.History.All().First();
        Assert.Equal(ActionKind.RestoreAll, entry.Action);
        Assert.True(_engine.Execute(_engine.PlanUndo(entry.Id)).Success);
        Assert.Equal(ServiceStartMode.Disabled, _scm.Services["Spooler"].StartMode);
    }

    [Fact]
    public void Per_user_instance_start_mode_is_not_changeable()
    {
        _scm.Services["CDPUserSvc_8c16c"] = new ServiceInfo { Name = "CDPUserSvc_8c16c", ServiceType = 0xE0, StartMode = ServiceStartMode.Automatic };
        var plan = _engine.Plan(new ActionRequest("CDPUserSvc_8c16c", ActionKind.SetStartMode, ServiceStartMode.Disabled));
        Assert.Equal(BlockReason.PerUserInstance, plan.Blocked);
        Assert.Equal("CDPUserSvc", plan.BlockedDetail);
    }
}
