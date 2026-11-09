using System.ComponentModel;
using SerWin.Core.Actions;
using SerWin.Core.Safety;
using SerWin.Core.Services;

namespace SerWin.Core.Processes;

public enum TerminateBlock
{
    None,
    /// <summary>Processus de la liste rouge (csrss, lsass, svchost…).</summary>
    RedList,
    /// <summary>Marqué critique par Windows : le terminer provoque un écran bleu.</summary>
    Critical,
    /// <summary>SerWin lui-même, ou pseudo-processus (Idle, System).</summary>
    Self,
    NotFound,
}

public sealed record TerminatePlan(ProcessInfo Process, TerminateBlock Blocked, IReadOnlyList<ServiceInfo> HostedServices)
{
    public bool IsBlocked => Blocked != TerminateBlock.None;
}

/// <summary>« Terminer un processus » (spec §5.6), soumis à la liste rouge processus (spec §5.3).</summary>
public sealed class ProcessActions(HistoryStore history)
{
    public static TerminatePlan Plan(ProcessInfo process, IReadOnlyCollection<ServiceInfo> services)
    {
        var hosted = services.Where(s => s.ProcessId == process.Pid && process.Pid != 0).ToList();
        var block =
            process.Pid is 0 or 4 || process.Pid == Environment.ProcessId ? TerminateBlock.Self
            : SafetyLists.RedProcesses.Contains(process.Name) ? TerminateBlock.RedList
            : process.IsCritical ? TerminateBlock.Critical
            // Héberger un service rouge rend le processus intouchable.
            : hosted.Any(s => SafetyLists.ForService(s.TemplateName) == SafetyLevel.Red) ? TerminateBlock.RedList
            : TerminateBlock.None;
        return new TerminatePlan(process, block, hosted);
    }

    /// <summary>
    /// Termine le processus et l'inscrit à l'historique (non annulable : un processus terminé ne se « ressuscite » pas).
    /// Vérifie que le PID désigne toujours le même processus (même date de démarrage) avant d'agir.
    /// </summary>
    public HistoryEntry Execute(TerminatePlan plan)
    {
        if (plan.IsBlocked) throw new InvalidOperationException($"Terminaison bloquée : {plan.Blocked}");
        var p = plan.Process;
        using (var proc = System.Diagnostics.Process.GetProcessById(p.Pid))
        {
            if (p.StartTime is { } expected && Math.Abs((proc.StartTime - expected).TotalSeconds) > 1)
                throw new ProcessChangedException(p.Pid);
            try
            {
                proc.Kill();
                proc.WaitForExit(5000);
            }
            catch (Win32Exception ex)
            {
                throw new ServiceActionException(p.Name, ServiceActionException.Map(ex.NativeErrorCode), ex.NativeErrorCode, ex.Message);
            }
        }

        var entry = new HistoryEntry
        {
            Action = ActionKind.TerminateProcess,
            Service = $"{p.Name} (PID {p.Pid})",
            Detail = plan.HostedServices.Count > 0 ? string.Join(", ", plan.HostedServices.Select(s => s.Name)) : null,
        };
        history.Add(entry);
        return entry;
    }
}

/// <summary>Le PID visé désigne désormais un autre processus (l'original s'est terminé entre-temps).</summary>
public sealed class ProcessChangedException(int pid) : InvalidOperationException($"PID {pid} reused by another process")
{
    public int Pid { get; } = pid;
}
