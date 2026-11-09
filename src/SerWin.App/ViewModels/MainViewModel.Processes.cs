using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using SerWin.App.Infrastructure;
using SerWin.Core.Actions;
using SerWin.Core.Processes;
using SerWin.Core.Services;
using Wpf.Ui.Controls;

namespace SerWin.App.ViewModels;

public enum ProcessViewMode
{
    Tree,
    ByCpu,
    ByMemory,
    Warnings,
}

/// <summary>v0.4 — Processus : arbre, svchost dépliés, signatures, consommation, terminer.</summary>
public sealed partial class MainViewModel
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly ProcessSampler _sampler = new();
    private readonly Dictionary<(int, DateTime?), ProcessItemViewModel> _processItems = [];
    private readonly HashSet<string> _signaturesPending = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _sampleTimer;
    private bool _sampling;
    private List<ProcessItemViewModel> _processRootsAll = [];

    /// <summary>Racines de l'arbre (mode Arbre sans recherche).</summary>
    public ObservableCollection<ProcessItemViewModel> ProcessRoots { get; } = [];
    /// <summary>Liste à plat (tri, avertissements ou recherche).</summary>
    public ObservableCollection<ProcessItemViewModel> FlatProcesses { get; } = [];

    public IReadOnlyList<Option<ProcessViewMode>> ProcessViewModes { get; private set; } = [];
    private Option<ProcessViewMode>? _selectedProcessViewMode;
    public Option<ProcessViewMode>? SelectedProcessViewMode
    {
        get => _selectedProcessViewMode;
        set
        {
            if (value is not null && Set(ref _selectedProcessViewMode, value)) RebuildProcessView();
        }
    }

    public bool IsTreeMode => SelectedProcessViewMode?.Value == ProcessViewMode.Tree && string.IsNullOrWhiteSpace(SearchText);
    public bool IsFlatMode => !IsTreeMode;
    public bool IsProcessesPage => CurrentPage == "processes";
    public string ProcessCountText => L.Format("Proc_Count", _processItems.Count, _processItems.Values.Count(p => p.HasWarning));

    private ProcessItemViewModel? _selectedProcess;
    public ProcessItemViewModel? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (Set(ref _selectedProcess, value))
            {
                Raise(nameof(HasSelectedProcess));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool HasSelectedProcess => SelectedProcess is not null;

    public ICommand TerminateProcessCommand { get; private set; } = null!;
    public ICommand ShowServiceCommand { get; private set; } = null!;

    private void InitProcesses()
    {
        ProcessViewModes = Enum.GetValues<ProcessViewMode>().Select(m => new Option<ProcessViewMode>(m, $"ProcView_{m}")).ToList();
        _selectedProcessViewMode = ProcessViewModes[0];
        TerminateProcessCommand = new RelayCommand(async () => await TerminateSelectedAsync(),
            _ => IsElevated && !IsBusy && SelectedProcess is { IsBlocked: false });
        ShowServiceCommand = new RelayCommand(p =>
        {
            if (p is not string name) return;
            SearchText = "";
            SelectedFilter = Filters[0];
            SelectedService = Services.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            CurrentPage = "services";
            Raise(nameof(NavIndex));
            SelectedTabIndex = 0;
        });
    }

    private void StartSampling()
    {
        if (_sampleTimer is not null) return;
        _sampleTimer = new DispatcherTimer { Interval = SampleInterval };
        _sampleTimer.Tick += async (_, _) => await SampleProcessesAsync();
        _sampleTimer.Start();
        _ = SampleProcessesAsync();
    }

    /// <summary>Échantillonne puis met à jour arbre, liste et consommation des services, sans perdre dépliage ni sélection.</summary>
    private async Task SampleProcessesAsync()
    {
        if (_sampling) return;
        _sampling = true;
        try
        {
            var services = Services.Select(s => s.Info).ToList();
            var (resolved, roots) = await Task.Run(() =>
            {
                var processes = _sampler.Sample();
                var r = ProcessPathResolver.Resolve(processes, services);
                return (r, ProcessTree.Build(r.Select(x => x.Process).ToList(), services));
            });

            var inferred = resolved.ToDictionary(x => (x.Process.Pid, x.Process.StartTime), x => x.PathInferred);
            var alive = new HashSet<(int, DateTime?)>();
            ProcessItemViewModel Visit(ProcessNode node, ProcessItemViewModel? parent, int depth)
            {
                var key = (node.Process.Pid, node.Process.StartTime);
                alive.Add(key);
                if (!_processItems.TryGetValue(key, out var item))
                {
                    item = new ProcessItemViewModel(node.Process, inferred.GetValueOrDefault(key), _kb.FindProcess(node.Process.Name), _ctx,
                        p => SelectedProcess = p)
                    {
                        // Premier affichage : racines et leurs enfants dépliés (wininit → services.exe → svchost…).
                        IsExpanded = depth < 2 && node.HostedServices.Count == 0,
                    };
                    _processItems[key] = item;
                }
                item.Update(node.Process, inferred.GetValueOrDefault(key), node.HostedServices);
                item.Parent = parent;
                var children = node.Children.Select(c => (object)Visit(c, item, depth + 1))
                    .Concat(node.HostedServices.Select(s => (object)new HostedServiceItemViewModel(s))).ToList();
                SyncChildren(item.Children, children);
                QueueSignature(item);
                return item;
            }

            _processRootsAll = roots.Select(r => Visit(r, null, 0)).ToList();
            foreach (var dead in _processItems.Keys.Where(k => !alive.Contains(k)).ToList()) _processItems.Remove(dead);
            if (SelectedProcess is { } sel && !alive.Contains(sel.Key)) SelectedProcess = null;

            RebuildProcessView();
            UpdateServiceConsumption();
            Raise(nameof(ProcessCountText));
        }
        finally
        {
            _sampling = false;
        }
    }

    /// <summary>Remplace les enfants seulement si la structure a changé (évite de régénérer l'arbre toutes les 2 s).</summary>
    private static void SyncChildren(ObservableCollection<object> target, List<object> wanted)
    {
        static object KeyOf(object o) => o is ProcessItemViewModel p ? p.Key : ((HostedServiceItemViewModel)o).Name;
        if (target.Count == wanted.Count && target.Select(KeyOf).SequenceEqual(wanted.Select(KeyOf))) return;
        target.Clear();
        foreach (var w in wanted) target.Add(w);
    }

    private void RebuildProcessView()
    {
        Raise(nameof(IsTreeMode));
        Raise(nameof(IsFlatMode));
        if (IsTreeMode)
        {
            if (!ProcessRoots.Select(r => r.Key).SequenceEqual(_processRootsAll.Select(r => r.Key)))
            {
                ProcessRoots.Clear();
                foreach (var r in _processRootsAll) ProcessRoots.Add(r);
            }
            return;
        }

        var query = SearchText.Trim();
        IEnumerable<ProcessItemViewModel> items = _processItems.Values;
        if (query.Length > 0)
            items = items.Where(p => p.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || p.Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true
                || p.Pid.ToString() == query
                || p.HostedServices.Any(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                                             || s.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        items = SelectedProcessViewMode?.Value switch
        {
            ProcessViewMode.ByCpu => items.OrderByDescending(p => p.Info.CpuPercent).ThenByDescending(p => p.Info.PrivateBytes),
            ProcessViewMode.ByMemory => items.OrderByDescending(p => p.Info.PrivateBytes),
            ProcessViewMode.Warnings => items.Where(p => p.HasWarning).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        var list = items.ToList();
        if (FlatProcesses.SequenceEqual(list)) return;
        var selected = SelectedProcess;
        FlatProcesses.Clear();
        foreach (var p in list) FlatProcesses.Add(p);
        SelectedProcess = selected;
    }

    private void QueueSignature(ProcessItemViewModel item)
    {
        if (item.Signature is not null || item.Info.Path is not { } path) return;
        // Résultat déjà en cache : appliqué tout de suite ; sinon calcul en arrière-plan (une fois par fichier).
        lock (_signaturesPending)
        {
            if (!_signaturesPending.Add(path)) return;
        }
        _ = Task.Run(() => SignatureVerifier.Verify(path)).ContinueWith(t =>
        {
            lock (_signaturesPending) _signaturesPending.Remove(path);
            if (t.IsCompletedSuccessfully)
                foreach (var p in _processItems.Values.Where(p => string.Equals(p.Info.Path, path, StringComparison.OrdinalIgnoreCase)))
                    p.Signature = t.Result;
            Raise(nameof(ProcessCountText));
            if (SelectedProcessViewMode?.Value == ProcessViewMode.Warnings) RebuildProcessView();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>Consommation par service (spec §5.6) : celle de son processus, signalée comme partagée si besoin.</summary>
    private void UpdateServiceConsumption()
    {
        var byPid = _processItems.Values.ToDictionary(p => p.Pid);
        foreach (var s in Services)
        {
            if (s.Info.ProcessId == 0 || !byPid.TryGetValue((int)s.Info.ProcessId, out var p))
            {
                s.Consumption = null;
                continue;
            }
            var text = p.Info.CountersAvailable
                ? L.Format("Proc_Consumption", p.CpuText, p.MemoryText, p.IoText)
                : L["Proc_ConsumptionUnavailable"];
            var others = p.HostedServices.Count - 1;
            s.Consumption = others > 0 ? text + " " + L.Format("Proc_SharedWith", others, p.Name, p.Pid) : text + $" — {p.Name} ({p.Pid})";
        }
    }

    private async Task TerminateSelectedAsync()
    {
        if (SelectedProcess is not { } item) return;
        var plan = ProcessActions.Plan(item.Info, Services.Select(s => s.Info).ToList());
        if (plan.IsBlocked)
        {
            Notify(InfoBarSeverity.Error, L["Act_BlockedTitle"], L[$"TermBlocked_{plan.Blocked}"]);
            return;
        }

        var warnings = new List<string>();
        if (plan.HostedServices.Count > 0)
            warnings.Add(L.Format("Term_HostedWarning", string.Join(", ", plan.HostedServices.Select(s => s.DisplayName))));
        if (item.IfDisabled.Count > 0) warnings.Add(string.Join(" · ", item.IfDisabled));
        warnings.Add(L["Term_NotUndoable"]);

        var response = _dialogs.Confirm(new ConfirmRequest
        {
            Title = L.Format("Term_Title", item.Name, item.Pid),
            Changes = [item.TreeLabel, item.PathText],
            Warnings = warnings,
            ConfirmLabel = L["Term_Confirm"],
            Danger = true,
        });
        if (!response.Confirmed) return;

        IsBusy = true;
        try
        {
            var actions = new ProcessActions(_engine.History);
            await Task.Run(() => actions.Execute(plan));
            Notify(InfoBarSeverity.Success, L.Format("Term_Done", item.Name, item.Pid));
            await RefreshHistoryAsync();
            await SampleProcessesAsync();
        }
        catch (Exception ex) when (ex is ServiceActionException or InvalidOperationException or ArgumentException)
        {
            Notify(InfoBarSeverity.Error, L.Format("Term_Failed", item.Name), ex switch { ServiceActionException sae => L[$"ActError_{sae.Code}"], ProcessChangedException => L["Term_PidReused"], _ => ex.Message });
        }
        finally
        {
            IsBusy = false;
        }
    }
}
