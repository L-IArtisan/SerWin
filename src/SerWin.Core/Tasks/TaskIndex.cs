using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using SerWin.Core.Services;

namespace SerWin.Core.Tasks;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskActionKind
{
    Exec,
    ComHandler,
    Other,
}

public sealed record TaskActionInfo
{
    public TaskActionKind Kind { get; init; }
    /// <summary>Exécutable (Exec) ou DLL du serveur COM résolue depuis le registre (ComHandler).</summary>
    public string? Path { get; init; }
    public string? Arguments { get; init; }
    public Guid? ClassId { get; init; }

    public override string ToString() => Kind switch
    {
        TaskActionKind.Exec => string.IsNullOrEmpty(Arguments) ? Path ?? "" : $"{Path} {Arguments}",
        TaskActionKind.ComHandler => $"COM {ClassId:B} → {Path ?? "?"}",
        _ => Kind.ToString(),
    };
}

/// <summary>Tâche planifiée, en lecture seule.</summary>
public sealed record ScheduledTaskInfo
{
    public required string Path { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; }
    public string State { get; init; } = "";
    public DateTime? LastRun { get; init; }
    public DateTime? NextRun { get; init; }
    public string? Author { get; init; }
    public List<TaskActionInfo> Actions { get; init; } = [];
    /// <summary>Descriptions des déclencheurs (texte fourni par le planificateur).</summary>
    public List<string> Triggers { get; init; } = [];
    /// <summary>Définition XML complète : permet une restauration exacte (sauvegardes).</summary>
    [JsonIgnore]
    public string? Xml { get; init; }

    public bool HasTriggers => Triggers.Count > 0;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TaskLinkReason
{
    /// <summary>Le chemin correspond à un motif de la base de connaissances.</summary>
    KnowledgePattern,
    /// <summary>Le nom du service apparaît dans la ligne de commande (sc start, net start…).</summary>
    NameInCommand,
    /// <summary>La tâche lance le même exécutable ou la même DLL que le service.</summary>
    SameBinary,
}

public sealed record LinkedTask(ScheduledTaskInfo Task, TaskLinkReason Reason);

/// <summary>Inventaire des tâches planifiées. Sans droits admin, certains dossiers protégés sont simplement ignorés.</summary>
public sealed class TaskIndex
{
    public IReadOnlyList<ScheduledTaskInfo> Tasks { get; }
    /// <summary>Nombre de dossiers illisibles (droits insuffisants).</summary>
    public int UnreadableFolders { get; }

    private TaskIndex(List<ScheduledTaskInfo> tasks, int unreadable)
    {
        Tasks = tasks;
        UnreadableFolders = unreadable;
    }

    public static TaskIndex Empty { get; } = new([], 0);

    /// <summary>Index construit à partir d'une liste (tests).</summary>
    internal static TaskIndex FromTasks(IEnumerable<ScheduledTaskInfo> tasks) => new([.. tasks], 0);

    public static TaskIndex Load()
    {
        var list = new List<ScheduledTaskInfo>();
        var unreadable = 0;
        using var ts = new TaskService();
        Walk(ts.RootFolder);
        list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new TaskIndex(list, unreadable);

        void Walk(TaskFolder folder)
        {
            try
            {
                foreach (var t in folder.Tasks)
                {
                    using (t)
                    {
                        var info = TryRead(t);
                        if (info is not null) list.Add(info);
                    }
                }
                foreach (var sub in folder.SubFolders)
                {
                    using (sub) Walk(sub);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or FileNotFoundException)
            {
                unreadable++;
            }
        }
    }

    private static ScheduledTaskInfo? TryRead(Microsoft.Win32.TaskScheduler.Task t)
    {
        try
        {
            var def = t.Definition;
            return new ScheduledTaskInfo
            {
                Path = t.Path,
                Name = t.Name,
                Enabled = t.Enabled,
                State = t.State.ToString(),
                LastRun = t.LastRunTime is var lr && lr.Year > 1999 ? lr : null,
                NextRun = t.NextRunTime is var nr && nr.Year > 1999 ? nr : null,
                Author = def.RegistrationInfo.Author,
                Actions = def.Actions.Select(ToInfo).ToList(),
                Triggers = def.Triggers.Select(tr => tr.ToString()).ToList(),
                Xml = t.Xml,
            };
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or InvalidOperationException or FileNotFoundException)
        {
            return null;
        }
    }

    private static TaskActionInfo ToInfo(Microsoft.Win32.TaskScheduler.Action a) => a switch
    {
        ExecAction e => new TaskActionInfo
        {
            Kind = TaskActionKind.Exec,
            Path = Environment.ExpandEnvironmentVariables(e.Path ?? "").Trim('"'),
            Arguments = e.Arguments,
        },
        ComHandlerAction c => new TaskActionInfo { Kind = TaskActionKind.ComHandler, ClassId = c.ClassId, Path = ResolveComServer(c.ClassId) },
        _ => new TaskActionInfo { Kind = TaskActionKind.Other },
    };

    private static string? ResolveComServer(Guid clsid)
    {
        try
        {
            using var k = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid:B}\InprocServer32");
            var v = k?.GetValue(null) as string;
            return string.IsNullOrWhiteSpace(v) ? null : Environment.ExpandEnvironmentVariables(v.Trim('"'));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tâches liées à un service : motifs connus (« \Microsoft\Windows\UpdateOrchestrator\* »),
    /// nom du service dans une commande, ou même binaire que le service.
    /// </summary>
    public List<LinkedTask> FindLinked(ServiceInfo service, IEnumerable<string>? knowledgePatterns = null)
    {
        var result = new Dictionary<string, LinkedTask>(StringComparer.OrdinalIgnoreCase);
        var patterns = (knowledgePatterns ?? []).Select(GlobToRegex).ToList();
        var nameRx = new Regex($@"(^|[\s""'/\\:=]){Regex.Escape(service.Name)}($|[\s""'/\\.:])", RegexOptions.IgnoreCase);
        var binaries = new[] { service.ServiceDll, IsSvchost(service.BinaryPath) ? null : ExecutablePath.Extract(service.BinaryPath) }
            .Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).ToList();

        foreach (var t in Tasks)
        {
            if (patterns.Any(p => p.IsMatch(t.Path)))
                result.TryAdd(t.Path, new LinkedTask(t, TaskLinkReason.KnowledgePattern));
            else if (t.Actions.Any(a => a.Kind == TaskActionKind.Exec && nameRx.IsMatch(a.Arguments ?? "")))
                result.TryAdd(t.Path, new LinkedTask(t, TaskLinkReason.NameInCommand));
            else if (t.Actions.Any(a => a.Path is { } p && binaries.Any(b => SamePath(p, b))))
                result.TryAdd(t.Path, new LinkedTask(t, TaskLinkReason.SameBinary));
        }
        return [.. result.Values];
    }

    public ScheduledTaskInfo? Find(string path) =>
        Tasks.FirstOrDefault(t => t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

    internal static Regex GlobToRegex(string glob) =>
        new("^" + Regex.Escape(glob).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase);

    private static bool IsSvchost(string? binaryPath) =>
        binaryPath?.Contains("svchost.exe", StringComparison.OrdinalIgnoreCase) == true;

    private static bool SamePath(string a, string b)
    {
        static string N(string p) => System.IO.Path.GetFullPath(ExecutablePath.NormalizeNtPath(Environment.ExpandEnvironmentVariables(p)))
            .Replace("SysWOW64", "System32", StringComparison.OrdinalIgnoreCase);
        try
        {
            return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
