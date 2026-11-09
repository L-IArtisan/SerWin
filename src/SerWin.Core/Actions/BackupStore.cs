using System.Text.Json;
using Microsoft.Win32;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;
using SerWin.Core.Tasks;

namespace SerWin.Core.Actions;

/// <summary>Valeur de registre sauvegardée, sous une forme restaurable à la main avec reg add.</summary>
public sealed record RegistryValueBackup(string Type, object? Value);

public sealed record ServiceBackup
{
    public required ServiceInfo Service { get; init; }
    /// <summary>Clé HKLM\SYSTEM\CurrentControlSet\Services\&lt;nom&gt; et sous-clés : chemin relatif → valeurs.</summary>
    public Dictionary<string, Dictionary<string, RegistryValueBackup>> Registry { get; init; } = [];
}

public sealed record LinkedTaskBackup(string Path, bool Enabled, string? Xml);

public sealed record BackupFile
{
    public int SchemaVersion { get; init; } = 1;
    public DateTimeOffset TakenAt { get; init; } = DateTimeOffset.Now;
    public string Label { get; init; } = "";
    public List<ServiceBackup> Services { get; init; } = [];

    /// <summary>Tâches planifiées liées aux services sauvegardés, avec leur définition XML complète.</summary>
    public List<LinkedTaskBackup> LinkedTasks { get; init; } = [];
}

/// <summary>Sauvegarde automatique avant chaque modification (spec §5.3) : %ProgramData%\SerWin\backups\*.json.</summary>
public sealed class BackupStore(string dataDirectory)
{
    public string Directory { get; } = Path.Combine(dataDirectory, "backups");

    /// <returns>Nom du fichier créé (relatif à <see cref="Directory"/>).</returns>
    public string Save(IEnumerable<ServiceInfo> services, string label, IEnumerable<ScheduledTaskInfo>? linkedTasks = null)
    {
        var backup = new BackupFile
        {
            Label = label,
            Services = services.Select(s => new ServiceBackup { Service = s, Registry = DumpServiceKey(s.Name) }).ToList(),
            LinkedTasks = (linkedTasks ?? []).DistinctBy(t => t.Path).Select(t => new LinkedTaskBackup(t.Path, t.Enabled, t.Xml)).ToList(),
        };
        System.IO.Directory.CreateDirectory(Directory);
        var safe = string.Concat(label.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
        if (safe.Length > 60) safe = safe[..60];
        var name = $"{backup.TakenAt:yyyyMMdd-HHmmss-fff}_{safe}.json";
        File.WriteAllText(Path.Combine(Directory, name), JsonSerializer.Serialize(backup, SnapshotStore.JsonOptions));
        return name;
    }

    public BackupFile? Load(string name)
    {
        var path = Path.Combine(Directory, Path.GetFileName(name));
        return File.Exists(path) ? JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), SnapshotStore.JsonOptions) : null;
    }

    internal static Dictionary<string, Dictionary<string, RegistryValueBackup>> DumpServiceKey(string serviceName, int maxDepth = 3)
    {
        var result = new Dictionary<string, Dictionary<string, RegistryValueBackup>>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            if (key is not null) Dump(key, "", 0);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Clé non lisible : la sauvegarde garde au moins la configuration SCM.
        }
        return result;

        void Dump(RegistryKey key, string path, int depth)
        {
            var values = new Dictionary<string, RegistryValueBackup>();
            foreach (var name in key.GetValueNames())
            {
                try
                {
                    var kind = key.GetValueKind(name);
                    var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    object? value = raw switch
                    {
                        byte[] b => Convert.ToHexString(b),
                        _ => raw,
                    };
                    values[name.Length == 0 ? "(default)" : name] = new RegistryValueBackup(kind.ToString(), value);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                }
            }
            result[path] = values;
            if (depth >= maxDepth) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var child = key.OpenSubKey(sub);
                    if (child is not null) Dump(child, path.Length == 0 ? sub : $@"{path}\{sub}", depth + 1);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
                {
                }
            }
        }
    }
}
