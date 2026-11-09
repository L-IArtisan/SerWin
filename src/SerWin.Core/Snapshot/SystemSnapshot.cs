using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SerWin.Core.Services;
using SerWin.Core.Platform;

namespace SerWin.Core.Snapshot;

/// <summary>
/// Photographie complète de l'état des services (spec §5.4), prise au premier lancement.
/// « Tout restaurer » (v0.2) reviendra à cet état.
/// </summary>
public sealed record TaskState(string Path, bool Enabled);

public sealed record SystemSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset TakenAt { get; init; }
    public string AppVersion { get; init; } = "";
    public bool TakenElevated { get; init; }
    public required OsInfo Os { get; init; }
    public required List<ServiceInfo> Services { get; init; }
    /// <summary>État (activée / désactivée) de toutes les tâches planifiées lisibles. Absent des photographies v0.1-v0.2.</summary>
    public List<TaskState>? Tasks { get; init; }
}

/// <summary>Stockage dans %ProgramData%\SerWin\, en JSON lisible (restaurable à la main).</summary>
public sealed class SnapshotStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public string DataDirectory { get; }
    public string SnapshotPath => Path.Combine(DataDirectory, "snapshot.json");

    public SnapshotStore(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SerWin");
    }

    public bool Exists => File.Exists(SnapshotPath);

    public SystemSnapshot? Load()
    {
        if (!Exists) return null;
        try
        {
            using var fs = File.OpenRead(SnapshotPath);
            return JsonSerializer.Deserialize<SystemSnapshot>(fs, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Crée la photographie si elle n'existe pas encore. Ne l'écrase jamais.</summary>
    public SystemSnapshot EnsureCreated(OsInfo os, IReadOnlyList<ServiceInfo> services, string appVersion, IEnumerable<TaskState>? tasks = null)
    {
        var existing = Load();
        if (existing is not null) return existing;

        var snapshot = new SystemSnapshot
        {
            TakenAt = DateTimeOffset.Now,
            AppVersion = appVersion,
            TakenElevated = OsInfo.IsElevated(),
            Os = os,
            Services = [.. services],
            Tasks = tasks?.ToList(),
        };
        Directory.CreateDirectory(DataDirectory);
        var tmp = SnapshotPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOptions));
        File.Move(tmp, SnapshotPath, overwrite: false);
        return snapshot;
    }
}
