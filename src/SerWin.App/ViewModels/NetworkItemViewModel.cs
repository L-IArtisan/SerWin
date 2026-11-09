using System.Windows.Media;
using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Context;
using SerWin.Core.Network;
using SerWin.Knowledge;

namespace SerWin.App.ViewModels;

/// <summary>Un programme qui a contacté la destination.</summary>
public sealed record NetworkSenderItem(string Label, string? Detail, string? ServiceName, int Queries, int Connections, bool Active)
{
    public bool HasService => ServiceName is not null;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>
/// Une destination réseau (nom d'hôte, ou adresse IP si aucun nom n'est connu) et tous les programmes qui l'ont contactée.
/// La fiche de connaissance éventuelle décrit le service en ligne de façon générale.
/// </summary>
public sealed class NetworkItemViewModel(string destination, DestinationEntry? entry, DisplayContext ctx) : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    private List<NetworkFlow> _flows = [];

    public string Destination { get; } = destination;
    public DestinationEntry? Entry { get; } = entry;
    public bool IsDocumented => Entry is not null;

    /// <summary>Remplace les échanges agrégés (appelé à chaque relevé).</summary>
    public void Update(IEnumerable<NetworkFlow> flows)
    {
        _flows = flows.ToList();
        Raise(null);
    }

    // ── Liste ────────────────────────────────────────────────
    public bool HasHostName => _flows.Any(f => f.HasHostName);
    public string Title => Entry?.DisplayName.Resolve(ctx.Language) ?? Destination;
    public string Subtitle => Entry is null ? SendersShort : $"{Destination} · {SendersShort}";
    public string SendersShort => string.Join(", ", Senders.Select(s => s.Label).Take(3)) + (Senders.Count > 3 ? "…" : "");
    public DateTime LastSeen => _flows.Count == 0 ? DateTime.MinValue : _flows.Max(f => f.LastSeen);
    public DateTime FirstSeen => _flows.Count == 0 ? DateTime.MaxValue : _flows.Min(f => f.FirstSeen);
    public bool IsActive => _flows.Any(f => f.ActiveConnections > 0);
    public int Queries => _flows.Sum(f => f.Queries);
    public int Connections => _flows.Sum(f => f.Connections);
    public AddressScope Scope => _flows.Count == 0 ? AddressScope.Internet : _flows.Max(f => f.Scope);
    public bool IsLocalNetwork => Scope == AddressScope.LocalNetwork;
    public string ActivityText => L.Format("Net_Activity", Queries, Connections);
    public string LastSeenText => LastSeen == DateTime.MinValue ? "" : FormatWhen(LastSeen);

    // ── Fiche ────────────────────────────────────────────────
    public string Owner => Entry?.Owner ?? "";
    public bool HasOwner => !string.IsNullOrEmpty(Owner);
    public string CategoryLabel => Entry is null ? "" : L[$"DestCat_{Entry.Category.Replace('-', '_')}"];
    public bool IsPrivacy => Entry?.Privacy == true;

    public DestinationNeed? Need => Entry?.Need;
    public string NeedLabel => Need is { } n ? L[$"Need_{n}"] : L["Need_Unknown"];
    public Brush NeedBrush => Need switch
    {
        DestinationNeed.Essential => Palette.Safe,
        DestinationNeed.Useful => Palette.Running,
        DestinationNeed.Optional => Palette.Neutral,
        DestinationNeed.Unwanted => Palette.Caution,
        _ => Palette.Neutral,
    };

    public string Summary => Entry?.Summary.Resolve(ctx.Language) ?? (IsLocalNetwork ? L["Net_LocalSummary"] : L["Net_UnknownSummary"]);

    public string LevelText
    {
        get
        {
            if (Entry is null) return "";
            var t = ctx.Level switch
            {
                ComplexityLevel.Expert => Entry.Levels.Expert,
                ComplexityLevel.Avance => Entry.Levels.Avance,
                _ => Entry.Levels.Simple,
            };
            return t.Resolve(ctx.Language);
        }
    }

    public IReadOnlyList<string> IfBlocked => Entry?.IfBlocked.Select(t => t.Resolve(ctx.Language)).ToList() ?? [];
    public bool HasIfBlocked => IfBlocked.Count > 0;
    public string? ControlText => Entry?.Control?.Resolve(ctx.Language);
    public bool HasControl => ControlText is not null;

    /// <summary>Verdict « chez toi » (mêmes règles de contexte que les services).</summary>
    public UsefulnessVerdict Verdict => Entry is null || ctx.Machine is null || Entry.UsefulIf.Count == 0
        ? UsefulnessVerdict.NoRule
        : UsefulnessRules.Evaluate(Entry.UsefulIf, ctx.Machine);
    public bool IsNotUsefulHere => Verdict.Verdict == Usefulness.NotUseful;
    public string? VerdictText => IsNotUsefulHere ? Entry?.VerdictIfNot?.Resolve(ctx.Language) ?? L["Net_NotUsefulHere"] : null;

    /// <summary>Programmes émetteurs, regroupés (un service, ou un exécutable tous processus confondus).</summary>
    public IReadOnlyList<NetworkSenderItem> Senders => _flows
        .GroupBy(f => f.Sender.Key)
        .Select(g =>
        {
            var s = g.First().Sender;
            var label = s.Service ?? s.ProcessName ?? L.Format("Net_EndedProcess", s.ProcessId);
            string? detail = s.Service is not null ? s.ProcessName
                : s.HostedServices.Count > 0 ? L.Format("Net_HostedServices", string.Join(", ", s.HostedServices.Take(6)), s.HostedServices.Count)
                : null;
            return new NetworkSenderItem(label, detail, s.Service, g.Sum(f => f.Queries), g.Sum(f => f.Connections), g.Any(f => f.ActiveConnections > 0));
        })
        .OrderByDescending(s => s.Queries + s.Connections)
        .ToList();

    /// <summary>Programmes habituels selon la fiche, absents des émetteurs observés (aide à repérer un intrus).</summary>
    public bool HasUnexpectedSender => Entry is { Senders.Count: > 0 } e && Senders.Any(s =>
        !e.Senders.Any(x => x.Equals(s.ServiceName, StringComparison.OrdinalIgnoreCase) || s.Label.Equals(x, StringComparison.OrdinalIgnoreCase)
                            || (s.Detail?.Contains(x, StringComparison.OrdinalIgnoreCase) ?? false)));

    // ── Technique ────────────────────────────────────────────
    public bool ShowTechnical => ctx.Level != ComplexityLevel.Simple;
    public string AddressesText => string.Join(", ", _flows.SelectMany(f => f.Addresses).Distinct().Take(8).Select(a => a.ToString()))
                                   + (_flows.SelectMany(f => f.Addresses).Distinct().Count() > 8 ? "…" : "");
    public string PortsText => string.Join(", ", _flows.SelectMany(f => f.Ports).Distinct().Order());
    public bool HasPorts => _flows.Any(f => f.Ports.Count > 0);
    public string AliasesText => string.Join(", ", _flows.SelectMany(f => f.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).Take(6));
    public bool HasAliases => _flows.Any(f => f.Aliases.Count > 0);
    public string FirstSeenText => FirstSeen == DateTime.MaxValue ? "" : FormatWhen(FirstSeen);
    public string PatternsText => Entry is null ? "" : string.Join(", ", Entry.Patterns);
    public bool IsDraft => Entry?.Verification.Status == VerificationStatus.Draft;

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        bool Has(string? s) => s?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
        return Has(Destination) || Has(Title) || Has(Owner) || Has(CategoryLabel)
               || Senders.Any(s => Has(s.Label) || Has(s.Detail))
               || _flows.Any(f => f.Addresses.Any(a => Has(a.ToString())));
    }

    private static string FormatWhen(DateTime t) =>
        t.Date == DateTime.Today ? t.ToString("T", L.Culture) : t.ToString("g", L.Culture);

    public void Refresh() => Raise(null);
}
