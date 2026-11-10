using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Privacy;

namespace SerWin.App.ViewModels;

public enum PrivacyFilter
{
    All,
    InUse,
    Webcam,
    Microphone,
    Location,
}

/// <summary>Une application qui a utilisé la caméra, le micro ou la localisation.</summary>
public sealed class PrivacyUseViewModel(CapabilityUse use) : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    public CapabilityUse Use { get; } = use;
    public string Title => Use.DisplayName;
    public string Icon => IconOf(Use.Capability);
    public static string IconOf(string capability) => capability switch { "webcam" => "🎥", "microphone" => "🎙️", "location" => "📍", _ => "•" };
    public string CapabilityLabel => L[$"Cap_{Use.Capability}"];
    public bool InUse => Use.InUse;
    public Brush StateBrush => InUse ? Palette.Blocked : Palette.Unknown;
    public string LastUsedText => InUse ? L["Privacy_InUseNow"] : Use.LastUsed is { } t ? Ago(t) : "";
    public string Subtitle => $"{CapabilityLabel} · {LastUsedText}";
    public string PathText => Use.ExecutablePath ?? L.Format("Privacy_StoreApp", Use.AppKey);
    public string StartText => Use.LastStart?.ToString("g", L.Culture) ?? "—";
    public string StopText => InUse ? L["Privacy_InUseNow"] : Use.LastStop?.ToString("g", L.Culture) ?? "—";
    public string DurationText => Use.Duration is { } d ? FormatDuration(d) : "—";
    public string PermissionText => Use.Permission switch
    {
        "Allow" => L["Privacy_PermAllow"],
        "Deny" => L["Privacy_PermDeny"],
        _ => L["Privacy_PermDesktop"],
    };
    public string ScopeText => L[Use.MachineWide ? "Privacy_ScopeMachine" : "Privacy_ScopeUser"];
    public string? ExecutableName => Use.ExecutablePath is { } p ? Path.GetFileName(p) : null;
    public bool HasExecutable => ExecutableName is not null;

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        bool Has(string? s) => s?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
        return Has(Title) || Has(Use.AppKey) || Has(CapabilityLabel);
    }

    internal static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? L["Privacy_JustNow"]
            : d.TotalHours < 1 ? L.Format("Privacy_MinutesAgo", (int)d.TotalMinutes)
            : d.TotalDays < 1 ? L.Format("Privacy_HoursAgo", (int)d.TotalHours)
            : d.TotalDays < 60 ? L.Format("Privacy_DaysAgo", (int)d.TotalDays)
            : t.ToString("d", L.Culture);
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalSeconds < 60 ? $"{(int)d.TotalSeconds} s"
        : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min"
        : $"{(int)d.TotalHours} h {d.Minutes:00}";

    public void Refresh() => Raise(null);
}

/// <summary>Résumé d'une fonction (caméra, micro, localisation) pour la vue d'ensemble.</summary>
public sealed record PrivacyCapabilitySummary(string Capability, string Icon, string Title, string Status, string LastUse, bool InUse)
{
    public Brush StatusBrush => InUse ? Palette.Blocked : Palette.Unknown;
}

/// <summary>
/// v1.1 — Confidentialité : qui a utilisé la caméra, le micro ou la localisation, et quand. Lecture seule du journal que
/// Windows tient lui-même ; les autorisations se règlent dans Paramètres (bouton direct).
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan PrivacyInterval = TimeSpan.FromSeconds(5);
    private DispatcherTimer? _privacyTimer;
    private List<PrivacyUseViewModel> _privacyAll = [];

    public bool IsPrivacyPage => CurrentPage == "privacy";
    public ObservableCollection<PrivacyUseViewModel> PrivacyUses { get; } = [];
    public ObservableCollection<PrivacyCapabilitySummary> PrivacySummaries { get; } = [];

    public IReadOnlyList<Option<PrivacyFilter>> PrivacyFilters { get; private set; } = [];
    private Option<PrivacyFilter>? _selectedPrivacyFilter;
    public Option<PrivacyFilter>? SelectedPrivacyFilter
    {
        get => _selectedPrivacyFilter;
        set
        {
            if (value is not null && Set(ref _selectedPrivacyFilter, value)) RebuildPrivacyView();
        }
    }

    private PrivacyUseViewModel? _selectedPrivacyUse;
    public PrivacyUseViewModel? SelectedPrivacyUse
    {
        get => _selectedPrivacyUse;
        set
        {
            if (Set(ref _selectedPrivacyUse, value)) Raise(nameof(HasSelectedPrivacyUse));
        }
    }
    public bool HasSelectedPrivacyUse => SelectedPrivacyUse is not null;

    private string _privacyInUseText = "";
    public string PrivacyInUseText { get => _privacyInUseText; private set { Set(ref _privacyInUseText, value); Raise(nameof(HasPrivacyInUse)); } }
    public bool HasPrivacyInUse => PrivacyInUseText.Length > 0;

    public ICommand OpenPrivacySettingsCommand { get; private set; } = null!;
    public ICommand ShowPrivacyOverviewCommand { get; private set; } = null!;
    public ICommand ShowPrivacyProcessCommand { get; private set; } = null!;
    public ICommand ShowPrivacyNetworkCommand { get; private set; } = null!;

    private void InitPrivacy()
    {
        PrivacyFilters = Enum.GetValues<PrivacyFilter>().Select(f => new Option<PrivacyFilter>(f, $"PrivacyFilter_{f}")).ToList();
        _selectedPrivacyFilter = PrivacyFilters[0];
        OpenPrivacySettingsCommand = new RelayCommand(p =>
        {
            // Ouvre la page correspondante des Paramètres Windows (protocole local, aucune connexion).
            var page = (p as string) switch
            {
                "webcam" => "privacy-webcam",
                "microphone" => "privacy-microphone",
                "location" => "privacy-location",
                _ => "privacy",
            };
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"ms-settings:{page}") { UseShellExecute = true })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Paramètres indisponibles : rien à faire.
            }
        });
        ShowPrivacyOverviewCommand = new RelayCommand(() => SelectedPrivacyUse = null);
        ShowPrivacyProcessCommand = new RelayCommand(p =>
        {
            if (p is PrivacyUseViewModel { ExecutableName: { } exe }) ShowProcess(new NetworkSenderItem(exe, null, null, 0, 0, false, 0, exe));
        });
        ShowPrivacyNetworkCommand = new RelayCommand(p =>
        {
            if (p is PrivacyUseViewModel { ExecutableName: { } exe }) ShowNetwork(new NetworkSenderItem(exe, null, null, 0, 0, false, 0, exe));
        });
    }

    private void RefreshPrivacyLabels()
    {
        foreach (var o in PrivacyFilters) o.Refresh();
        foreach (var u in _privacyAll) u.Refresh();
        if (_privacyTimer is not null) _ = LoadPrivacyAsync();
    }

    /// <summary>Première ouverture : lecture puis relevé toutes les 5 s tant que la page est affichée (indicateur « en cours »).</summary>
    private void StartPrivacy()
    {
        _ = LoadPrivacyAsync();
        if (_privacyTimer is not null) return;
        _privacyTimer = new DispatcherTimer { Interval = PrivacyInterval };
        _privacyTimer.Tick += async (_, _) =>
        {
            if (IsPrivacyPage) await LoadPrivacyAsync();
        };
        _privacyTimer.Start();
    }

    private async Task LoadPrivacyAsync()
    {
        var (uses, globals) = await Task.Run(() =>
            (CapabilityUsage.Read(), CapabilityUsage.Tracked.ToDictionary(c => c, CapabilityUsage.GlobalPermission)));
        var selectedKey = SelectedPrivacyUse is { } s ? (s.Use.Capability, s.Use.AppKey) : default;
        _privacyAll = uses.OrderByDescending(u => u.InUse).ThenByDescending(u => u.LastUsed).Select(u => new PrivacyUseViewModel(u)).ToList();
        RebuildPrivacyView();
        if (selectedKey != default)
            SelectedPrivacyUse = _privacyAll.FirstOrDefault(u => (u.Use.Capability, u.Use.AppKey) == selectedKey);

        PrivacySummaries.Clear();
        foreach (var cap in CapabilityUsage.Tracked)
        {
            var (device, user) = globals[cap];
            var status = device == "Deny" ? L["Privacy_GlobalDeviceOff"] : user == "Deny" ? L["Privacy_GlobalUserOff"] : L["Privacy_GlobalOn"];
            var mine = _privacyAll.Where(u => u.Use.Capability == cap).ToList();
            var last = mine.FirstOrDefault();
            var lastUse = last is null ? L["Privacy_NeverUsed"] : L.Format("Privacy_LastUse", last.Title, last.LastUsedText, mine.Count);
            PrivacySummaries.Add(new PrivacyCapabilitySummary(cap, PrivacyUseViewModel.IconOf(cap),
                L[$"Cap_{cap}"], status, lastUse, mine.Any(u => u.InUse)));
        }
        var active = _privacyAll.Where(u => u.InUse).ToList();
        PrivacyInUseText = active.Count == 0 ? ""
            : L.Format("Privacy_InUseBanner", string.Join(", ", active.Select(u => $"{u.Title} ({u.CapabilityLabel.ToLower(L.Culture)})")));
    }

    private void RebuildPrivacyView()
    {
        var filter = SelectedPrivacyFilter?.Value ?? PrivacyFilter.All;
        var query = SearchText.Trim();
        var visible = _privacyAll.Where(u => filter switch
        {
            PrivacyFilter.InUse => u.InUse,
            PrivacyFilter.Webcam => u.Use.Capability == "webcam",
            PrivacyFilter.Microphone => u.Use.Capability == "microphone",
            PrivacyFilter.Location => u.Use.Capability == "location",
            _ => true,
        } && u.Matches(query)).ToList();
        if (visible.SequenceEqual(PrivacyUses)) return;
        PrivacyUses.Clear();
        foreach (var u in visible) PrivacyUses.Add(u);
    }
}
