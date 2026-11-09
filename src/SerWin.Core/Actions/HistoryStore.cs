using System.Text.Json;
using SerWin.Core.Snapshot;

namespace SerWin.Core.Actions;

/// <summary>Entrée d'historique horodatée (spec §5.3). Chaque entrée est annulable individuellement.</summary>
public sealed record HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
    public ActionKind Action { get; init; }
    /// <summary>Service visé (vide pour « Tout restaurer »).</summary>
    public string Service { get; init; } = "";
    /// <summary>Type de démarrage demandé, pour SetStartMode.</summary>
    public string? StartMode { get; init; }
    /// <summary>Changements effectivement appliqués, dans l'ordre.</summary>
    public List<ServiceChange> Changes { get; init; } = [];
    /// <summary>Fichier de sauvegarde (relatif au dossier backups).</summary>
    public string? BackupFile { get; init; }
    public Guid? UndoOf { get; init; }
    public DateTimeOffset? UndoneAt { get; init; }
    public Guid? UndoneBy { get; init; }
    /// <summary>Erreur survenue en cours d'exécution (les changements listés ont tout de même été appliqués).</summary>
    public ServiceActionError? Error { get; init; }
    public string? ErrorMessage { get; init; }
    public string User { get; init; } = Environment.UserName;
    /// <summary>Informations complémentaires (ex. services hébergés par un processus terminé).</summary>
    public string? Detail { get; init; }

    public bool IsUndone => UndoneAt is not null;
}

/// <summary>Historique persistant : %ProgramData%\SerWin\history.json (JSON lisible).</summary>
public sealed class HistoryStore(string dataDirectory)
{
    private readonly object _lock = new();
    public string FilePath { get; } = Path.Combine(dataDirectory, "history.json");

    public List<HistoryEntry> All()
    {
        lock (_lock) return Load().OrderByDescending(e => e.At).ToList();
    }

    public HistoryEntry? Get(Guid id)
    {
        lock (_lock) return Load().FirstOrDefault(e => e.Id == id);
    }

    public void Add(HistoryEntry entry)
    {
        lock (_lock)
        {
            var list = Load();
            list.Add(entry);
            Save(list);
        }
    }

    public void MarkUndone(Guid id, Guid undoneBy)
    {
        lock (_lock)
        {
            var list = Load();
            var i = list.FindIndex(e => e.Id == id);
            if (i < 0) return;
            list[i] = list[i] with { UndoneAt = DateTimeOffset.Now, UndoneBy = undoneBy };
            Save(list);
        }
    }

    private List<HistoryEntry> Load()
    {
        if (!File.Exists(FilePath)) return [];
        var json = File.ReadAllText(FilePath);
        return JsonSerializer.Deserialize<List<HistoryEntry>>(json, SnapshotStore.JsonOptions) ?? [];
    }

    private void Save(List<HistoryEntry> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, SnapshotStore.JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
