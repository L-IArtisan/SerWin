using System.Text.Json;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;

namespace SerWin.Core.Actions;

/// <summary>
/// Sérialisation des valeurs « avant / après » des changements riches (triggers, récupération) dans l'historique.
/// Le JSON reste lisible et contient tout ce qu'il faut pour restaurer à l'identique.
/// </summary>
public static class ChangeCodec
{
    private static readonly JsonSerializerOptions Compact = new(SnapshotStore.JsonOptions) { WriteIndented = false };

    public static string Triggers(IEnumerable<ServiceTrigger> triggers) => JsonSerializer.Serialize(triggers.ToList(), Compact);

    public static List<ServiceTrigger> Triggers(string json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<ServiceTrigger>>(json, Compact) ?? [];

    public static string Recovery(RecoveryConfig? recovery) => JsonSerializer.Serialize(recovery ?? new RecoveryConfig(), Compact);

    public static RecoveryConfig Recovery(string json) =>
        string.IsNullOrWhiteSpace(json) ? new RecoveryConfig() : JsonSerializer.Deserialize<RecoveryConfig>(json, Compact) ?? new RecoveryConfig();

    /// <summary>Récupération neutralisée : aucune action (on garde la période de remise à zéro).</summary>
    public static RecoveryConfig NoRecovery(RecoveryConfig? current) =>
        new() { ResetPeriodSeconds = current?.ResetPeriodSeconds ?? 0, Actions = [] };

    /// <summary>Clé de comparaison des triggers (ce que Windows renvoie réellement).</summary>
    public static string TriggerKey(IEnumerable<ServiceTrigger> triggers) =>
        string.Join("|", triggers.Select(t => $"{t.RawType}/{t.RawAction}/{t.Subtype}/{string.Join(",", t.RawData.Select(d => $"{d.DataType}:{d.Base64}"))}"));

    public static string RecoveryKey(RecoveryConfig? r) =>
        r is null || r.Actions.All(a => a.Type == RecoveryActionType.None)
            ? "none"
            : string.Join(",", r.Actions.Select(a => $"{a.Type}:{a.DelayMs}")) + $"|{r.Command}";
}
