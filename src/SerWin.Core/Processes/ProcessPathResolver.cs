using SerWin.Core.Safety;
using SerWin.Core.Services;

namespace SerWin.Core.Processes;

/// <summary>
/// Sans droits administrateur, Windows refuse l'accès aux processus des autres comptes (SYSTEM, services) :
/// leur chemin est alors déduit de sources sûres, et marqué comme tel.
/// </summary>
public static class ProcessPathResolver
{
    /// <summary>Pseudo-processus du noyau : pas de fichier exécutable.</summary>
    public static readonly IReadOnlySet<string> KernelPseudoProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "[System Process]", "System", "Secure System", "Registry", "Memory Compression", "Interrupts",
    };

    public static List<(ProcessInfo Process, bool PathInferred)> Resolve(IReadOnlyList<ProcessInfo> processes, IReadOnlyCollection<ServiceInfo> services)
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var byPid = services.Where(s => s.ProcessId != 0).ToLookup(s => (int)s.ProcessId);
        var result = new List<(ProcessInfo, bool)>(processes.Count);
        foreach (var p in processes)
        {
            if (p.Path is not null || KernelPseudoProcesses.Contains(p.Name))
            {
                result.Add((p, false));
                continue;
            }

            // 1. Le gestionnaire de services connaît le binaire de chaque service en cours.
            var fromService = byPid[p.Pid]
                .Select(s => ExecutablePath.Extract(s.BinaryPath))
                .FirstOrDefault(path => path is not null && Path.GetFileName(path).Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (fromService is not null)
            {
                result.Add((p with { Path = fromService }, true));
                continue;
            }

            // 2. Processus système de la liste rouge : toujours dans System32 (session 0 ou Windows lui-même).
            if (SafetyLists.RedProcesses.Contains(p.Name) && File.Exists(Path.Combine(system32, p.Name)))
            {
                result.Add((p with { Path = Path.Combine(system32, p.Name) }, true));
                continue;
            }

            result.Add((p, false));
        }
        return result;
    }
}
