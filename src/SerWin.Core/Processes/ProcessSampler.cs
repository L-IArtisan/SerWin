using System.Runtime.InteropServices;
using static SerWin.Core.Interop.ProcessNative;

namespace SerWin.Core.Processes;

/// <summary>Un processus à un instant donné (consommation calculée depuis l'échantillon précédent).</summary>
public sealed record ProcessInfo
{
    public int Pid { get; init; }
    public int ParentPid { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Chemin complet de l'exécutable (null si inaccessible : processus protégé, autre utilisateur sans droits admin…).</summary>
    public string? Path { get; init; }
    public DateTime? StartTime { get; init; }
    public int SessionId { get; init; }
    public int Threads { get; init; }
    /// <summary>Mémoire privée (octets) — l'équivalent de « Mémoire » dans le Gestionnaire des tâches.</summary>
    public long PrivateBytes { get; init; }
    public long WorkingSet { get; init; }
    /// <summary>Pourcentage de la capacité totale du processeur (tous cœurs confondus).</summary>
    public double CpuPercent { get; init; }
    /// <summary>Lectures + écritures (octets/s), tous périphériques d'E/S confondus.</summary>
    public double IoBytesPerSec { get; init; }
    /// <summary>Processus marqué « critique » par Windows : le terminer provoque un écran bleu.</summary>
    public bool IsCritical { get; init; }
    /// <summary>Faux si les compteurs n'ont pas pu être lus (droits insuffisants).</summary>
    public bool CountersAvailable { get; init; }
}

/// <summary>
/// Échantillonne les processus (Toolhelp + compteurs kernel32). Appeler <see cref="Sample"/> périodiquement :
/// le CPU et les E/S sont calculés par différence avec l'appel précédent.
/// </summary>
public sealed class ProcessSampler
{
    private readonly record struct Previous(long CpuTicks, ulong IoBytes, long Timestamp);
    private Dictionary<(int Pid, long Created), Previous> _previous = [];
    private static readonly int Cores = Environment.ProcessorCount;

    public List<ProcessInfo> Sample()
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var next = new Dictionary<(int, long), Previous>();
        var result = new List<ProcessInfo>();

        foreach (var (pid, ppid, name, threads) in Enumerate())
        {
            ProcessIdToSessionId((uint)pid, out var session);
            string? path = null;
            DateTime? start = null;
            long priv = 0, ws = 0;
            double cpu = 0, io = 0;
            var critical = false;
            var counters = false;

            using (var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid))
            {
                if (!h.IsInvalid)
                {
                    path = ImagePath(h);
                    if (GetProcessTimes(h, out var created, out _, out var kernel, out var user))
                    {
                        counters = true;
                        if (created > 0) start = DateTime.FromFileTime(created);
                        var mem = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
                        if (GetProcessMemoryInfo(h, ref mem, mem.cb))
                        {
                            priv = (long)mem.PrivateUsage;
                            ws = (long)mem.WorkingSetSize;
                        }
                        var ioBytes = GetProcessIoCounters(h, out var ioc) ? ioc.ReadTransferCount + ioc.WriteTransferCount : 0UL;
                        var key = (pid, created);
                        var cpuTicks = kernel + user;
                        if (_previous.TryGetValue(key, out var prev))
                        {
                            var seconds = (now - prev.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                            if (seconds > 0)
                            {
                                // Les temps CPU sont en unités de 100 ns.
                                cpu = Math.Clamp((cpuTicks - prev.CpuTicks) / 1e7 / seconds / Cores * 100, 0, 100);
                                io = ioBytes >= prev.IoBytes ? (ioBytes - prev.IoBytes) / seconds : 0;
                            }
                        }
                        next[key] = new Previous(cpuTicks, ioBytes, now);
                    }
                    IsProcessCritical(h, out critical);
                }
            }

            result.Add(new ProcessInfo
            {
                Pid = pid, ParentPid = ppid, Name = name, Path = path, StartTime = start, SessionId = (int)session,
                Threads = threads, PrivateBytes = priv, WorkingSet = ws, CpuPercent = cpu, IoBytesPerSec = io,
                IsCritical = critical, CountersAvailable = counters,
            });
        }

        _previous = next;
        return result;
    }

    private static IEnumerable<(int Pid, int Ppid, string Name, int Threads)> Enumerate()
    {
        using var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap.IsInvalid) yield break;
        var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (!Process32FirstW(snap, ref e)) yield break;
        do
        {
            yield return ((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile, (int)e.cntThreads);
        }
        while (Process32NextW(snap, ref e));
    }

    private static string? ImagePath(Microsoft.Win32.SafeHandles.SafeProcessHandle h)
    {
        var buf = new char[1024];
        var size = (uint)buf.Length;
        return QueryFullProcessImageNameW(h, 0, buf, ref size) ? new string(buf, 0, (int)size) : null;
    }
}
