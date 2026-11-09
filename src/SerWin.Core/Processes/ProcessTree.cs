using SerWin.Core.Services;

namespace SerWin.Core.Processes;

/// <summary>Nœud de l'arbre parent → enfant, avec les services hébergés (svchost « dépliés »).</summary>
public sealed class ProcessNode(ProcessInfo process)
{
    public ProcessInfo Process { get; } = process;
    public List<ProcessNode> Children { get; } = [];
    public List<ServiceInfo> HostedServices { get; } = [];
}

public static class ProcessTree
{
    /// <summary>
    /// Construit l'arbre. Un PID parent peut avoir été réutilisé par un processus plus récent :
    /// dans ce cas l'enfant est rattaché à la racine (parent disparu).
    /// </summary>
    public static List<ProcessNode> Build(IReadOnlyList<ProcessInfo> processes, IReadOnlyCollection<ServiceInfo> services)
    {
        var nodes = processes.ToDictionary(p => p.Pid, p => new ProcessNode(p));
        foreach (var s in services.Where(s => s.ProcessId != 0))
            if (nodes.TryGetValue((int)s.ProcessId, out var host)) host.HostedServices.Add(s);
        foreach (var n in nodes.Values) n.HostedServices.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

        var roots = new List<ProcessNode>();
        foreach (var n in nodes.Values.OrderBy(n => n.Process.Pid))
        {
            var p = n.Process;
            if (p.ParentPid != p.Pid && p.Pid != 0 && nodes.TryGetValue(p.ParentPid, out var parent) && IsPlausibleParent(parent.Process, p))
                parent.Children.Add(n);
            else
                roots.Add(n);
        }
        foreach (var n in nodes.Values) n.Children.Sort((a, b) => Nullable.Compare(a.Process.StartTime, b.Process.StartTime));
        return roots;
    }

    private static bool IsPlausibleParent(ProcessInfo parent, ProcessInfo child) =>
        parent.StartTime is null || child.StartTime is null || parent.StartTime <= child.StartTime;

    /// <summary>Parcours en profondeur.</summary>
    public static IEnumerable<ProcessNode> Flatten(IEnumerable<ProcessNode> roots)
    {
        foreach (var r in roots)
        {
            yield return r;
            foreach (var c in Flatten(r.Children)) yield return c;
        }
    }
}
