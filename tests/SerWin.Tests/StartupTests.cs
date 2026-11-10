using SerWin.Core.Actions;
using SerWin.Core.Startup;

namespace SerWin.Tests;

public class StartupApprovalTests
{
    [Fact]
    public void Decodes_values_written_by_windows()
    {
        // Valeurs réelles relevées dans HKCU\…\StartupApproved\Run.
        var steam = Convert.FromHexString("030000000485BA294506DC01");
        var (enabled, at) = StartupApproval.Parse(steam);
        Assert.False(enabled);
        Assert.NotNull(at);
        Assert.True(at!.Value.Year >= 2020);

        Assert.True(StartupApproval.Parse(Convert.FromHexString("020000000000000000000000")).Enabled);
        Assert.True(StartupApproval.Parse(Convert.FromHexString("060000000000000000000000")).Enabled);
        Assert.False(StartupApproval.Parse(Convert.FromHexString("070000000000000000000000")).Enabled);
        Assert.True(StartupApproval.Parse(null).Enabled); // absent = activé
    }

    [Fact]
    public void Encodes_like_task_manager()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0);
        var off = StartupApproval.Encode(false, now);
        Assert.Equal(12, off.Length);
        Assert.Equal(3, off[0]);
        Assert.Equal(now, StartupApproval.Parse(off).DisabledAt);
        Assert.Equal("020000000000000000000000", Convert.ToHexString(StartupApproval.Encode(true, now)));
        Assert.Null(StartupApproval.FromText("absent"));
    }
}

public sealed class StartupActionTests : IDisposable
{
    private const string Target = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run|Discord";
    private const string TaskPath = @"\GoogleUpdaterTaskSystem";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-startup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _scm = new();
    private readonly ActionEngine _engine;
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);

    private static StartupItem Discord(bool enabled) =>
        new(StartupSource.RunUser, "Discord", "Discord.exe --start-inactive", @"C:\Discord.exe", enabled, null, Target);

    public StartupActionTests()
    {
        Directory.CreateDirectory(_dir);
        _engine = new ActionEngine(_scm, new HistoryStore(_dir), new BackupStore(_dir));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Disabling_writes_the_approval_value_and_undo_restores_absence()
    {
        var plan = _engine.PlanStartup(Discord(true), enable: false, Now);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeKind.StartupApproved, change.Kind);
        Assert.Equal("absent", change.Before);

        var result = _engine.Execute(plan);
        Assert.True(result.Success);
        Assert.False(StartupApproval.IsEnabled(_scm.StartupValues[Target]));

        var undo = _engine.Execute(_engine.PlanUndo(result.Entry!.Id));
        Assert.True(undo.Success);
        Assert.False(_scm.StartupValues.ContainsKey(Target)); // revenu exactement à « absent »
    }

    [Fact]
    public void Re_enabling_an_already_enabled_item_is_a_no_op()
    {
        Assert.True(_engine.PlanStartup(Discord(true), enable: true, Now).IsNoOp);
        _scm.StartupValues[Target] = "030000000485BA294506DC01";
        Assert.Single(_engine.PlanStartup(Discord(false), enable: true, Now).Changes);
    }

    [Fact]
    public void Startup_task_can_be_disabled_and_restored()
    {
        _scm.Tasks[TaskPath] = true;
        var result = _engine.Execute(_engine.PlanTask(TaskPath, enable: false));
        Assert.False(_scm.Tasks[TaskPath]);
        _engine.Execute(_engine.PlanUndo(result.Entry!.Id));
        Assert.True(_scm.Tasks[TaskPath]);
        Assert.Equal(BlockReason.NotFound, _engine.PlanTask(@"\Absente", enable: false).Blocked);
    }
}
