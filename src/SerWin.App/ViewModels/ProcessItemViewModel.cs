using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Processes;
using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

/// <summary>Service affiché sous son processus hôte dans l'arbre (svchost « déplié »).</summary>
public sealed class HostedServiceItemViewModel(ServiceInfo service) : ObservableObject
{
    public string Name => service.Name;
    public string Label => string.IsNullOrWhiteSpace(service.DisplayName) ? service.Name : $"{service.DisplayName} ({service.Name})";

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public bool IsExpanded { get; set; }
}

/// <summary>Un processus dans l'arbre ou la liste. Mis à jour en place à chaque échantillonnage.</summary>
public sealed class ProcessItemViewModel : ObservableObject
{
    private static readonly Loc L = Loc.Instance;
    private static readonly Dictionary<string, string?> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<ProcessItemViewModel> _onSelected;
    private readonly DisplayContext _ctx;

    public ProcessItemViewModel(ProcessInfo info, bool pathInferred, KnowledgeEntry? entry, DisplayContext ctx, Action<ProcessItemViewModel> onSelected)
    {
        Info = info;
        PathInferred = pathInferred;
        Entry = entry;
        _ctx = ctx;
        _onSelected = onSelected;
    }

    public ProcessInfo Info { get; private set; }
    public bool PathInferred { get; private set; }
    public KnowledgeEntry? Entry { get; }
    public (int, DateTime?) Key => (Info.Pid, Info.StartTime);

    /// <summary>Enfants : processus puis services hébergés.</summary>
    public ObservableCollection<object> Children { get; } = [];
    public List<ServiceInfo> HostedServices { get; private set; } = [];
    public ProcessItemViewModel? Parent { get; set; }

    private bool _isExpanded;
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value) && value) _onSelected(this);
        }
    }

    public void Update(ProcessInfo info, bool pathInferred, List<ServiceInfo> hosted)
    {
        Info = info;
        PathInferred = pathInferred;
        HostedServices = hosted;
        Raise(null);
    }

    // ── Identité ─────────────────────────────────────────────
    public string Name => Info.Name;
    public int Pid => Info.Pid;
    public string PidText => L.Format("Proc_Pid", Info.Pid);

    public string? Description
    {
        get
        {
            if (Entry is not null) return Entry.DisplayName.Resolve(_ctx.Language);
            if (ProcessPathResolver.KernelPseudoProcesses.Contains(Info.Name)) return L.TryGet($"Proc_Pseudo_{Info.Name.Replace(' ', '_').Trim('[', ']')}");
            if (Info.Path is not { } path) return null;
            lock (Descriptions)
            {
                if (!Descriptions.TryGetValue(path, out var d))
                {
                    try { d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); }
                    catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException) { d = null; }
                    Descriptions[path] = string.IsNullOrWhiteSpace(d) ? null : d;
                }
                return Descriptions[path];
            }
        }
    }

    /// <summary>
    /// Libellé de l'arbre. Un hôte de services (svchost…) affiche directement ce qu'il héberge :
    /// « svchost.exe — Client DNS », « svchost.exe — Audio Windows +2 ».
    /// </summary>
    public string TreeLabel
    {
        get
        {
            if (HostedServices.Count > 0)
            {
                var first = HostedServices[0].DisplayName is { Length: > 0 } dn ? dn : HostedServices[0].Name;
                return HostedServices.Count == 1 ? $"{Info.Name} — {first}" : $"{Info.Name} — {first} +{HostedServices.Count - 1}";
            }
            return Description is { } d && !d.Equals(Info.Name, StringComparison.OrdinalIgnoreCase) ? $"{Info.Name} — {d}" : Info.Name;
        }
    }

    public override string ToString() => TreeLabel;

    // ── Consommation ─────────────────────────────────────────
    public string CpuText => Info.CountersAvailable ? $"{Info.CpuPercent:0.0} %" : "—";
    public string MemoryText => Info.CountersAvailable ? FormatBytes(Info.PrivateBytes) : "—";
    public string IoText => Info.CountersAvailable ? FormatBytes((long)Info.IoBytesPerSec) + "/s" : "—";
    public bool CountersUnavailable => !Info.CountersAvailable && !ProcessPathResolver.KernelPseudoProcesses.Contains(Info.Name);

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} Mo",
        >= 1L << 10 => $"{bytes / 1024.0:0} Ko",
        _ => $"{bytes} o",
    };

    // ── Sécurité ─────────────────────────────────────────────
    private SignatureInfo? _signature;
    public SignatureInfo? Signature
    {
        get => _signature;
        set
        {
            _signature = value;
            Raise(nameof(Signature));
            Raise(nameof(SignatureLabel));
            Raise(nameof(HasWarning));
            Raise(nameof(WarningText));
        }
    }

    public string SignatureLabel => Signature switch
    {
        null when Info.Path is null => L["Sig_NoPath"],
        null => L["Sig_Checking"],
        { Status: SignatureStatus.Signed or SignatureStatus.SignedCatalog } s => L.Format("Sig_SignedBy", s.Signer ?? "?"),
        { } s => L[$"Sig_{s.Status}"],
    };

    public bool IsSuspiciousLocation => SuspiciousLocation.IsSuspicious(Info.Path);
    public bool IsUnsigned => Signature is { Status: SignatureStatus.Unsigned or SignatureStatus.Invalid };
    /// <summary>Badge ⚠️ (spec §5.6) : non signé, signature invalide, ou emplacement suspect.</summary>
    public bool HasWarning => IsUnsigned || IsSuspiciousLocation;
    public string WarningText => string.Join(" · ", new[]
    {
        IsUnsigned ? L[$"Sig_{Signature!.Status}"] : null,
        IsSuspiciousLocation ? L["Proc_SuspiciousLocation"] : null,
    }.Where(s => s is not null));

    public bool IsBlocked => SafetyLists.RedProcesses.Contains(Info.Name) || Info.IsCritical
        || HostedServices.Any(s => SafetyLists.ForService(s.TemplateName) == SafetyLevel.Red);
    /// <summary>
    /// Pastille. Pour un svchost, elle reflète les services hébergés (sinon tous seraient rouges, ce qui noierait l'information) ;
    /// la terminaison reste bloquée dans tous les cas.
    /// </summary>
    public Brush RiskBrush
    {
        get
        {
            if (Info.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
            {
                var levels = HostedServices.Select(s => SafetyLists.ForService(s.TemplateName)).ToList();
                return levels.Contains(SafetyLevel.Red) ? Palette.Blocked : levels.Contains(SafetyLevel.Orange) ? Palette.Caution : Palette.Neutral;
            }
            return IsBlocked ? Palette.Blocked : HasWarning ? Palette.Caution : Palette.Neutral;
        }
    }

    // ── Fiche ────────────────────────────────────────────────
    public bool IsDocumented => Entry is not null;
    public string Summary => Entry?.Summary.Resolve(_ctx.Language) ?? Description ?? L["Proc_NoDescription"];
    public string LevelText => Entry is null ? "" : (_ctx.Level switch
    {
        ComplexityLevel.Expert => Entry.Levels.Expert,
        ComplexityLevel.Avance => Entry.Levels.Avance,
        _ => Entry.Levels.Simple,
    }).Resolve(_ctx.Language);
    public IReadOnlyList<string> IfDisabled => Entry?.IfDisabled.Select(t => t.Resolve(_ctx.Language)).ToList() ?? [];
    public bool HasIfDisabled => IfDisabled.Count > 0;
    public bool HasHostedServices => HostedServices.Count > 0;
    public IReadOnlyList<HostedServiceItemViewModel> HostedServiceItems => HostedServices.Select(s => new HostedServiceItemViewModel(s)).ToList();

    // ── Technique ────────────────────────────────────────────
    public bool ShowTechnical => _ctx.Level != ComplexityLevel.Simple;
    public string PathText => Info.Path is null ? L["Proc_PathUnavailable"] : PathInferred ? L.Format("Proc_PathInferred", Info.Path) : Info.Path;
    public string ParentText => Parent is { } p ? $"{p.Name} ({p.Pid})" : L.Format("Proc_ParentGone", Info.ParentPid);
    public string StartText => Info.StartTime?.ToString("G", L.Culture) ?? "—";
    public string SessionText => Info.SessionId == 0 ? L["Proc_Session0"] : Info.SessionId.ToString();
    public string ThreadsText => Info.Threads.ToString();

    public void Refresh() => Raise(null);
}
