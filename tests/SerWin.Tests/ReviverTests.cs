using System.Xml.Linq;
using SerWin.Core.Actions;
using SerWin.Core.Events;
using SerWin.Core.Revivers;
using SerWin.Core.Services;
using SerWin.Core.Tasks;

namespace SerWin.Tests;

public class ServiceEventParsingTests
{
    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static XElement Event(int id, string utc, string? userSid, string data) => XElement.Parse($"""
        <Event xmlns='{Ns}'><System><Provider Name='Service Control Manager'/><EventID Qualifiers='16384'>{id}</EventID>
        <TimeCreated SystemTime='{utc}'/><Channel>System</Channel>{(userSid is null ? "<Security/>" : $"<Security UserID='{userSid}'/>")}</System>
        <EventData>{data}</EventData></Event>
        """);

    [Fact]
    public void Parses_7040_with_localized_start_types()
    {
        // Échantillon réel (Windows 11 26300, français) : DiagTrack désactivé par l'utilisateur.
        var e = ServiceEventReader.Parse(Event(7040, "2026-11-09T12:20:47Z", "S-1-5-18",
            "<Data Name='param1'>Expériences des utilisateurs connectés et télémétrie</Data><Data Name='param2'>Démarrage automatique</Data>" +
            "<Data Name='param3'>désactivé</Data><Data Name='param4'>DiagTrack</Data>"))!;
        Assert.Equal(ServiceEventKind.StartModeChanged, e.Kind);
        Assert.Equal("DiagTrack", e.ServiceName);
        Assert.Equal(ServiceStartMode.Automatic, e.OldMode);
        Assert.Equal(ServiceStartMode.Disabled, e.NewMode);
        Assert.True(e.IsBySystem);
    }

    [Fact]
    public void Parses_7031_service_name_from_binary()
    {
        var e = ServiceEventReader.Parse(Event(7031, "2026-11-07T13:54:22Z", null,
            "<Data Name='param1'>Microsoft Office Click-to-Run Service</Data><Data Name='param2'>2</Data><Data Name='param3'>0</Data>" +
            "<Data Name='param4'>1</Data><Data Name='param5'>Redémarrer le service</Data>" +
            "<Binary>43006C00690063006B0054006F00520075006E005300760063000000</Binary>"))!;
        Assert.Equal(ServiceEventKind.CrashedWithRecovery, e.Kind);
        Assert.Equal("ClickToRunSvc", e.ServiceName);
        Assert.Equal(2, e.CrashCount);
        Assert.Equal("Redémarrer le service", e.RecoveryAction);
    }

    [Fact]
    public void Parses_7036_state_from_binary()
    {
        var hex = Convert.ToHexString(System.Text.Encoding.Unicode.GetBytes("wuauserv/4\0"));
        var e = ServiceEventReader.Parse(Event(7036, "2026-11-07T13:54:22Z", null,
            $"<Data Name='param1'>Windows Update</Data><Data Name='param2'>en cours d’exécution</Data><Binary>{hex}</Binary>"))!;
        Assert.Equal(ServiceEventKind.Started, e.Kind);
        Assert.Equal("wuauserv", e.ServiceName);
    }

    [Theory]
    [InlineData("Démarrage automatique", ServiceStartMode.Automatic)]
    [InlineData("Démarrage à la demande", ServiceStartMode.Manual)]
    [InlineData("désactivé", ServiceStartMode.Disabled)]
    [InlineData("auto start", ServiceStartMode.Automatic)]
    [InlineData("demand start", ServiceStartMode.Manual)]
    [InlineData("disabled", ServiceStartMode.Disabled)]
    [InlineData("boot start", ServiceStartMode.Boot)]
    public void Normalizes_localized_start_modes(string raw, ServiceStartMode expected) =>
        Assert.Equal(expected, ServiceEventReader.ParseStartMode(raw));

    [Fact]
    public void Unknown_language_gives_null_so_raw_text_is_shown() =>
        Assert.Null(ServiceEventReader.ParseStartMode("自動"));
}

public class RevivalDetectorTests
{
    private static readonly DateTime T = new(2026, 10, 27, 2, 1, 0);

    private static ServiceEvent Change(string svc, ServiceStartMode from, ServiceStartMode to, DateTime at, string sid = "S-1-5-18") =>
        new() { At = at, EventId = 7040, Kind = ServiceEventKind.StartModeChanged, ServiceName = svc, OldMode = from, NewMode = to, UserSid = sid };

    private static ServiceInfo Svc(string name, ServiceStartMode mode) => new() { Name = name, StartMode = mode };

    [Fact]
    public void Windows_reenabling_a_disabled_service_raises_an_alert()
    {
        var alerts = RevivalDetector.Detect([Change("wuauserv", ServiceStartMode.Disabled, ServiceStartMode.Manual, T)], [],
            [Svc("wuauserv", ServiceStartMode.Manual)]);
        var a = Assert.Single(alerts);
        Assert.Equal(RevivalSource.EventLog, a.Source);
        Assert.Equal(ServiceStartMode.Manual, a.NewMode);
    }

    [Fact]
    public void Change_made_by_serwin_is_not_an_alert()
    {
        var history = new HistoryEntry
        {
            At = new DateTimeOffset(T.AddSeconds(2)),
            Action = ActionKind.SetStartMode,
            Changes = [new("wuauserv", ChangeKind.StartMode, "Disabled", "Manual")],
        };
        Assert.Empty(RevivalDetector.Detect([Change("wuauserv", ServiceStartMode.Disabled, ServiceStartMode.Manual, T)], [history],
            [Svc("wuauserv", ServiceStartMode.Manual)]));
    }

    [Fact]
    public void Service_disabled_again_since_is_not_an_alert() =>
        Assert.Empty(RevivalDetector.Detect([Change("wuauserv", ServiceStartMode.Disabled, ServiceStartMode.Manual, T)], [],
            [Svc("wuauserv", ServiceStartMode.Disabled)]));

    [Fact]
    public void Serwin_disable_that_did_not_hold_is_an_alert()
    {
        var history = new HistoryEntry
        {
            At = new DateTimeOffset(T),
            Action = ActionKind.SetStartMode,
            Changes = [new("DiagTrack", ChangeKind.StartMode, "Automatic", "Disabled")],
        };
        var a = Assert.Single(RevivalDetector.Detect([], [history], [Svc("DiagTrack", ServiceStartMode.Automatic)]));
        Assert.Equal(RevivalSource.SerWinMismatch, a.Source);

        // Annulée par l'utilisateur : plus d'alerte.
        Assert.Empty(RevivalDetector.Detect([], [history with { UndoneAt = DateTimeOffset.Now }], [Svc("DiagTrack", ServiceStartMode.Automatic)]));
    }
}

public class TimelineTests
{
    private static readonly DateTime T = new(2026, 10, 27, 2, 1, 8);

    [Fact]
    public void Correlates_tasks_updates_and_other_services()
    {
        var svc = new ServiceInfo { Name = "wuauserv", DisplayName = "Windows Update" };
        var events = new List<ServiceEvent>
        {
            new() { At = T, Kind = ServiceEventKind.StartModeChanged, ServiceName = "wuauserv", OldMode = ServiceStartMode.Disabled, NewMode = ServiceStartMode.Manual },
            new() { At = T.AddSeconds(-1), Kind = ServiceEventKind.StartModeChanged, ServiceName = "BITS", OldMode = ServiceStartMode.Automatic, NewMode = ServiceStartMode.Manual },
            new() { At = T.AddHours(-3), Kind = ServiceEventKind.StartModeChanged, ServiceName = "Far", OldMode = ServiceStartMode.Manual, NewMode = ServiceStartMode.Automatic },
        };
        var entry = Assert.Single(TimelineBuilder.Build(svc, events, [], includeTaskRuns: true,
            taskRuns: (_, _) => [new TaskRunEvent(T.AddSeconds(-30), 100, @"\Microsoft\Windows\WaaSMedic\PerformRemediation", null)],
            updates: (_, _) => [new UpdateEvent(T.AddMinutes(-40), 19, "Mise à jour cumulative 2026-10")]));

        Assert.Contains(entry.Clues, c => c.Kind == ClueKind.Task && c.Subject.EndsWith("PerformRemediation"));
        Assert.Contains(entry.Clues, c => c.Kind == ClueKind.WindowsUpdate);
        Assert.Contains(entry.Clues, c => c.Kind == ClueKind.OtherService && c.Subject == "BITS");
        Assert.DoesNotContain(entry.Clues, c => c.Subject == "Far");
    }

    [Fact]
    public void Serwin_own_change_is_identified_and_not_blamed_on_others()
    {
        var svc = new ServiceInfo { Name = "DiagTrack" };
        var ev = new ServiceEvent { At = T, Kind = ServiceEventKind.StartModeChanged, ServiceName = "DiagTrack" };
        var h = new HistoryEntry { At = new DateTimeOffset(T.AddSeconds(1)), Changes = [new("DiagTrack", ChangeKind.StartMode, "Automatic", "Disabled")] };
        var entry = Assert.Single(TimelineBuilder.Build(svc, [ev], [h], includeTaskRuns: true,
            taskRuns: (_, _) => throw new InvalidOperationException("ne doit pas chercher de tâche"),
            updates: (_, _) => throw new InvalidOperationException("ne doit pas chercher de mise à jour")));
        Assert.Equal(ClueKind.SerWin, Assert.Single(entry.Clues).Kind);
    }
}

public class ReviverAnalyzerTests
{
    private static readonly ScheduledTaskInfo OrchestratorTask = new()
    {
        Path = @"\Microsoft\Windows\UpdateOrchestrator\Schedule Scan",
        Enabled = true,
        Triggers = ["Tous les jours"],
        Actions = [new TaskActionInfo { Kind = TaskActionKind.Exec, Path = @"C:\WINDOWS\system32\usoclient.exe", Arguments = "StartScan" }],
    };

    private static readonly ScheduledTaskInfo ScStartTask = new()
    {
        Path = @"\Vendor\Restart",
        Enabled = false,
        Actions = [new TaskActionInfo { Kind = TaskActionKind.Exec, Path = @"C:\WINDOWS\system32\sc.exe", Arguments = "start wuauserv" }],
    };

    private static readonly ScheduledTaskInfo Unrelated = new()
    {
        Path = @"\Other\wuauservX",
        Enabled = true,
        Actions = [new TaskActionInfo { Kind = TaskActionKind.Exec, Path = @"C:\x.exe", Arguments = "--name wuauservExtra" }],
    };

    [Fact]
    public void Finds_all_seven_families()
    {
        var target = new ServiceInfo
        {
            Name = "wuauserv",
            StartMode = ServiceStartMode.Manual,
            ServiceDll = @"C:\WINDOWS\system32\wuaueng.dll",
            Triggers = [new ServiceTrigger { Kind = TriggerKind.GroupPolicy, Action = TriggerAction.Start, SubtypeName = "MachinePolicyPresent" }],
            Recovery = new RecoveryConfig { Actions = [new RecoveryAction(RecoveryActionType.Restart, 60000)] },
            DependentServices = ["Dep"],
        };
        var all = new List<ServiceInfo>
        {
            target,
            new() { Name = "Dep", StartMode = ServiceStartMode.Automatic },
            new() { Name = "WaaSMedicSvc", StartMode = ServiceStartMode.Manual },
        };
        var known = new List<KnownReviver>
        {
            new("task", @"\Microsoft\Windows\UpdateOrchestrator\*"),
            new("policy", @"HKLM\SOFTWARE\SerWinTests\DoesNotExist"),
            new("update", "cumulative-update"),
        };

        var findings = ReviverAnalyzer.Analyze(target, all, TaskIndex.FromTasks([OrchestratorTask, ScStartTask, Unrelated]), known,
            new ManagementContext(false, false));

        Assert.Contains(findings, f => f.Kind == ReviverKind.Trigger && f.Strength == ReviverStrength.Active);
        Assert.Contains(findings, f => f.Kind == ReviverKind.Recovery && f.Strength == ReviverStrength.Possible);
        Assert.Contains(findings, f => f.Kind == ReviverKind.Dependent && f.Subject == "Dep" && f.Strength == ReviverStrength.Active);
        Assert.Contains(findings, f => f.Kind == ReviverKind.Task && f.TaskReason == TaskLinkReason.KnowledgePattern && f.Strength == ReviverStrength.Active);
        Assert.Contains(findings, f => f.Kind == ReviverKind.Task && f.TaskReason == TaskLinkReason.NameInCommand && f.Strength == ReviverStrength.Inactive);
        Assert.DoesNotContain(findings, f => f.Subject == Unrelated.Path);
        Assert.Contains(findings, f => f.Kind == ReviverKind.RepairService && f.Subject == "WaaSMedicSvc");
        Assert.Contains(findings, f => f.Kind == ReviverKind.Policy && f.Strength == ReviverStrength.Inactive);
        Assert.Contains(findings, f => f.Kind == ReviverKind.Known);
        // Les mécanismes actifs viennent en premier.
        Assert.Equal(ReviverStrength.Active, findings[0].Strength);
    }

    [Fact]
    public void Disabled_service_makes_triggers_and_dependents_inactive()
    {
        var target = new ServiceInfo
        {
            Name = "X",
            StartMode = ServiceStartMode.Disabled,
            Triggers = [new ServiceTrigger { Kind = TriggerKind.DeviceInterfaceArrival, Action = TriggerAction.Start }],
            DependentServices = ["Dep"],
        };
        var findings = ReviverAnalyzer.Analyze(target, [target, new ServiceInfo { Name = "Dep", StartMode = ServiceStartMode.Automatic }],
            TaskIndex.Empty, [], new ManagementContext(false, false));
        Assert.All(findings, f => Assert.Equal(ReviverStrength.Inactive, f.Strength));
    }

    [Fact]
    public void Task_launching_the_service_binary_is_linked()
    {
        var target = new ServiceInfo { Name = "MyAgent", BinaryPath = "\"C:\\Program Files\\Vendor\\agent.exe\" --service" };
        var task = new ScheduledTaskInfo
        {
            Path = @"\Vendor\Watchdog",
            Enabled = true,
            Actions = [new TaskActionInfo { Kind = TaskActionKind.Exec, Path = @"C:\Program Files\Vendor\agent.exe" }],
        };
        var link = Assert.Single(TaskIndex.FromTasks([task]).FindLinked(target));
        Assert.Equal(TaskLinkReason.SameBinary, link.Reason);
    }
}

/// <summary>Lecture réelle (sans droits admin) des tâches et du journal Système.</summary>
public class LiveReviverTests
{
    [Fact]
    public void Reads_scheduled_tasks_and_service_events()
    {
        var tasks = TaskIndex.Load();
        Assert.NotEmpty(tasks.Tasks);
        Assert.All(tasks.Tasks, t => Assert.StartsWith(@"\", t.Path));

        var events = ServiceEventReader.ReadServiceEvents();
        Assert.All(events.Where(e => e.Kind == ServiceEventKind.StartModeChanged), e => Assert.NotNull(e.ServiceName));
    }
}
