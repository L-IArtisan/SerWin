using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SerWin.App.Infrastructure;
using SerWin.Core.Network;
using SerWin.Knowledge;
using Wpf.Ui.Controls;

namespace SerWin.App.ViewModels;

public enum NetworkFilter
{
    All,
    /// <summary>Télémétrie, publicité et destinations touchant à la vie privée.</summary>
    Privacy,
    /// <summary>Destinations sans fiche.</summary>
    Undocumented,
    /// <summary>Connexions ouvertes en ce moment.</summary>
    Active,
    /// <summary>Appareils du réseau local (box, NAS, imprimante…).</summary>
    LocalNetwork,
}

/// <summary>
/// v1.1 — Réseau : qui contacte quoi, et à quoi ça sert. Lecture seule : SerWin ne se connecte à rien, il lit la table
/// des connexions et le journal DNS de Windows. Aucun blocage dans cette version.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan NetworkInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DnsHistory = TimeSpan.FromDays(7);
    private const int DnsEveryTicks = 1;

    private readonly SenderResolver _senderResolver = new();
    private NetworkObserver? _observer;
    private DispatcherTimer? _networkTimer;
    private readonly Dictionary<string, NetworkItemViewModel> _networkItems = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _dnsReadUntil;
    private int _networkTick;
    private bool _networkSampling;
    private bool _networkErrorShown;

    public bool IsNetworkPage => CurrentPage == "network";

    /// <summary>Destinations visibles (filtre + recherche), les plus récentes en premier.</summary>
    public ObservableCollection<NetworkItemViewModel> NetworkItems { get; } = [];

    public IReadOnlyList<Option<NetworkFilter>> NetworkFilters { get; private set; } = [];
    private Option<NetworkFilter>? _selectedNetworkFilter;
    public Option<NetworkFilter>? SelectedNetworkFilter
    {
        get => _selectedNetworkFilter;
        set
        {
            if (value is not null && Set(ref _selectedNetworkFilter, value)) RebuildNetworkView();
        }
    }

    private NetworkItemViewModel? _selectedNetworkItem;
    public NetworkItemViewModel? SelectedNetworkItem
    {
        get => _selectedNetworkItem;
        set
        {
            if (Set(ref _selectedNetworkItem, value))
            {
                Raise(nameof(HasSelectedNetworkItem));
                Raise(nameof(ShowNetworkOverview));
            }
        }
    }
    public bool HasSelectedNetworkItem => SelectedNetworkItem is not null;

    // ── Journal DNS ──────────────────────────────────────────
    private EventLogSetting? _dnsLog;
    public bool DnsLogEnabled => _dnsLog?.Enabled == true;
    public bool DnsLogDisabled => !DnsLogEnabled;
    public string DnsLogText => _dnsLog is null ? L["Net_DnsLogUnreadable"]
        : DnsLogEnabled ? L.Format("Net_DnsLogOn", _observer?.DnsQueryCount ?? 0)
        : L["Net_DnsLogOff"];
    public bool ShowDnsLogDisable => DnsLogEnabled && _ctx.Level != ComplexityLevel.Simple;

    public ICommand EnableDnsLogCommand { get; private set; } = null!;
    public ICommand DisableDnsLogCommand { get; private set; } = null!;
    public ICommand ShowSenderServiceCommand { get; private set; } = null!;

    public string NetworkCountText => L.Format("Net_Count", NetworkItems.Count, _networkItems.Count,
        _networkItems.Values.Count(i => i.IsActive));

    private void InitNetwork()
    {
        NetworkFilters = Enum.GetValues<NetworkFilter>().Select(f => new Option<NetworkFilter>(f, $"NetFilter_{f}")).ToList();
        _selectedNetworkFilter = NetworkFilters[0];
        EnableDnsLogCommand = new RelayCommand(async () => await ConfigureDnsLogAsync(true), _ => IsElevated && !IsBusy && DnsLogDisabled);
        DisableDnsLogCommand = new RelayCommand(async () => await ConfigureDnsLogAsync(false), _ => IsElevated && !IsBusy && DnsLogEnabled);
        ShowSenderServiceCommand = ShowServiceCommand;
        InitNetworkOverview();
    }

    private void RefreshNetworkLabels()
    {
        foreach (var o in NetworkFilters) o.Refresh();
        foreach (var i in _networkItems.Values) i.Refresh();
        Raise(nameof(DnsLogText));
        Raise(nameof(ShowDnsLogDisable));
        Raise(nameof(NetworkCountText));
        foreach (var o in TimelineRanges) o.Refresh();
        RebuildNetworkOverview(force: true);
    }

    /// <summary>Démarre l'observation à la première ouverture de la page ; ensuite, relevé toutes les 3 s tant que la page est affichée.</summary>
    private void StartNetwork()
    {
        if (_networkTimer is not null) return;
        _observer = new NetworkObserver(_senderResolver);
        _dnsReadUntil = DateTime.Now - DnsHistory;
        _networkTimer = new DispatcherTimer { Interval = NetworkInterval };
        _networkTimer.Tick += async (_, _) =>
        {
            if (IsNetworkPage) await SampleNetworkAsync();
        };
        _networkTimer.Start();
        _ = SampleNetworkAsync(forceDns: true);
    }

    private async Task SampleNetworkAsync(bool forceDns = false)
    {
        // Sans la liste des services, les svchost ne seraient pas identifiés : on attend le premier chargement.
        if (_networkSampling || _observer is null || Services.Count == 0) return;
        _networkSampling = true;
        try
        {
            var observer = _observer;
            var services = Services.Select(s => s.Info).ToList();
            var readDns = forceDns || ++_networkTick % DnsEveryTicks == 0;
            var since = _dnsReadUntil;
            var now = DateTime.Now;
            var (dnsLog, listening) = await Task.Run(() =>
            {
                _senderResolver.Refresh(services);
                EventLogSetting? setting = null;
                if (readDns)
                {
                    setting = DnsLog.ReadSetting();
                    // Lecture du plus ancien au plus récent pour que les noms les plus récents l'emportent.
                    var queries = DnsLog.Read(since);
                    queries.Reverse();
                    observer.AddDns(queries);
                    observer.AddCache(DnsCache.Read(), now);
                }
                observer.AddConnections(TcpConnections.ReadEstablished(), now);
                return (setting, ListeningPorts.Read());
            });
            if (readDns)
            {
                _dnsReadUntil = now.AddSeconds(-5);
                _dnsLog = dnsLog;
                Raise(nameof(DnsLogEnabled));
                Raise(nameof(DnsLogDisabled));
                Raise(nameof(DnsLogText));
                Raise(nameof(ShowDnsLogDisable));
                CommandManager.InvalidateRequerySuggested();
            }
            UpdateNetworkItems(observer);
            UpdateListening(listening);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Un relevé raté ne doit jamais bloquer l'appli ni ouvrir une fenêtre d'erreur toutes les 3 s :
            // on le signale une seule fois et le relevé suivant réessaie.
            if (!_networkErrorShown)
            {
                _networkErrorShown = true;
                Notify(InfoBarSeverity.Warning, L["Net_SampleFailed"], ex.Message);
            }
        }
        finally
        {
            _networkSampling = false;
        }
    }

    private void UpdateNetworkItems(NetworkObserver observer)
    {
        foreach (var group in observer.Flows.Where(f => f.Scope != AddressScope.Loopback).GroupBy(f => f.Destination, StringComparer.OrdinalIgnoreCase))
        {
            var aliases = group.SelectMany(f => f.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var entry = group.Any(f => f.HasHostName) ? _kb.FindDestination(group.Key, aliases) : null;
            // Nouvelle destination, ou fiche trouvée entre-temps grâce à un alias (CNAME) relevé plus tard.
            if (!_networkItems.TryGetValue(group.Key, out var item) || (item.Entry is null && entry is not null))
            {
                if (item is not null && SelectedNetworkItem == item) SelectedNetworkItem = null;
                item = new NetworkItemViewModel(group.Key, entry, _ctx);
                _networkItems[group.Key] = item;
            }
            item.Update(group);
        }
        // Une destination vue par adresse IP puis nommée grâce au journal DNS change de clé : on retire l'ancienne.
        var alive = observer.Flows.Select(f => f.Destination).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _networkItems.Keys.Where(k => !alive.Contains(k)).ToList()) _networkItems.Remove(gone);
        RebuildNetworkView();
    }

    private void RebuildNetworkView()
    {
        var visible = _networkItems.Values
            .Where(PassesNetworkFilter)
            .OrderByDescending(i => i.IsActive)
            .ThenByDescending(i => i.LastSeen)
            .ToList();

        var selected = SelectedNetworkItem;
        if (!visible.SequenceEqual(NetworkItems))
        {
            NetworkItems.Clear();
            foreach (var i in visible) NetworkItems.Add(i);
        }
        if (selected is not null && NetworkItems.Contains(selected)) SelectedNetworkItem = selected;
        else if (selected is not null && !_networkItems.ContainsValue(selected)) SelectedNetworkItem = null;
        Raise(nameof(NetworkCountText));
        RebuildNetworkOverview(force: false);
    }

    private async Task ConfigureDnsLogAsync(bool enable)
    {
        var plan = await Task.Run(() => _engine.PlanDnsLog(enable));
        var warnings = enable ? new List<string> { L["Warn_DnsLog"] } : [];
        await RunPlanAsync(plan, L[enable ? "History_DnsLogOn" : "History_DnsLogOff"], L[enable ? "Net_EnableDnsLog" : "Net_DisableDnsLog"],
            warnings, danger: false, intro: enable ? L["Net_DnsLogIntro"] : null);
        await SampleNetworkAsync(forceDns: true);
    }
}

public sealed partial class MainViewModel
{
    /// <summary>Filtre et recherche de la page Réseau, partagés par la liste, le fil en direct et la frise.</summary>
    private bool PassesNetworkFilter(NetworkItemViewModel i) =>
        (SelectedNetworkFilter?.Value ?? NetworkFilter.All) switch
        {
            NetworkFilter.Privacy => i.IsPrivacy || i.Need == DestinationNeed.Unwanted,
            NetworkFilter.Undocumented => !i.IsDocumented && !i.IsLocalNetwork,
            NetworkFilter.Active => i.IsActive,
            NetworkFilter.LocalNetwork => i.IsLocalNetwork,
            _ => true,
        } && i.Matches(SearchText.Trim()) && (SenderFilter is null || i.HasSender(SenderFilter));
}
