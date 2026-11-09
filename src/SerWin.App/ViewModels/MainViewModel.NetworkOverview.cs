using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using SerWin.App.Infrastructure;
using SerWin.Core.Network;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

public enum TimelineRange
{
    /// <summary>24 dernières heures, une barre par heure.</summary>
    Day,
    /// <summary>7 derniers jours, une barre par tranche de 4 h.</summary>
    Week,
}

/// <summary>
/// Vue d'ensemble de la page Réseau (affichée quand aucune destination n'est sélectionnée) :
/// fil en direct des échanges et frise par utilité, avec le filtre et la recherche de la page.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Catégories de la frise, du haut vers le bas de chaque barre (l'indésirable en haut, bien visible).</summary>
    private enum NeedKey { Unwanted, Optional, Unknown, Useful, Essential }

    private const int FeedSize = 150;
    private const double ChartHeight = 110;

    private string _overviewKey = "";

    public bool ShowNetworkOverview => !HasSelectedNetworkItem;

    public ObservableCollection<NetworkFeedItem> NetworkFeed { get; } = [];
    public ObservableCollection<TimelineBar> TimelineBars { get; } = [];

    private IReadOnlyList<TimelineLegendItem> _timelineLegend = [];
    public IReadOnlyList<TimelineLegendItem> TimelineLegend { get => _timelineLegend; private set => Set(ref _timelineLegend, value); }

    private string _timelineSummary = "";
    public string TimelineSummary { get => _timelineSummary; private set => Set(ref _timelineSummary, value); }

    public bool HasFeed => NetworkFeed.Count > 0;

    public IReadOnlyList<Option<TimelineRange>> TimelineRanges { get; private set; } = [];
    private Option<TimelineRange>? _selectedTimelineRange;
    public Option<TimelineRange>? SelectedTimelineRange
    {
        get => _selectedTimelineRange;
        set
        {
            if (value is not null && Set(ref _selectedTimelineRange, value)) RebuildNetworkOverview(force: true);
        }
    }

    public ICommand ShowNetworkOverviewCommand { get; private set; } = null!;

    /// <summary>Clic sur une ligne du fil : ouvre la fiche de la destination.</summary>
    public NetworkFeedItem? SelectedFeedItem
    {
        get => null;
        set
        {
            if (value is not null && _networkItems.TryGetValue(value.Destination, out var item)) SelectedNetworkItem = item;
            Raise();
        }
    }

    private void InitNetworkOverview()
    {
        TimelineRanges = Enum.GetValues<TimelineRange>().Select(r => new Option<TimelineRange>(r, $"TimelineRange_{r}")).ToList();
        _selectedTimelineRange = TimelineRanges[0];
        ShowNetworkOverviewCommand = new RelayCommand(() => SelectedNetworkItem = null);
    }

    /// <summary>Recalcule fil et frise si quelque chose a changé (nouvel échange, filtre, recherche, période, heure).</summary>
    private void RebuildNetworkOverview(bool force)
    {
        if (_observer is null) return;
        var range = SelectedTimelineRange?.Value ?? TimelineRange.Day;
        var now = DateTime.Now;
        var key = $"{_observer.LastSequence}|{SelectedNetworkFilter?.Value}|{SearchText}|{range}|{now:yyyyMMddHH}|{_networkItems.Count}";
        if (!force && key == _overviewKey) return;
        _overviewKey = key;

        // Chaque événement est rattaché à la destination affichée dans la liste (fiche, filtre, recherche).
        var events = _observer.Events
            .Select(e => (Event: e, Item: _networkItems.GetValueOrDefault(e.Destination)))
            .Where(x => x.Item is not null && PassesNetworkFilter(x.Item))
            .ToList();

        // ── Fil en direct ──
        // Les échanges identiques et rapprochés (même programme, même destination, même nature, < 1 min) sont regroupés.
        var groups = new List<(NetworkEvent Event, NetworkItemViewModel Item, int Count)>();
        foreach (var x in events.OrderByDescending(x => x.Event.Time).ThenByDescending(x => x.Event.Sequence))
        {
            if (groups.Count > 0 && groups[^1] is var last && last.Event.Sender.Key == x.Event.Sender.Key
                && last.Event.Destination == x.Event.Destination && last.Event.Kind == x.Event.Kind
                && last.Event.Time - x.Event.Time < TimeSpan.FromMinutes(1))
            {
                groups[^1] = (last.Event, last.Item, last.Count + 1);
                continue;
            }
            if (groups.Count == FeedSize) break;
            groups.Add((x.Event, x.Item!, 1));
        }
        var feed = groups
            .Select(x => new NetworkFeedItem(
                x.Event.Sequence,
                x.Event.Time.Date == now.Date ? x.Event.Time.ToString("T", L.Culture) : x.Event.Time.ToString("g", L.Culture),
                x.Event.Sender.Service ?? x.Event.Sender.ProcessName ?? L.Format("Net_EndedProcess", x.Event.Sender.ProcessId),
                x.Item!.Title,
                x.Item.Destination,
                x.Item.NeedBrush,
                x.Item.NeedLabel,
                x.Event.Kind,
                x.Count))
            .ToList();
        if (force || !feed.Select(f => (f.Sequence, f.Count)).SequenceEqual(NetworkFeed.Select(f => (f.Sequence, f.Count))))
        {
            NetworkFeed.Clear();
            foreach (var f in feed) NetworkFeed.Add(f);
            Raise(nameof(HasFeed));
        }

        // ── Frise ──
        var (bucket, count) = range == TimelineRange.Day ? (TimeSpan.FromHours(1), 24) : (TimeSpan.FromHours(4), 42);
        var needOf = events.ToDictionary(x => x.Event, x => KeyOf(x.Item!));
        var buckets = NetworkTimeline.Build(events.Select(x => x.Event), e => needOf[e], now, bucket, count);
        var max = Math.Max(1, buckets.Max(b => b.Total));
        var keys = Enum.GetValues<NeedKey>();

        TimelineBars.Clear();
        foreach (var b in buckets)
        {
            var segments = keys.Where(k => b.Counts.GetValueOrDefault(k) > 0)
                .Select(k => new TimelineSegment(Math.Max(2, b.Counts[k] * ChartHeight / max), BrushOf(k)))
                .ToList();
            TimelineBars.Add(new TimelineBar(segments, BarToolTip(b, range, keys), BarLabel(b, range), now >= b.Start && now < b.End));
        }

        var totals = keys.ToDictionary(k => k, k => buckets.Sum(b => b.Counts.GetValueOrDefault(k)));
        TimelineLegend = keys.Where(k => totals[k] > 0).Select(k => new TimelineLegendItem(BrushOf(k), LabelOf(k), totals[k])).ToList();
        TimelineSummary = L.Format(range == TimelineRange.Day ? "Timeline_SummaryDay" : "Timeline_SummaryWeek",
            totals.Values.Sum(), totals[NeedKey.Unwanted]);
    }

    private static NeedKey KeyOf(NetworkItemViewModel item) => item.Need switch
    {
        DestinationNeed.Essential => NeedKey.Essential,
        DestinationNeed.Useful => NeedKey.Useful,
        DestinationNeed.Optional => NeedKey.Optional,
        DestinationNeed.Unwanted => NeedKey.Unwanted,
        _ => NeedKey.Unknown,
    };

    private static DestinationNeed? NeedOf(NeedKey k) => k switch
    {
        NeedKey.Essential => DestinationNeed.Essential,
        NeedKey.Useful => DestinationNeed.Useful,
        NeedKey.Optional => DestinationNeed.Optional,
        NeedKey.Unwanted => DestinationNeed.Unwanted,
        _ => null,
    };

    private static System.Windows.Media.Brush BrushOf(NeedKey k) => NetworkItemViewModel.BrushFor(NeedOf(k));
    private static string LabelOf(NeedKey k) => NeedOf(k) is { } n ? L[$"Need_{n}"] : L["Need_Unknown"];

    private static string BarToolTip(TimelineBucket<NeedKey> b, TimelineRange range, NeedKey[] keys)
    {
        var fmt = range == TimelineRange.Day ? "t" : "g";
        var sb = new StringBuilder(L.Format("Timeline_BarTip", b.Start.ToString(fmt, L.Culture), b.End.ToString("t", L.Culture), b.Total));
        foreach (var k in keys.Where(k => b.Counts.GetValueOrDefault(k) > 0))
            sb.Append('\n').Append(LabelOf(k)).Append(L.Colon).Append(b.Counts[k]);
        return sb.ToString();
    }

    private static string BarLabel(TimelineBucket<NeedKey> b, TimelineRange range) => range switch
    {
        TimelineRange.Day when b.Start.Hour % 3 == 0 => L.Format("Timeline_HourLabel", b.Start.Hour),
        TimelineRange.Week when b.Start.Hour == 0 => b.Start.ToString("ddd d", L.Culture),
        _ => "",
    };
}
