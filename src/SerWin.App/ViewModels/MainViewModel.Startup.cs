using System.Collections.ObjectModel;
using System.Windows.Input;
using SerWin.App.Infrastructure;
using SerWin.Core.Actions;
using SerWin.Core.Services;
using SerWin.Core.Startup;
using SerWin.Core.Tasks;

namespace SerWin.App.ViewModels;

public enum StartupFilter
{
    All,
    /// <summary>Pistes d'optimisation : désactiver ou différer.</summary>
    Suggested,
    Programs,
    Tasks,
    Services,
    Disabled,
}

/// <summary>
/// v1.1 — Démarrage : programmes à l'ouverture de session, tâches au démarrage et services tiers automatiques, avec des
/// pistes d'optimisation. Désactiver un programme suit exactement la méthode de Windows (StartupApproved) ; différer un
/// service utilise le « démarrage automatique différé » natif. Tout passe par le moteur d'actions : sauvegardé, annulable.
/// </summary>
public sealed partial class MainViewModel
{
    private List<StartupEntryViewModel> _startupAll = [];
    private bool _startupLoading;

    public bool IsStartupPage => CurrentPage == "startup";
    public ObservableCollection<StartupEntryViewModel> StartupEntries { get; } = [];

    public IReadOnlyList<Option<StartupFilter>> StartupFilters { get; private set; } = [];
    private Option<StartupFilter>? _selectedStartupFilter;
    public Option<StartupFilter>? SelectedStartupFilter
    {
        get => _selectedStartupFilter;
        set
        {
            if (value is not null && Set(ref _selectedStartupFilter, value)) RebuildStartupView();
        }
    }

    private StartupEntryViewModel? _selectedStartupEntry;
    public StartupEntryViewModel? SelectedStartupEntry
    {
        get => _selectedStartupEntry;
        set
        {
            if (!Set(ref _selectedStartupEntry, value)) return;
            Raise(nameof(HasSelectedStartupEntry));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    public bool HasSelectedStartupEntry => SelectedStartupEntry is not null;

    public ICommand ToggleStartupCommand { get; private set; } = null!;
    public ICommand DelayServiceCommand { get; private set; } = null!;
    public ICommand ShowStartupOverviewCommand { get; private set; } = null!;
    public ICommand ShowNetworkByExeCommand { get; private set; } = null!;
    public ICommand ShowProcessByExeCommand { get; private set; } = null!;

    // ── Vue d'ensemble ───────────────────────────────────────
    private string _bootText = "";
    public string BootText { get => _bootText; private set => Set(ref _bootText, value); }
    public bool HasBootText => BootText.Length > 0;

    public string StartupSummary => L.Format("Startup_Summary",
        _startupAll.Count(e => e.Kind == StartupEntryKind.Program && e.Enabled),
        _startupAll.Count(e => e.Kind == StartupEntryKind.Program && !e.Enabled),
        _startupAll.Count(e => e.Kind == StartupEntryKind.Task && e.Enabled),
        _startupAll.Count(e => e.Kind == StartupEntryKind.Service));
    public string StartupSuggestionText => L.Format("Startup_SuggestionCount", _startupAll.Count(e => e.IsSuggested));
    public IReadOnlyList<StartupEntryViewModel> SlowestEntries => _startupAll
        .Where(e => e.Degradation is { TotalSeconds: >= 0.5 })
        .OrderByDescending(e => e.Degradation).Take(5).ToList();
    public bool HasSlowestEntries => SlowestEntries.Count > 0;

    private void InitStartup()
    {
        StartupFilters = Enum.GetValues<StartupFilter>().Select(f => new Option<StartupFilter>(f, $"StartupFilter_{f}")).ToList();
        _selectedStartupFilter = StartupFilters[0];
        ToggleStartupCommand = new RelayCommand(async () => await ToggleStartupAsync(),
            _ => IsElevated && !IsBusy && SelectedStartupEntry is { CanToggle: true });
        DelayServiceCommand = new RelayCommand(async () => await DelayServiceAsync(),
            _ => IsElevated && !IsBusy && SelectedStartupEntry is { Kind: StartupEntryKind.Service, Enabled: true });
        ShowStartupOverviewCommand = new RelayCommand(() => SelectedStartupEntry = null);
        // Liens croisés par nom d'exécutable (le programme n'a pas toujours de service).
        ShowProcessByExeCommand = new RelayCommand(p =>
        {
            if (p is StartupEntryViewModel { ExecutableName: { } exe }) ShowProcess(new NetworkSenderItem(exe, null, null, 0, 0, false, 0, exe));
        });
        ShowNetworkByExeCommand = new RelayCommand(p =>
        {
            if (p is StartupEntryViewModel { ExecutableName: { } exe } e)
                ShowNetwork(e.Service is { } s ? s : new NetworkSenderItem(exe, null, null, 0, 0, false, 0, exe));
        });
    }

    private void RefreshStartupLabels()
    {
        foreach (var o in StartupFilters) o.Refresh();
        foreach (var e in _startupAll) e.Refresh();
        RaiseStartupSummary();
    }

    private void RaiseStartupSummary()
    {
        Raise(nameof(StartupSummary));
        Raise(nameof(StartupSuggestionText));
        Raise(nameof(SlowestEntries));
        Raise(nameof(HasSlowestEntries));
        Raise(nameof(HasBootText));
    }

    /// <summary>Relit tout (programmes, tâches, services, mesures de démarrage) ; appelé à l'ouverture de la page et après une action.</summary>
    private async Task LoadStartupAsync()
    {
        if (_startupLoading || Services.Count == 0) return;
        _startupLoading = true;
        try
        {
            _ctx.Programs ??= await Task.Run(InstalledPrograms.Load);
            var tasks = _taskIndex.Tasks.Count > 0 ? _taskIndex : await Task.Run(() => TaskIndex.Load());
            if (_taskIndex.Tasks.Count == 0) _taskIndex = tasks;
            var elevated = IsElevated;
            var (items, boots, degradations) = await Task.Run(() =>
            (
                StartupReader.Read(),
                elevated ? BootPerformance.ReadBoots() : null,
                elevated ? BootPerformance.ReadDegradations(DateTime.Now.AddDays(-30)) : null
            ));

            var entries = new List<StartupEntryViewModel>();
            await Task.Run(() =>
            {
                entries.AddRange(items.Select(i => StartupEntryViewModel.FromProgram(i, _ctx, _kb)));
                // Tâches au démarrage hors dossier \Microsoft\ (celles de Windows ne sont pas des pistes d'optimisation).
                entries.AddRange(tasks.Tasks.Where(t => t.RunsAtStartup && !t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase))
                    .Select(t => StartupEntryViewModel.FromTask(t, _ctx, _kb)));
            });
            // Services tiers en démarrage automatique (éditeur connu et différent de Microsoft).
            entries.AddRange(Services
                .Where(s => s.Info.StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed
                            && !s.Info.IsPerUserInstance && s.Info.Publisher is { Length: > 0 } pub
                            && !pub.Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                .Select(s => StartupEntryViewModel.FromService(s, _ctx, _kb)));

            // Ralentissements attribués par Windows : rapprochés par nom d'exécutable.
            if (degradations is not null)
                foreach (var e in entries.Where(e => e.ExecutableName is not null))
                {
                    var match = degradations.Where(d => Path.GetFileName(d.FileName).Equals(e.ExecutableName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (match.Count > 0) e.Degradation = TimeSpan.FromMilliseconds(match.Average(d => d.Degradation.TotalMilliseconds));
                }

            BootText = boots is { Count: > 0 } b
                ? L.Format("Startup_Boot", b[0].BootTime.TotalSeconds.ToString("0", L.Culture), b[0].PostBoot.TotalSeconds.ToString("0", L.Culture),
                    b.Average(x => x.BootTime.TotalSeconds).ToString("0", L.Culture), b.Count)
                : elevated ? L["Startup_BootUnknown"] : "";

            var selected = SelectedStartupEntry?.Name;
            _startupAll = entries;
            RebuildStartupView();
            SelectedStartupEntry = selected is null ? null : StartupEntries.FirstOrDefault(e => e.Name == selected);
            RaiseStartupSummary();
        }
        finally
        {
            _startupLoading = false;
        }
    }

    private void RebuildStartupView()
    {
        var filter = SelectedStartupFilter?.Value ?? StartupFilter.All;
        var query = SearchText.Trim();
        var visible = _startupAll
            .Where(e => filter switch
            {
                StartupFilter.Suggested => e.IsSuggested,
                StartupFilter.Programs => e.Kind == StartupEntryKind.Program,
                StartupFilter.Tasks => e.Kind == StartupEntryKind.Task,
                StartupFilter.Services => e.Kind == StartupEntryKind.Service,
                StartupFilter.Disabled => !e.Enabled,
                _ => true,
            })
            .Where(e => e.Matches(query))
            .OrderBy(e => e.Kind)
            .ThenByDescending(e => e.IsSuggested)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        StartupEntries.Clear();
        foreach (var e in visible) StartupEntries.Add(e);
    }

    private async Task ToggleStartupAsync()
    {
        if (SelectedStartupEntry is not { } e) return;
        var enable = !e.Enabled;
        ActionPlan plan = e.Kind == StartupEntryKind.Task
            ? await Task.Run(() => _engine.PlanTask(e.Task!.Path, enable))
            : await Task.Run(() => _engine.PlanStartup(e.Item!, enable, DateTime.Now));
        var warnings = new List<string>();
        if (!enable && e.IsSecurity) warnings.Add(L["Startup_WarnSecurity"]);
        if (!enable) warnings.Add(L["Startup_WarnNothingDeleted"]);
        await RunPlanAsync(plan, L.Format(enable ? "History_StartupOn" : "History_StartupOff", e.Title),
            L[enable ? "Startup_Enable" : "Startup_Disable"], warnings, danger: false);
        await LoadStartupAsync();
    }

    private async Task DelayServiceAsync()
    {
        if (SelectedStartupEntry is not { Service: { } svc } e) return;
        var mode = e.IsDelayed ? ServiceStartMode.Automatic : ServiceStartMode.AutomaticDelayed;
        var plan = await Task.Run(() => _engine.Plan(new ActionRequest(svc.Name, ActionKind.SetStartMode, mode)));
        var warnings = new List<string>();
        if (mode == ServiceStartMode.AutomaticDelayed && e.IsSecurity) warnings.Add(L["Startup_WarnSecurity"]);
        await RunPlanAsync(plan, L.Format("History_SetStartMode", svc.DisplayName, L[$"StartMode_{mode}"]),
            L[e.IsDelayed ? "Startup_Undelay" : "Startup_Delay"], warnings, danger: false);
        await LoadStartupAsync();
    }
}
