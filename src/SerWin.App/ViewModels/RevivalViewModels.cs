using System.Windows.Media;
using SerWin.App.Localization;
using SerWin.Core.Events;
using SerWin.Core.Revivers;
using SerWin.Core.Services;

namespace SerWin.App.ViewModels;

/// <summary>Un mécanisme de relance possible (analyse statique), avec libellés localisés.</summary>
public sealed class FindingViewModel(ReviverFinding f, Func<string, string> displayName)
{
    private static readonly Loc L = Loc.Instance;

    public ReviverFinding Finding { get; } = f;

    public string StrengthLabel => L[$"RevStrength_{f.Strength}"];
    public Brush StrengthBrush => f.Strength switch
    {
        ReviverStrength.Active => Palette.Caution,
        ReviverStrength.Possible => Palette.Running,
        _ => Palette.Neutral,
    };
    public double Opacity => f.Strength == ReviverStrength.Inactive ? 0.6 : 1.0;

    public string Title => f.Kind switch
    {
        ReviverKind.Trigger => L.Format("Rev_Trigger", ServiceItemViewModel.FormatTrigger(f.Trigger!)),
        ReviverKind.Recovery => L.Format("Rev_Recovery", f.Subject),
        ReviverKind.Dependent => L.Format("Rev_Dependent", displayName(f.Subject), L[$"StartMode_{f.Detail}"]),
        ReviverKind.Task => L.Format("Rev_Task", f.Subject),
        ReviverKind.RepairService => L.Format("Rev_Repair", displayName(f.Subject)),
        ReviverKind.Policy when f.Subject is "domain-gpo" or "mdm" => L[$"Rev_Managed_{f.Subject.Replace('-', '_')}"],
        ReviverKind.Policy => L.Format("Rev_Policy", f.Subject),
        ReviverKind.Known => L.Format("Rev_Known", L.TryGet($"Known_{f.Subject.Replace('-', '_')}") ?? f.Subject),
        _ => f.Subject,
    };

    public string? Detail => f.Kind switch
    {
        ReviverKind.Trigger => L["RevDetail_Trigger"],
        ReviverKind.Recovery => L.Format("RevDetail_Recovery", ServiceItemViewModel.FormatDuration(uint.TryParse(f.Detail, out var s) ? s : 0)),
        ReviverKind.Dependent => L["RevDetail_Dependent"],
        ReviverKind.Task when f.Task is { } t => string.Join(" · ", new[]
        {
            L[$"TaskReason_{f.TaskReason}"],
            t.Enabled ? L["Task_Enabled"] : L["Task_Disabled"],
            t.LastRun is { } lr ? L.Format("Task_LastRun", lr.ToString("g", L.Culture)) : null,
            t.Actions.Count > 0 ? string.Join(", ", t.Actions.Select(a => a.ToString())) : null,
        }.Where(x => !string.IsNullOrEmpty(x))),
        ReviverKind.RepairService => f.Detail ?? L["RevDetail_Repair"],
        ReviverKind.Policy when f.Subject is "domain-gpo" or "mdm" => null,
        ReviverKind.Policy => f.Strength == ReviverStrength.Inactive ? L["RevDetail_PolicyAbsent"]
            : string.IsNullOrEmpty(f.Detail) ? L["RevDetail_PolicyEmpty"] : f.Detail,
        ReviverKind.Known => L.TryGet($"KnownDetail_{f.Subject.Replace('-', '_')}"),
        _ => f.Detail,
    };
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>Un événement de la chronologie et ses indices de corrélation.</summary>
public sealed class TimelineItemViewModel(TimelineEntry entry, Func<string, string> displayName)
{
    private static readonly Loc L = Loc.Instance;

    public string When => entry.Event.At.ToString("g", L.Culture);

    public string Title
    {
        get
        {
            var e = entry.Event;
            return e.Kind switch
            {
                ServiceEventKind.StartModeChanged => L.Format("Tl_StartMode", Mode(e.OldMode, e.RawOld), Mode(e.NewMode, e.RawNew)),
                ServiceEventKind.Started => L["Tl_Started"],
                ServiceEventKind.Stopped => L["Tl_Stopped"],
                ServiceEventKind.Crashed => L.Format("Tl_Crashed", e.CrashCount),
                ServiceEventKind.CrashedWithRecovery => L.Format("Tl_CrashedRecovery", e.CrashCount, e.RecoveryAction),
                ServiceEventKind.Installed => L.Format("Tl_Installed", e.ImagePath),
                _ => e.Kind.ToString(),
            };
        }
    }

    public Brush Brush => entry.Event.Kind switch
    {
        ServiceEventKind.StartModeChanged when entry.Event.OldMode == ServiceStartMode.Disabled => Palette.Caution,
        ServiceEventKind.Crashed or ServiceEventKind.CrashedWithRecovery => Palette.Blocked,
        _ => Palette.Running,
    };

    public string? By => entry.Event.Account is null ? null
        : entry.Event.IsBySystem ? L.Format("Tl_BySystem", entry.Event.Account)
        : L.Format("Tl_By", entry.Event.Account);
    public bool HasBy => By is not null;

    public IReadOnlyList<string> Clues => entry.Clues.Count == 0
        ? entry.Event.Kind == ServiceEventKind.StartModeChanged ? [L["Clue_None"]] : []
        : entry.Clues.Select(Describe).ToList();

    private string Describe(Clue c) => c.Kind switch
    {
        ClueKind.SerWin => L.Format("Clue_SerWin", c.Detail),
        ClueKind.Task => L.Format("Clue_Task", c.At.ToString("T", L.Culture), c.Subject),
        ClueKind.OtherService => L.Format("Clue_OtherService", c.At.ToString("T", L.Culture), displayName(c.Subject), LocalizeChange(c.Detail)),
        ClueKind.WindowsUpdate => L.Format("Clue_Update", c.At.ToString("g", L.Culture), c.Subject),
        ClueKind.Installation => L.Format("Clue_Installation", c.At.ToString("T", L.Culture), c.Subject),
        _ => c.Subject,
    };

    private static string LocalizeChange(string? detail)
    {
        if (detail is null) return "";
        var parts = detail.Split(" → ");
        return parts.Length == 2 ? $"{Mode(Enum.TryParse<ServiceStartMode>(parts[0], out var a) ? a : null, parts[0])} → {Mode(Enum.TryParse<ServiceStartMode>(parts[1], out var b) ? b : null, parts[1])}" : detail;
    }

    private static string Mode(ServiceStartMode? m, string? raw) => m is { } v ? L[$"StartMode_{v}"] : raw ?? "?";
}

/// <summary>Alerte « désactivé puis relancé ».</summary>
public sealed class AlertViewModel(RevivalAlert alert, Func<string, string> displayName)
{
    private static readonly Loc L = Loc.Instance;

    public RevivalAlert Alert { get; } = alert;
    public string Service => alert.Service;
    public string Title => displayName(alert.Service);
    public string When => alert.At.ToString("g", L.Culture);
    public string Message => alert.Source == RevivalSource.SerWinMismatch
        ? L.Format("Alert_SerWinMismatch", L[$"StartMode_{alert.NewMode}"])
        : L.Format("Alert_EventLog", L[$"StartMode_{alert.NewMode}"], alert.Account ?? "?");
}
