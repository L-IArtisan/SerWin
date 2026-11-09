using SerWin.App.Infrastructure;
using SerWin.App.Localization;
using SerWin.Core.Actions;
using SerWin.Core.Safety;

namespace SerWin.App.ViewModels;

/// <summary>Une entrée de l'historique, avec libellés localisés.</summary>
public sealed class HistoryItemViewModel(HistoryEntry entry, Func<string, string> displayName, bool canAct) : ObservableObject
{
    private static readonly Loc L = Loc.Instance;

    public HistoryEntry Entry { get; } = entry;
    public Guid Id => Entry.Id;

    public string When => Entry.At.LocalDateTime.ToString("g", L.Culture);

    public string Title => Entry.Action switch
    {
        ActionKind.Start => L.Format("History_Start", displayName(Entry.Service)),
        ActionKind.Stop => L.Format("History_Stop", displayName(Entry.Service)),
        ActionKind.SetStartMode => L.Format("History_SetStartMode", displayName(Entry.Service), L[$"StartMode_{Entry.StartMode}"]),
        ActionKind.Undo => L["History_Undo"],
        ActionKind.RestoreAll => L.Format("History_RestoreAll", Entry.Changes.Count),
        ActionKind.TerminateProcess => L.Format("History_Terminate", Entry.Service),
        ActionKind.ApplySuggestions => L.Format("History_Suggestions", Entry.Changes.Count(c => c.Kind == ChangeKind.StartMode)),
        ActionKind.Neutralize => L.Format("History_Neutralize", displayName(Entry.Service)),
        ActionKind.LockAcl => L.Format("History_LockAcl", displayName(Entry.Service)),
        ActionKind.UnlockAcl => L.Format("History_UnlockAcl", displayName(Entry.Service)),
        ActionKind.ConfigureDnsLog => L[Entry.Changes.Any(c => SerWin.Core.Network.EventLogSetting.Parse(c.After)?.Enabled == true) ? "History_DnsLogOn" : "History_DnsLogOff"],
        _ => Entry.Action.ToString(),
    };

    public IReadOnlyList<string> ChangeLines => Entry.Action == ActionKind.TerminateProcess
        ? Entry.Detail is { } hosted ? [L.Format("History_TerminateHosted", hosted)] : []
        : Entry.Changes.Select(c => Describe(c, displayName)).ToList();

    public string? Status =>
        Entry.IsUndone ? L.Format("History_Undone", Entry.UndoneAt!.Value.LocalDateTime.ToString("g", L.Culture))
        : Entry.Error is { } e ? L.Format("History_PartialError", L[$"ActError_{e}"])
        : null;
    public bool HasStatus => Status is not null;

    /// <summary>Une annulation ne s'annule pas elle-même : on refait l'action d'origine si besoin.</summary>
    public bool CanUndo => canAct && !Entry.IsUndone && Entry.Action is not (ActionKind.Undo or ActionKind.TerminateProcess);
    public string BackupFile => Entry.BackupFile ?? "";

    public static string Describe(ServiceChange c, Func<string, string> displayName) => c.Kind switch
    {
        ChangeKind.StartMode => L.Format("Change_StartMode", displayName(c.Service), L[$"StartMode_{c.Before}"], L[$"StartMode_{c.After}"]),
        ChangeKind.State when c.After == "Running" => L.Format("Change_Start", displayName(c.Service)),
        ChangeKind.State => L.Format("Change_Stop", displayName(c.Service)),
        ChangeKind.Triggers when ChangeCodec.Triggers(c.After).Count == 0 =>
            L.Format("Change_TriggersRemoved", displayName(c.Service), ChangeCodec.Triggers(c.Before).Count),
        ChangeKind.Triggers => L.Format("Change_TriggersRestored", displayName(c.Service), ChangeCodec.Triggers(c.After).Count),
        ChangeKind.Recovery when ChangeCodec.RecoveryKey(ChangeCodec.Recovery(c.After)) == "none" =>
            L.Format("Change_RecoveryDisabled", displayName(c.Service)),
        ChangeKind.Recovery => L.Format("Change_RecoveryRestored", displayName(c.Service)),
        ChangeKind.TaskEnabled when c.After == bool.FalseString => L.Format("Change_TaskDisabled", c.Target),
        ChangeKind.TaskEnabled => L.Format("Change_TaskEnabled", c.Target),
        ChangeKind.RegistryAcl when AclLock.IsLocked(c.After) => L.Format("Change_AclLocked", displayName(c.Service)),
        ChangeKind.RegistryAcl => L.Format("Change_AclUnlocked", displayName(c.Service)),
        ChangeKind.EventLog => SerWin.Core.Network.EventLogSetting.Parse(c.After) is { Enabled: true } on
            ? L.Format("Change_DnsLogOn", on.MaxSize / (1024 * 1024))
            : L["Change_DnsLogOff"],
        _ => c.Kind.ToString(),
    };

    /// <summary>Aperçu détaillé (spec §5.2) : une ligne par changement, plus le détail de chaque déclencheur supprimé ou recréé.</summary>
    public static IEnumerable<string> DescribeDetailed(ServiceChange c, Func<string, string> displayName)
    {
        yield return Describe(c, displayName);
        if (c.Kind == ChangeKind.Triggers)
        {
            var before = ChangeCodec.Triggers(c.Before);
            var after = ChangeCodec.Triggers(c.After);
            foreach (var t in after.Count == 0 ? before : after)
                yield return "    – " + ServiceItemViewModel.FormatTrigger(t);
        }
        else if (c.Kind == ChangeKind.Recovery)
        {
            var before = ChangeCodec.Recovery(c.Before);
            if (before.Actions.Count > 0)
                yield return "    – " + string.Join(", ", before.Actions.Select(a => L[$"RecoveryAction_{a.Type}"])) + " → " + L["RecoveryAction_None"];
        }
    }

    public void Refresh() => Raise(null);
}
