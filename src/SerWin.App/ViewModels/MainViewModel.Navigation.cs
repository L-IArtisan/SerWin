using System.Windows.Input;
using SerWin.App.Infrastructure;
using SerWin.Core.Network;
using Wpf.Ui.Controls;

namespace SerWin.App.ViewModels;

/// <summary>
/// Filtre « échanges de ce programme » de la page Réseau : un service, ou un processus (ses services hébergés,
/// son exécutable s'il n'est pas un svchost partagé, et son numéro).
/// </summary>
public sealed record NetworkSenderFilter(string Label, int? Pid, IReadOnlyList<string> Names)
{
    public bool Matches(SenderIdentity s) =>
        (s.Service is not null && Names.Contains(s.Service, StringComparer.OrdinalIgnoreCase))
        || (s.ProcessName is not null && Names.Contains(s.ProcessName, StringComparer.OrdinalIgnoreCase))
        || (Pid is { } pid && s.ProcessId == pid && s.ProcessName is not null);
}

/// <summary>
/// Liens croisés Service ↔ Processus ↔ Réseau, disponibles depuis chaque page (comme « Voir la fiche »).
/// </summary>
public sealed partial class MainViewModel
{
    public ICommand ShowProcessCommand { get; private set; } = null!;
    public ICommand ShowNetworkCommand { get; private set; } = null!;
    public ICommand ClearSenderFilterCommand { get; private set; } = null!;

    private NetworkSenderFilter? _senderFilter;
    /// <summary>Programme dont on affiche les échanges (null = tous).</summary>
    public NetworkSenderFilter? SenderFilter
    {
        get => _senderFilter;
        private set
        {
            if (!Set(ref _senderFilter, value)) return;
            Raise(nameof(HasSenderFilter));
            Raise(nameof(SenderFilterText));
            RebuildNetworkView();
        }
    }
    public bool HasSenderFilter => SenderFilter is not null;
    public string SenderFilterText => SenderFilter is { } f ? L.Format("Net_SenderFilter", f.Label) : "";

    private void InitNavigation()
    {
        ShowProcessCommand = new RelayCommand(ShowProcess);
        ShowNetworkCommand = new RelayCommand(ShowNetwork);
        ClearSenderFilterCommand = new RelayCommand(() => SenderFilter = null);
    }

    /// <summary>
    /// Ouvre la page Processus sur le processus concerné : celui qui héberge un service, ou celui qui a fait un échange
    /// réseau. S'il s'est terminé depuis, la recherche affiche les processus du même nom.
    /// </summary>
    private void ShowProcess(object? p)
    {
        var (pid, name) = p switch
        {
            ServiceItemViewModel s => ((int)s.Info.ProcessId, (string?)null),
            NetworkSenderItem n => (n.Pid, n.ProcessName),
            string service => ((int)(Services.FirstOrDefault(s => s.Name.Equals(service, StringComparison.OrdinalIgnoreCase))?.Info.ProcessId ?? 0), null),
            _ => (0, null),
        };

        SearchText = "";
        CurrentPage = "processes";
        Raise(nameof(NavIndex));

        var item = pid != 0 ? _processItems.Values.FirstOrDefault(i => i.Pid == pid) : null;
        item ??= name is not null ? _processItems.Values.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        if (item is null)
        {
            if (name is not null) SearchText = Path.GetFileNameWithoutExtension(name);
            Notify(InfoBarSeverity.Informational, L["Nav_ProcessGone"]);
            return;
        }

        // Arbre : on déplie les parents pour que le processus soit visible, puis on le sélectionne.
        for (var parent = item.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
        foreach (var other in _processItems.Values.Where(i => i != item && i.IsSelected)) other.IsSelected = false;
        item.IsSelected = true;
        SelectedProcess = item;
    }

    /// <summary>Ouvre la page Réseau filtrée sur les échanges d'un service ou d'un processus.</summary>
    private void ShowNetwork(object? p)
    {
        NetworkSenderFilter? filter = p switch
        {
            ServiceItemViewModel s => new(s.DisplayName, null, [s.Name]),
            ProcessItemViewModel proc => new($"{proc.Name} ({proc.Pid})", proc.Pid,
                proc.HostedServices.Select(h => h.Name)
                    // Un svchost partagé : on ne garde que ses services, sinon tous les svchost correspondraient.
                    .Concat(proc.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase) ? [] : [proc.Name])
                    .ToList()),
            NetworkSenderItem n => new(n.Label, null, [n.ServiceName ?? n.ProcessName ?? n.Label]),
            string service => new(service, null, [service]),
            _ => null,
        };
        if (filter is null) return;

        SearchText = "";
        SelectedNetworkFilter = NetworkFilters[0];
        CurrentPage = "network";
        Raise(nameof(NavIndex));
        SelectedNetworkItem = null;
        SenderFilter = filter;
    }
}
