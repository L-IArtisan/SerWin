using System.Windows.Media;
using SerWin.Core.Network;

namespace SerWin.App.ViewModels;

/// <summary>Une ligne du fil en direct : « 10:42:13 · Code.exe → Télémétrie Microsoft ».</summary>
public sealed record NetworkFeedItem(
    long Sequence,
    string TimeText,
    string SenderLabel,
    string DestinationTitle,
    string Destination,
    Brush Brush,
    string NeedLabel,
    NetworkEventKind Kind,
    int Count = 1)
{
    /// <summary>Échanges identiques regroupés (même programme, même destination, à moins d'une minute d'intervalle).</summary>
    public string CountText => Count > 1 ? $"×{Count}" : "";
    public bool HasCount => Count > 1;

    /// <summary>Requête DNS (« a demandé ») ou connexion (« s'est connecté »).</summary>
    public string KindGlyph => Kind == NetworkEventKind.Dns ? "?" : "→";
}

/// <summary>Un morceau de barre de la frise (une utilité), hauteur déjà calculée en pixels.</summary>
public sealed record TimelineSegment(double Height, Brush Brush);

/// <summary>Une barre de la frise : segments empilés de haut en bas, info-bulle et étiquette éventuelle.</summary>
public sealed record TimelineBar(IReadOnlyList<TimelineSegment> Segments, string ToolTip, string Label, bool IsCurrent)
{
    public bool HasLabel => Label.Length > 0;
}

/// <summary>Une entrée de la légende de la frise.</summary>
public sealed record TimelineLegendItem(Brush Brush, string Label, int Count);
