using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Diagnostics.Eventing.Reader;
using SerWin.App.Infrastructure;
using SerWin.Core.Events;
using SerWin.Core.Platform;
using SerWin.Core.Revivers;
using SerWin.Core.Services;
using SerWin.Core.Tasks;
using Wpf.Ui.Controls;

namespace SerWin.App.ViewModels;

/// <summary>v0.3 — Qui le relance : analyse statique, chronologie, alertes.</summary>
public sealed partial class MainViewModel
{
    private TaskIndex _taskIndex = TaskIndex.Empty;
    private List<ServiceEvent> _serviceEvents = [];
    private ManagementContext _management = new(false, false);
    private bool _reviverDataLoaded;
    private int _analysisVersion;

    public ObservableCollection<FindingViewModel> Findings { get; } = [];
    public ObservableCollection<TimelineItemViewModel> Timeline { get; } = [];
    public ObservableCollection<AlertViewModel> Alerts { get; } = [];

    private bool _isAnalyzing;
    public bool IsAnalyzing { get => _isAnalyzing; private set => Set(ref _isAnalyzing, value); }

    public bool HasFindings => Findings.Count > 0;
    public bool HasTimeline => Timeline.Count > 0;
    public bool HasAlerts => Alerts.Count > 0;
    public string AlertsNavLabel => Alerts.Count == 0 ? L["Nav_Alerts"] : $"{L["Nav_Alerts"]} ({Alerts.Count})";

    private bool? _taskLogEnabled;
    public bool ShowTaskLogBanner => _taskLogEnabled == false;
    public string TaskInventoryText => _taskIndex.Tasks.Count == 0 ? ""
        : L.Format(IsElevated ? "Rev_TasksRead" : "Rev_TasksReadLimited", _taskIndex.Tasks.Count);

    public ICommand EnableTaskLogCommand { get; private set; } = null!;
    public ICommand ShowAlertCommand { get; private set; } = null!;

    private void InitRevivers()
    {
        EnableTaskLogCommand = new RelayCommand(EnableTaskLog, _ => IsElevated && !IsBusy);
        ShowAlertCommand = new RelayCommand(p =>
        {
            if (p is not string name) return;
            SearchText = "";
            SelectedFilter = Filters[0];
            SelectedService = Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            CurrentPage = "services";
            Raise(nameof(NavIndex));
            SelectedTabIndex = WhoRevivesTabIndex;
        });
    }

    private const int WhoRevivesTabIndex = 1;

    /// <summary>État de toutes les tâches, pour la photographie du premier lancement (spec §5.4).</summary>
    private static IEnumerable<SerWin.Core.Snapshot.TaskState>? TaskStatesForSnapshot()
    {
        try
        {
            return TaskIndex.Load().Tasks.Select(t => new SerWin.Core.Snapshot.TaskState(t.Path, t.Enabled)).ToList();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Tâches planifiées liées (sauvegardées avec le service avant chaque modification).</summary>
    private IEnumerable<ScheduledTaskInfo> LinkedTasksFor(ServiceInfo s) =>
        _taskIndex.FindLinked(s, _kb.FindService(s.TemplateName)?.Revivers.Where(r => r.Type == "task").Select(r => r.Target)).Select(l => l.Task);

    /// <summary>Charge en arrière-plan tâches, journaux et contexte de gestion, puis calcule alertes et analyse.</summary>
    private async Task LoadReviverDataAsync()
    {
        IsAnalyzing = true;
        try
        {
            (_taskIndex, _serviceEvents, _management, _taskLogEnabled) = await Task.Run(() =>
            {
                TaskIndex tasks;
                try { tasks = TaskIndex.Load(); }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { tasks = TaskIndex.Empty; }
                return (tasks, ServiceEventReader.ReadServiceEvents(), ManagementContext.Detect(), ServiceEventReader.IsTaskSchedulerLogEnabled());
            });
            _reviverDataLoaded = true;
            Raise(nameof(ShowTaskLogBanner));
            Raise(nameof(TaskInventoryText));
            RecomputeAlerts();
            await AnalyzeSelectedAsync();
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private void RecomputeAlerts()
    {
        if (!_reviverDataLoaded) return;
        var history = History.Select(h => h.Entry).ToList();
        var alerts = RevivalDetector.Detect(_serviceEvents, history, Services.Select(s => s.Info).ToList());
        Alerts.Clear();
        foreach (var a in alerts) Alerts.Add(new AlertViewModel(a, DisplayNameOf));
        var revived = alerts.Select(a => a.Service).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Services) s.IsRevived = revived.Contains(s.Name);
        Raise(nameof(HasAlerts));
        Raise(nameof(AlertsNavLabel));
        if (SelectedFilter.Value == ServiceFilter.Revived) ApplyFilter();
    }

    /// <summary>Analyse du service sélectionné. Une sélection plus récente annule l'affichage d'une analyse en cours.</summary>
    private async Task AnalyzeSelectedAsync()
    {
        var version = ++_analysisVersion;
        Findings.Clear();
        Timeline.Clear();
        RaiseAnalysis();
        if (!_reviverDataLoaded || SelectedService is not { } svc) return;

        var known = svc.Entry?.Revivers.Select(r => new KnownReviver(r.Type, r.Target, r.Note?.Resolve(_ctx.Language))).ToList() ?? [];
        var all = Services.Select(s => s.Info).ToList();
        var history = History.Select(h => h.Entry).ToList();
        var includeTasks = _taskLogEnabled == true;
        var info = svc.Info;

        IsAnalyzing = true;
        try
        {
            var (findings, timeline) = await Task.Run(() => (
                ReviverAnalyzer.Analyze(info, all, _taskIndex, known, _management),
                TimelineBuilder.Build(info, _serviceEvents, history, includeTasks)));
            if (version != _analysisVersion) return;
            foreach (var f in findings) Findings.Add(new FindingViewModel(f, DisplayNameOf));
            foreach (var t in timeline) Timeline.Add(new TimelineItemViewModel(t, DisplayNameOf));
            RaiseAnalysis();
        }
        finally
        {
            if (version == _analysisVersion) IsAnalyzing = false;
        }
    }

    private void RaiseAnalysis()
    {
        Raise(nameof(HasFindings));
        Raise(nameof(HasTimeline));
    }

    /// <summary>Après une action : relire les journaux (SerWin vient d'y écrire un 7040) et recalculer.</summary>
    private async Task RefreshReviverDataAfterActionAsync()
    {
        if (!_reviverDataLoaded) return;
        _serviceEvents = await Task.Run(() => ServiceEventReader.ReadServiceEvents());
        RecomputeAlerts();
        await AnalyzeSelectedAsync();
    }

    private async void EnableTaskLog()
    {
        try
        {
            await Task.Run(ServiceEventReader.EnableTaskSchedulerLog);
            _taskLogEnabled = ServiceEventReader.IsTaskSchedulerLogEnabled();
            Raise(nameof(ShowTaskLogBanner));
            Notify(InfoBarSeverity.Success, L["TaskLog_Enabled"], L["TaskLog_EnabledHelp"]);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            Notify(InfoBarSeverity.Error, L["TaskLog_Failed"], ex.Message);
        }
    }
}
