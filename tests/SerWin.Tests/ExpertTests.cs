using SerWin.Core.Actions;
using SerWin.Core.Platform;
using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Core.Tasks;

namespace SerWin.Tests;

public class AclLockTests
{
    // Descripteur réel de la clé Spooler relevé sur Windows 11 26300.
    private const string Sddl = "O:SYD:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)(A;CIIOID;KA;;;CO)(A;CIID;KR;;;AC)";

    [Fact]
    public void Lock_and_unlock_round_trip()
    {
        Assert.False(AclLock.IsLocked(Sddl));
        var locked = AclLock.Lock(Sddl);
        Assert.True(AclLock.IsLocked(locked));
        Assert.Contains("(D;;", locked);
        var unlocked = AclLock.Unlock(locked);
        Assert.False(AclLock.IsLocked(unlocked));
        Assert.Equal(AclLock.Normalize(Sddl), unlocked);
    }

    [Fact]
    public void Garbage_is_not_locked()
    {
        Assert.False(AclLock.IsLocked(null));
        Assert.False(AclLock.IsLocked("pas un SDDL"));
    }
}

public sealed class NeutralizeTests : IDisposable
{
    private const string Sddl = "O:SYD:AI(A;CIID;KR;;;BU)(A;CIID;KA;;;BA)(A;CIID;KA;;;SY)";
    private const string TaskPath = @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-expert-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _scm = new();
    private readonly ActionEngine _engine;

    private static readonly ServiceTrigger GpTrigger = new()
    {
        Kind = TriggerKind.GroupPolicy, Action = TriggerAction.Start, Subtype = new Guid("659fcae6-5bdb-4da9-b1ff-ca2a178d46e0"),
        SubtypeName = "MachinePolicyPresent", RawType = 5, RawAction = 1,
    };

    private static readonly ServiceTrigger PipeTrigger = new()
    {
        Kind = TriggerKind.NetworkEndpoint, Action = TriggerAction.Start, Subtype = new Guid("1f81d131-3fac-4537-9e0c-7e7b0c2f4b55"),
        RawType = 6, RawAction = 1, RawData = [new TriggerDataRaw(2, Convert.ToBase64String("winreg\0"u8.ToArray()))],
    };

    public NeutralizeTests()
    {
        _scm.Services["wuauserv"] = new ServiceInfo
        {
            Name = "wuauserv", StartMode = ServiceStartMode.Manual, State = ServiceRunState.Running,
            Triggers = [GpTrigger, PipeTrigger],
            Recovery = new RecoveryConfig { ResetPeriodSeconds = 86400, Actions = [new(RecoveryActionType.Restart, 60000)] },
            RegistryKeySddl = Sddl,
        };
        _scm.Services["RpcSs"] = new ServiceInfo { Name = "RpcSs", StartMode = ServiceStartMode.Automatic, RegistryKeySddl = Sddl };
        _scm.Tasks[TaskPath] = true;
        var linked = new ScheduledTaskInfo { Path = TaskPath, Enabled = true };
        _engine = new ActionEngine(_scm, new HistoryStore(_dir), new BackupStore(_dir),
            s => s.Name == "wuauserv" ? [linked] : []);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Neutralize_plans_every_mechanism()
    {
        var plan = _engine.PlanNeutralize("wuauserv");
        Assert.Equal(
            [ChangeKind.StartMode, ChangeKind.State, ChangeKind.Triggers, ChangeKind.Recovery, ChangeKind.TaskEnabled],
            plan.Changes.Select(c => c.Kind));
        Assert.Equal(TaskPath, plan.Changes.Single(c => c.Kind == ChangeKind.TaskEnabled).Target);
    }

    [Fact]
    public void Neutralize_then_undo_restores_triggers_byte_for_byte()
    {
        var result = _engine.Execute(_engine.PlanNeutralize("wuauserv"));
        Assert.True(result.Success, result.ErrorMessage);
        var s = _scm.Services["wuauserv"];
        Assert.Equal(ServiceStartMode.Disabled, s.StartMode);
        Assert.Equal(ServiceRunState.Stopped, s.State);
        Assert.Empty(s.Triggers);
        Assert.Equal("none", ChangeCodec.RecoveryKey(s.Recovery));
        Assert.Equal(86400u, s.Recovery!.ResetPeriodSeconds);
        Assert.False(_scm.Tasks[TaskPath]);

        Assert.True(_engine.Execute(_engine.PlanUndo(result.Entry!.Id)).Success);
        s = _scm.Services["wuauserv"];
        Assert.Equal(ServiceStartMode.Manual, s.StartMode);
        Assert.Equal(ServiceRunState.Running, s.State);
        Assert.Equal(ChangeCodec.TriggerKey([GpTrigger, PipeTrigger]), ChangeCodec.TriggerKey(s.Triggers));
        Assert.Equal(RecoveryActionType.Restart, Assert.Single(s.Recovery!.Actions).Type);
        Assert.True(_scm.Tasks[TaskPath]);
    }

    [Fact]
    public void Lock_prevents_any_rewrite_of_start_and_undo_unlocks_first()
    {
        var lockResult = _engine.Execute(_engine.PlanLock("wuauserv"));
        Assert.True(lockResult.Success, lockResult.ErrorMessage);
        Assert.Equal(["mode wuauserv Disabled", "acl wuauserv lock"], _scm.Calls);
        Assert.True(_scm.Services["wuauserv"].IsAclLocked);

        // Windows (ou un réparateur) tente de réactiver : refusé.
        Assert.Throws<ServiceActionException>(() => _scm.SetStartMode("wuauserv", ServiceStartMode.Manual));

        _scm.Calls.Clear();
        Assert.True(_engine.Execute(_engine.PlanUndo(lockResult.Entry!.Id)).Success);
        Assert.Equal(["acl wuauserv unlock", "mode wuauserv Manual"], _scm.Calls);
        Assert.False(_scm.Services["wuauserv"].IsAclLocked);
    }

    [Fact]
    public void Unlock_plan_and_noops()
    {
        Assert.True(_engine.PlanUnlock("wuauserv").IsNoOp);
        _engine.Execute(_engine.PlanLock("wuauserv"));
        Assert.True(_engine.PlanLock("wuauserv").IsNoOp);
        var unlock = _engine.PlanUnlock("wuauserv");
        Assert.Equal(ChangeKind.RegistryAcl, Assert.Single(unlock.Changes).Kind);
        Assert.True(_engine.Execute(unlock).Success);
        Assert.False(_scm.Services["wuauserv"].IsAclLocked);
    }

    [Fact]
    public void Red_list_cannot_be_neutralized_or_locked()
    {
        Assert.Equal(BlockReason.RedList, _engine.PlanNeutralize("RpcSs").Blocked);
        Assert.Equal(BlockReason.RedList, _engine.PlanLock("RpcSs").Blocked);
    }

    [Fact]
    public void Lock_without_readable_acl_is_refused()
    {
        _scm.Services["wuauserv"] = _scm.Services["wuauserv"] with { RegistryKeySddl = null };
        Assert.Equal(BlockReason.AclUnreadable, _engine.PlanLock("wuauserv").Blocked);
    }

    [Fact]
    public void Live_trigger_codec_round_trip_on_real_services()
    {
        foreach (var s in new ServiceReader().ReadAll().Where(s => s.Triggers.Count > 0).Take(20))
            Assert.Equal(ChangeCodec.TriggerKey(s.Triggers), ChangeCodec.TriggerKey(ChangeCodec.Triggers(ChangeCodec.Triggers(s.Triggers))));
    }
}

/// <summary>Tests réels sur le service Fax (sans risque), remis dans son état exact. Administrateur uniquement.</summary>
public class ElevatedExpertTests
{
    private const string Victim = "Fax";

    [AdminFact]
    public void Real_triggers_and_recovery_can_be_removed_and_restored()
    {
        var control = new WindowsServiceControl();
        var original = control.Read(Victim)!;
        try
        {
            var pipe = new ServiceTrigger
            {
                Kind = TriggerKind.NetworkEndpoint, Action = TriggerAction.Start, Subtype = new Guid("1f81d131-3fac-4537-9e0c-7e7b0c2f4b55"),
                RawType = 6, RawAction = 1, RawData = [new TriggerDataRaw(2, Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("serwintest\0")))],
            };
            control.SetTriggers(Victim, [pipe]);
            var read = control.Read(Victim)!;
            Assert.Equal(ChangeCodec.TriggerKey([pipe]), ChangeCodec.TriggerKey(read.Triggers));

            control.SetRecovery(Victim, new RecoveryConfig { ResetPeriodSeconds = 3600, Actions = [new(RecoveryActionType.Restart, 1000), new(RecoveryActionType.None, 0)] });
            Assert.Equal(RecoveryActionType.Restart, control.Read(Victim)!.Recovery!.Actions[0].Type);
        }
        finally
        {
            control.SetTriggers(Victim, original.Triggers);
            control.SetRecovery(Victim, original.Recovery ?? new RecoveryConfig());
        }
        var after = control.Read(Victim)!;
        Assert.Equal(ChangeCodec.TriggerKey(original.Triggers), ChangeCodec.TriggerKey(after.Triggers));
        Assert.Equal(ChangeCodec.RecoveryKey(original.Recovery), ChangeCodec.RecoveryKey(after.Recovery));
    }

    [AdminFact]
    public void Real_acl_lock_blocks_the_scm_and_unlocks()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-elevated-" + Guid.NewGuid().ToString("N"));
        var control = new WindowsServiceControl();
        var original = control.Read(Victim)!;
        var engine = new ActionEngine(control, new HistoryStore(dir), new BackupStore(dir));
        try
        {
            var lockResult = engine.Execute(engine.PlanLock(Victim));
            Assert.True(lockResult.Success, lockResult.ErrorMessage);
            Assert.True(control.Read(Victim)!.IsAclLocked);
            var ex = Assert.Throws<ServiceActionException>(() => control.SetStartMode(Victim, ServiceStartMode.Manual));
            Assert.Equal(ServiceActionError.AccessDenied, ex.Code);

            Assert.True(engine.Execute(engine.PlanUndo(lockResult.Entry!.Id)).Success);
            Assert.False(control.Read(Victim)!.IsAclLocked);
            Assert.Equal(original.StartMode, control.Read(Victim)!.StartMode);
        }
        finally
        {
            if (control.Read(Victim)!.IsAclLocked) control.SetRegistryAcl(Victim, AclLock.Unlock(control.Read(Victim)!.RegistryKeySddl!));
            control.SetStartMode(Victim, original.StartMode);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
