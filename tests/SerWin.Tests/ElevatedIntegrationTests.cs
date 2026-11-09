using SerWin.Core.Actions;
using SerWin.Core.Platform;
using SerWin.Core.Services;

namespace SerWin.Tests;

/// <summary>Test ignoré (et signalé comme tel) hors session administrateur.</summary>
public sealed class AdminFactAttribute : FactAttribute
{
    public AdminFactAttribute()
    {
        if (!OsInfo.IsElevated()) Skip = "Nécessite une session administrateur (dotnet test depuis un terminal élevé).";
    }
}

/// <summary>
/// Tests sur le vrai gestionnaire de services. Ils modifient brièvement le service Fax (sans risque, démarrage
/// à la demande) puis le remettent dans son état exact, même en cas d'échec.
/// </summary>
public class ElevatedIntegrationTests
{
    private const string Victim = "Fax";

    [AdminFact]
    public void Changes_and_restores_start_mode_on_the_real_scm()
    {
        var control = new WindowsServiceControl();
        var before = control.Read(Victim);
        Assert.NotNull(before);
        var original = before!.StartMode;
        try
        {
            var target = original == ServiceStartMode.Disabled ? ServiceStartMode.Manual : ServiceStartMode.Disabled;
            control.SetStartMode(Victim, target);
            Assert.Equal(target, control.Read(Victim)!.StartMode);

            control.SetStartMode(Victim, ServiceStartMode.AutomaticDelayed);
            Assert.Equal(ServiceStartMode.AutomaticDelayed, control.Read(Victim)!.StartMode);
        }
        finally
        {
            control.SetStartMode(Victim, original);
        }
        Assert.Equal(original, control.Read(Victim)!.StartMode);
    }

    [AdminFact]
    public void Engine_records_backs_up_and_undoes_on_the_real_scm()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-elevated-" + Guid.NewGuid().ToString("N"));
        var control = new WindowsServiceControl();
        var original = control.Read(Victim)!.StartMode;
        try
        {
            var engine = new ActionEngine(control, new HistoryStore(dir), new BackupStore(dir));
            var target = original == ServiceStartMode.Disabled ? ServiceStartMode.Manual : ServiceStartMode.Disabled;
            var result = engine.Execute(engine.Plan(new ActionRequest(Victim, ActionKind.SetStartMode, target)));
            Assert.True(result.Success, result.ErrorMessage);

            var backup = engine.Backups.Load(result.Entry!.BackupFile!);
            Assert.Equal(original, backup!.Services[0].Service.StartMode);
            Assert.Contains("", backup.Services[0].Registry.Keys); // clé racine du service sauvegardée
            Assert.True(backup.Services[0].Registry[""].ContainsKey("Start"));

            Assert.True(engine.Execute(engine.PlanUndo(result.Entry.Id)).Success);
            Assert.Equal(original, control.Read(Victim)!.StartMode);
        }
        finally
        {
            control.SetStartMode(Victim, original);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [AdminFact]
    public void Red_list_service_is_never_touched_even_as_admin()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-elevated-" + Guid.NewGuid().ToString("N"));
        var engine = new ActionEngine(new WindowsServiceControl(), new HistoryStore(dir), new BackupStore(dir));
        var plan = engine.Plan(new ActionRequest("RpcSs", ActionKind.SetStartMode, ServiceStartMode.Disabled));
        Assert.Equal(BlockReason.RedList, plan.Blocked);
        Assert.Throws<InvalidOperationException>(() => engine.Execute(plan));
    }
}
