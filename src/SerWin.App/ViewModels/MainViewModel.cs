using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Actions;
using SerWin.Core.Platform;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;
using SerWin.Core.Tasks;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

public enum ServiceFilter
{
    All,
    Running,
    Disabled,
    ThirdParty,
    Undocumented,
    Privacy,
    Revived,
    NotUseful,
    DiffersFromDefault,
}

/// <summary>Option de liste déroulante dont le libellé suit la langue.</summary>
public sealed class Option<T>(T value, string key) : ObservableObject
{
    public T Value { get; } = value;
    public string Label => Loc.Instance[key];
    public void Refresh() => Raise(nameof(Label));
}

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    private readonly AppSettings _settings;
    private readonly KnowledgeBase _kb;
    private readonly SnapshotStore _snapshots = new(DataDirectory.Default);
    private readonly IDialogService _dialogs;
    private readonly ActionEngine _engine;
    private readonly DisplayContext _ctx;
    private SystemSnapshot? _snapshot;
    private string? _snapshotError;

    public MainViewModel(AppSettings settings, IDialogService dialogs)
    {
        _settings = settings;
        _kb = KnowledgeBase.LoadEmbedded();
        Os = OsInfo.Detect();
        IsElevated = OsInfo.IsElevated();
        _ctx = new DisplayContext { Os = Os, Level = settings.Level };

        ServicesView = CollectionViewSource.GetDefaultView(Services);
        ServicesView.Filter = o => o is ServiceItemViewModel s && PassesFilter(s);
        // Tri par nom affiché, qui dépend de la langue de l'interface (voir ServiceItemViewModel.DisplayName).
        ServicesView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(ServiceItemViewModel.DisplayName), System.ComponentModel.ListSortDirection.Ascending));

        Levels =
        [
            new(ComplexityLevel.Simple, "Level_Simple"),
            new(ComplexityLevel.Avance, "Level_Avance"),
            new(ComplexityLevel.Expert, "Level_Expert"),
        ];
        _selectedLevel = Levels.First(l => l.Value == settings.Level);

        Filters = Enum.GetValues<ServiceFilter>().Select(f => new Option<ServiceFilter>(f, $"Filter_{f}")).ToList();
        _selectedFilter = Filters[0];

        Languages =
        [
            new(null, "Language_System"),
            new("fr", "Language_French"),
            new("en", "Language_English"),
        ];
        _selectedLanguage = Languages.FirstOrDefault(l => l.Value == settings.Language) ?? Languages[0];

        RefreshCommand = new RelayCommand(async () => await LoadAsync());
        CopyCommand = new RelayCommand(p => { if (p is string s && s.Length > 0) TryCopy(s); });
        NavigateCommand = new RelayCommand(p => CurrentPage = p as string ?? "services");
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder);

        L.LanguageChanged += (_, _) => RefreshLabels();
        _dialogs = dialogs;
        _engine = new ActionEngine(new WindowsServiceControl(), new HistoryStore(DataDirectory.Default), new BackupStore(DataDirectory.Default), LinkedTasksFor);
        InitActions();
        InitRevivers();
        InitProcesses();
        InitSuggestions();
        InitExpert();
    }

    // ── Données ──────────────────────────────────────────────
    public OsInfo Os { get; }
    public bool IsElevated { get; }
    public ObservableCollection<ServiceItemViewModel> Services { get; } = [];
    public ICollectionView ServicesView { get; }

    private ServiceItemViewModel? _selectedService;
    public ServiceItemViewModel? SelectedService
    {
        get => _selectedService;
        set
        {
            if (Set(ref _selectedService, value))
            {
                Raise(nameof(HasSelection));
                OnSelectedServiceChanged();
            }
        }
    }
    public bool HasSelection => SelectedService is not null;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    private string? _loadError;
    public string? LoadError { get => _loadError; private set { Set(ref _loadError, value); Raise(nameof(HasLoadError)); } }
    public bool HasLoadError => LoadError is not null;

    // ── Navigation ───────────────────────────────────────────
    /// <summary>Index dans la barre de navigation : 0 = Services, 5 = Réglages (1-4 : versions futures).</summary>
    public int NavIndex
    {
        get => CurrentPage switch { "settings" => 5, "history" => 4, "alerts" => 3, "suggestions" => 2, "processes" => 1, _ => 0 };
        set
        {
            CurrentPage = value switch { 5 => "settings", 4 => "history", 3 => "alerts", 2 => "suggestions", 1 => "processes", _ => "services" };
            Raise();
        }
    }

    /// <summary>Service à sélectionner au chargement (argument --select, conservé lors d'une relance élevée).</summary>
    public string? InitialSelection { get; set; }

    private string _currentPage = "services";
    public string CurrentPage
    {
        get => _currentPage;
        set
        {
            if (Set(ref _currentPage, value))
            {
                Raise(nameof(IsServicesPage));
                Raise(nameof(IsSettingsPage));
                Raise(nameof(IsHistoryPage));
                Raise(nameof(IsAlertsPage));
                Raise(nameof(IsProcessesPage));
                Raise(nameof(IsSuggestionsPage));
                if (IsHistoryPage) _ = RefreshHistoryAsync();
            }
        }
    }
    public bool IsServicesPage => CurrentPage == "services";
    public bool IsSettingsPage => CurrentPage == "settings";
    public bool IsHistoryPage => CurrentPage == "history";
    public bool IsAlertsPage => CurrentPage == "alerts";

    // ── Recherche et filtres ─────────────────────────────────
    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value))
            {
                ApplyFilter();
                RebuildProcessView();
            }
        }
    }

    public IReadOnlyList<Option<ServiceFilter>> Filters { get; }
    private Option<ServiceFilter> _selectedFilter;
    public Option<ServiceFilter> SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is not null && Set(ref _selectedFilter, value)) ApplyFilter();
        }
    }

    private bool PassesFilter(ServiceItemViewModel s)
    {
        var ok = SelectedFilter.Value switch
        {
            ServiceFilter.Running => s.IsRunning,
            ServiceFilter.Disabled => s.IsDisabled,
            ServiceFilter.ThirdParty => s.IsThirdParty,
            ServiceFilter.Undocumented => s.IsUndocumented,
            ServiceFilter.Privacy => s.IsPrivacy,
            ServiceFilter.Revived => s.IsRevived,
            ServiceFilter.NotUseful => s.IsNotUseful,
            ServiceFilter.DiffersFromDefault => s.DiffersFromDefault,
            _ => true,
        };
        return ok && s.Matches(SearchText.Trim());
    }

    private void ApplyFilter()
    {
        ServicesView.Refresh();
        Raise(nameof(CountText));
    }

    public string CountText
    {
        get
        {
            var visible = ServicesView.Cast<object>().Count();
            return L.Format("List_Count", visible, Services.Count);
        }
    }

    // ── Niveau et langue ─────────────────────────────────────
    public IReadOnlyList<Option<ComplexityLevel>> Levels { get; }
    private Option<ComplexityLevel> _selectedLevel;
    public Option<ComplexityLevel> SelectedLevel
    {
        get => _selectedLevel;
        set
        {
            if (value is null || !Set(ref _selectedLevel, value)) return;
            _ctx.Level = value.Value;
            _settings.Level = value.Value;
            _settings.Save();
            foreach (var s in Services) s.Refresh();
            RebuildStartModeOptions();
            RefreshExpertLabels();
            // L'onglet Technique disparaît en mode Simple : on revient sur « Comprendre ».
            if (value.Value == ComplexityLevel.Simple && SelectedTabIndex == TechnicalTabIndex) SelectedTabIndex = 0;
        }
    }

    private const int TechnicalTabIndex = 3;
    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

    public IReadOnlyList<Option<string?>> Languages { get; }
    private Option<string?> _selectedLanguage;
    public Option<string?> SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value is null || !Set(ref _selectedLanguage, value)) return;
            _settings.Language = value.Value;
            _settings.Save();
            L.SetLanguage(value.Value);
        }
    }

    private void RefreshLabels()
    {
        foreach (var o in Levels) o.Refresh();
        foreach (var o in Filters) o.Refresh();
        foreach (var o in Languages) o.Refresh();
        foreach (var s in Services) s.Refresh();
        ServicesView.Refresh();
        Raise(nameof(CountText));
        Raise(nameof(OsText));
        Raise(nameof(KnowledgeText));
        Raise(nameof(SnapshotText));
        Raise(nameof(PrivilegeText));
        Raise(nameof(DataFolderText));
        Raise(nameof(AppVersionText));
        RefreshActionLabels();
        RecomputeAlerts();
        _ = AnalyzeSelectedAsync();
        Raise(nameof(TaskInventoryText));
        foreach (var o in ProcessViewModes) o.Refresh();
        foreach (var p in _processItems.Values) p.Refresh();
        Raise(nameof(ProcessCountText));
        UpdateServiceConsumption();
        RebuildFacts();
        BuildSuggestions();
    }

    // ── Barre d'état / Réglages ──────────────────────────────
    public static string AppVersion =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.1.0";

    public string AppVersionText => L.Format("About_Version", AppVersion);
    public string OsText => L.Format("Status_Os", Os.FriendlyName, Os.DisplayVersion, Os.FullBuild);
    public string KnowledgeText => L.Format("Status_Knowledge", _kb.Metadata.Version, _kb.ServiceCount);
    public string PrivilegeText => IsElevated ? L["Status_Admin"] : L["Status_Standard"];
    public string DataFolderText => _snapshots.DataDirectory;

    public string SnapshotText =>
        _snapshotError is not null ? L.Format("Status_SnapshotError", _snapshotError)
        : _snapshot is null ? L["Status_SnapshotPending"]
        : L.Format("Status_Snapshot", _snapshot.TakenAt.LocalDateTime.ToString("g", L.Culture));

    // ── Commandes ────────────────────────────────────────────
    public ICommand RefreshCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand NavigateCommand { get; }
    public ICommand OpenDataFolderCommand { get; }

    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        LoadError = null;
        var selectedName = SelectedService?.Name ?? InitialSelection;
        try
        {
            var infos = await Task.Run(() => new ServiceReader().ReadAll());
            Services.Clear();
            foreach (var info in infos)
                Services.Add(new ServiceItemViewModel(info, _kb.FindService(info.TemplateName), _ctx));
            ApplyFilter();
            SelectedService = Services.FirstOrDefault(s => s.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase))
                              ?? ServicesView.Cast<ServiceItemViewModel>().FirstOrDefault();

            // Photographie au premier lancement (spec §5.4) — jamais écrasée ensuite.
            try
            {
                _snapshot = await Task.Run(() => _snapshots.EnsureCreated(Os, infos, AppVersion, _snapshots.Exists ? null : TaskStatesForSnapshot()));
                _snapshotError = null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _snapshotError = ex.Message;
            }
            Raise(nameof(SnapshotText));
            if (IsElevated) TryHardenDataDirectory();
            await RefreshHistoryAsync();
            _ = LoadReviverDataAsync();
            StartSampling();
            _ = LoadContextAsync();
        }
        catch (Exception ex)
        {
            LoadError = L.Format("Error_Load", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static void TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Presse-papiers momentanément verrouillé par une autre application.
        }
    }

    private void OpenDataFolder()
    {
        if (!Directory.Exists(_snapshots.DataDirectory)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_snapshots.DataDirectory}\"") { UseShellExecute = true });
    }
}
