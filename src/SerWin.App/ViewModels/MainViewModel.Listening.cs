using System.Collections.ObjectModel;
using System.Windows.Media;
using SerWin.Core.Network;

namespace SerWin.App.ViewModels;

/// <summary>Un port ouvert en écoute, avec le programme responsable et ce que SerWin en sait.</summary>
public sealed record ListeningItem(string PortText, string Program, string Description, string? ServiceName, bool Sensitive, bool Reachable)
{
    public bool HasService => ServiceName is not null;
    public Brush PortBrush => Sensitive && Reachable ? Palette.Blocked : Reachable ? Palette.Caution : Palette.Neutral;
}

/// <summary>v1.1 — « Qui écoute sur ton PC » : ports ouverts en entrée, dans la vue d'ensemble de la page Réseau.</summary>
public sealed partial class MainViewModel
{
    private string _listeningKey = "";

    public ObservableCollection<ListeningItem> ListeningItems { get; } = [];

    private string _listeningHeader = "";
    public string ListeningHeader { get => _listeningHeader; private set => Set(ref _listeningHeader, value); }

    /// <summary>
    /// Relève les ports en écoute. Les ports UDP éphémères (≥ 49152) sont des échanges sortants, pas des services qui
    /// attendent des connexions : ils sont écartés.
    /// </summary>
    private void UpdateListening(List<ListeningPort> ports)
    {
        var shown = ports.Where(p => p.Protocol == "TCP" || !ListeningPorts.IsDynamicRpc(p.Port)).ToList();
        var key = string.Join(";", shown.Select(p => p.Key));
        if (key == _listeningKey && ListeningItems.Count > 0) return;
        _listeningKey = key;

        ListeningItems.Clear();
        foreach (var p in shown.OrderByDescending(p => p.ReachableFromNetwork && KnownPorts.IsSensitive(p.Protocol, p.Port))
                     .ThenBy(p => ListeningPorts.IsDynamicRpc(p.Port)).ThenBy(p => p.Port))
        {
            var sender = _senderResolver.Resolve(p.ProcessId, p.OwnerModule, null);
            var service = sender.Service ?? (p.OwnerModule is { } m && Services.Any(s => s.Name.Equals(m, StringComparison.OrdinalIgnoreCase)) ? m : null);
            var program = service is not null ? DisplayNameOf(service)
                : sender.ProcessName ?? p.OwnerModule ?? L.Format("Net_EndedProcess", p.ProcessId);
            var label = KnownPorts.LabelKey(p.Protocol, p.Port) is { } k ? L[k] : L["Port_Unknown"];
            var exposure = L[p.ReachableFromNetwork ? "Listen_Network" : "Listen_Local"];
            ListeningItems.Add(new ListeningItem($"{p.Protocol} {p.Port}", program, $"{label} · {exposure}", service,
                KnownPorts.IsSensitive(p.Protocol, p.Port), p.ReachableFromNetwork));
        }
        ListeningHeader = L.Format("Listen_Header", shown.Count(p => p.ReachableFromNetwork), shown.Count);
    }
}
