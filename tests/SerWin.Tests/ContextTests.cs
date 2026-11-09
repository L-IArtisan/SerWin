using SerWin.Core.Actions;
using SerWin.Core.Context;
using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.Tests;

public class UsefulnessRulesTests
{
    private static MachineContext Ctx(params (string Key, int? Count)[] facts) =>
        MachineContext.From(facts.ToDictionary(f => f.Key, f => new Fact(f.Count)));

    [Theory]
    [InlineData(">0", 2, true)]
    [InlineData(">0", 0, false)]
    [InlineData(">=2", 2, true)]
    [InlineData("true", 1, true)]
    [InlineData("present", 0, false)]
    [InlineData("installed", 1, true)]
    [InlineData("none", 0, true)]
    [InlineData("none", 1, false)]
    [InlineData("absent", 0, true)]
    [InlineData("0", 0, true)]
    public void Conditions(string expected, int count, bool result) =>
        Assert.Equal(result, UsefulnessRules.Check(expected, new Fact(count)));

    [Fact]
    public void Unknown_fact_gives_unknown_verdict()
    {
        var v = UsefulnessRules.Evaluate(new Dictionary<string, string> { ["printers"] = ">0" }, Ctx(("printers", null)));
        Assert.Equal(Usefulness.Unknown, v.Verdict);
    }

    [Fact]
    public void Any_satisfied_condition_makes_the_service_useful()
    {
        var rules = new Dictionary<string, string> { ["domainJoined"] = "true", ["mdmEnrolled"] = "true" };
        Assert.Equal(Usefulness.Useful, UsefulnessRules.Evaluate(rules, Ctx(("domainJoined", 0), ("mdmEnrolled", 1))).Verdict);
        Assert.Equal(Usefulness.NotUseful, UsefulnessRules.Evaluate(rules, Ctx(("domainJoined", 0), ("mdmEnrolled", 0))).Verdict);
        // Une seule condition inconnue empêche de conclure « inutile ».
        Assert.Equal(Usefulness.Unknown, UsefulnessRules.Evaluate(rules, Ctx(("domainJoined", 0), ("mdmEnrolled", null))).Verdict);
    }

    [Fact]
    public void No_rule_means_no_verdict() =>
        Assert.Equal(Usefulness.Unknown, UsefulnessRules.Evaluate(new Dictionary<string, string>(), Ctx()).Verdict);

    [Fact]
    public void Every_knowledge_rule_uses_a_detected_key_and_a_valid_condition()
    {
        var kb = KnowledgeBase.LoadEmbedded();
        foreach (var e in kb.Services.Concat(kb.Processes))
            foreach (var (key, expected) in e.UsefulIf)
            {
                Assert.True(MachineContext.Keys.All.Contains(key), $"{e.Name} : clé usefulIf inconnue « {key} »");
                Assert.True(UsefulnessRules.IsKnownCondition(expected), $"{e.Name} : condition illisible « {expected} »");
            }
    }

    [Fact]
    public void Live_detection_runs_without_admin()
    {
        var ctx = MachineContext.Detect();
        Assert.All(MachineContext.Keys.All, k => Assert.True(ctx.Facts.ContainsKey(k), k));
        Assert.NotNull(ctx[MachineContext.Keys.Touch].Count);
    }
}

public class FactoryDefaultsTests
{
    private static KnowledgeEntry Entry(params (string Key, string Start)[] defaults)
    {
        var e = new KnowledgeEntry { Name = "X" };
        foreach (var (k, s) in defaults) e.Defaults[k] = new DefaultConfig { StartType = s };
        return e;
    }

    [Fact]
    public void Exact_build_wins()
    {
        var d = Entry(("Professional:26100", "Manual"), ("Professional:26300", "Disabled")).DefaultFor("Professional", 26300, out var exact);
        Assert.True(exact);
        Assert.Equal("Disabled", d!.StartType);
    }

    [Fact]
    public void Falls_back_to_nearest_build_of_same_edition()
    {
        var d = Entry(("Professional:22631", "Automatic"), ("Professional:26100", "Manual"), ("Core:26300", "Disabled"))
            .DefaultFor("Professional", 26300, out var exact);
        Assert.False(exact);
        Assert.Equal("Manual", d!.StartType);
    }

    [Fact]
    public void Entries_without_start_type_are_ignored()
    {
        var e = new KnowledgeEntry { Name = "X" };
        e.Defaults["Professional:26300"] = new DefaultConfig { Triggers = ["GroupPolicy:MachinePolicyPresent"] };
        Assert.Null(e.DefaultFor("Professional", 26300, out _));
    }
}

public sealed class DisableManyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-sugg-" + Guid.NewGuid().ToString("N"));
    private readonly FakeServiceControl _scm = new();
    private readonly ActionEngine _engine;

    public DisableManyTests()
    {
        _engine = new ActionEngine(_scm, new HistoryStore(_dir), new BackupStore(_dir));
        _scm.Add("Fax", ServiceStartMode.Manual, ServiceRunState.Stopped)
            .Add("bthserv", ServiceStartMode.Manual, ServiceRunState.Running)
            .Add("RpcSs", ServiceStartMode.Automatic, ServiceRunState.Running)
            .Add("SysMain", ServiceStartMode.Disabled, ServiceRunState.Stopped);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Disables_and_stops_selected_services_in_one_undoable_entry()
    {
        var plan = _engine.PlanDisableMany(["Fax", "bthserv", "RpcSs", "SysMain"], stopNow: true, out var skipped);
        Assert.Equal(("RpcSs", BlockReason.RedList), Assert.Single(skipped));
        Assert.Equal(3, plan.Changes.Count); // Fax désactivé, bthserv désactivé + arrêté ; SysMain déjà désactivé.

        var result = _engine.Execute(plan);
        Assert.True(result.Success);
        Assert.Equal(ActionKind.ApplySuggestions, result.Entry!.Action);
        Assert.Equal(ServiceStartMode.Disabled, _scm.Services["bthserv"].StartMode);
        Assert.Equal(ServiceRunState.Stopped, _scm.Services["bthserv"].State);
        Assert.Equal(ServiceStartMode.Automatic, _scm.Services["RpcSs"].StartMode);

        Assert.True(_engine.Execute(_engine.PlanUndo(result.Entry.Id)).Success);
        Assert.Equal(ServiceStartMode.Manual, _scm.Services["bthserv"].StartMode);
        Assert.Equal(ServiceRunState.Running, _scm.Services["bthserv"].State);
        Assert.Equal(ServiceStartMode.Manual, _scm.Services["Fax"].StartMode);
    }

    [Fact]
    public void Without_stop_now_running_services_keep_running_until_reboot()
    {
        var plan = _engine.PlanDisableMany(["bthserv"], stopNow: false, out _);
        Assert.Equal(ChangeKind.StartMode, Assert.Single(plan.Changes).Kind);
    }
}

public class AntivirusDetectionTests
{
    [Theory]
    [InlineData("Windows Defender", "windowsdefender://", true)]
    [InlineData("Microsoft Defender Antivirus", null, true)]
    [InlineData("Bitdefender Antivirus", @"C:\Program Files\Bitdefender\Bitdefender Security\wscfix.exe", false)]
    [InlineData("Kaspersky", @"C:\Program Files (x86)\Kaspersky Lab\avp.exe", false)]
    public void Only_Microsoft_Defender_is_excluded(string name, string? path, bool isDefender)
    {
        Assert.Equal(isDefender, MachineContext.IsMicrosoftDefender(name, path));
    }
}
