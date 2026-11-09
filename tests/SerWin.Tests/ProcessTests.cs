using SerWin.Core.Actions;
using SerWin.Core.Processes;
using SerWin.Core.Safety;
using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.Tests;

public class ProcessTreeTests
{
    private static readonly DateTime T0 = new(2026, 11, 9, 8, 0, 0);

    private static ProcessInfo P(int pid, int ppid, string name, int minutes) =>
        new() { Pid = pid, ParentPid = ppid, Name = name, StartTime = T0.AddMinutes(minutes) };

    [Fact]
    public void Builds_parent_child_tree_and_unfolds_svchost()
    {
        var procs = new[] { P(700, 600, "services.exe", 0), P(600, 1, "wininit.exe", 0), P(900, 700, "svchost.exe", 1), P(901, 700, "svchost.exe", 1) };
        var services = new[]
        {
            new ServiceInfo { Name = "Dnscache", DisplayName = "Client DNS", ProcessId = 900 },
            new ServiceInfo { Name = "Dhcp", DisplayName = "Client DHCP", ProcessId = 900 },
            new ServiceInfo { Name = "Spooler", ProcessId = 0 },
        };
        var roots = ProcessTree.Build(procs, services);
        var wininit = Assert.Single(roots);
        var svcs = Assert.Single(wininit.Children);
        Assert.Equal("services.exe", svcs.Process.Name);
        Assert.Equal(2, svcs.Children.Count);
        var host = svcs.Children.Single(c => c.Process.Pid == 900);
        Assert.Equal(["Client DHCP", "Client DNS"], host.HostedServices.Select(s => s.DisplayName));
        Assert.Empty(svcs.Children.Single(c => c.Process.Pid == 901).HostedServices);
    }

    [Fact]
    public void Reused_parent_pid_is_not_a_parent()
    {
        // Le parent d'origine (PID 50) a disparu ; un processus plus récent a reçu le même PID.
        var roots = ProcessTree.Build([P(50, 1, "newcomer.exe", 30), P(60, 50, "orphan.exe", 10)], []);
        Assert.Equal(2, roots.Count);
        Assert.All(roots, r => Assert.Empty(r.Children));
    }
}

public class TerminatePlanTests
{
    private static ProcessInfo P(string name, int pid = 1234, bool critical = false) => new() { Pid = pid, Name = name, IsCritical = critical };

    [Theory]
    [InlineData("lsass.exe")]
    [InlineData("csrss.exe")]
    [InlineData("svchost.exe")]
    [InlineData("SVCHOST.EXE")]
    public void Red_list_processes_are_blocked(string name) =>
        Assert.Equal(TerminateBlock.RedList, ProcessActions.Plan(P(name), []).Blocked);

    [Fact]
    public void Critical_processes_are_blocked() =>
        Assert.Equal(TerminateBlock.Critical, ProcessActions.Plan(P("odd.exe", critical: true), []).Blocked);

    [Fact]
    public void Self_and_kernel_are_blocked()
    {
        Assert.Equal(TerminateBlock.Self, ProcessActions.Plan(P("SerWin.exe", Environment.ProcessId), []).Blocked);
        Assert.Equal(TerminateBlock.Self, ProcessActions.Plan(P("x", 4), []).Blocked);
    }

    [Fact]
    public void Process_hosting_a_red_service_is_blocked()
    {
        var plan = ProcessActions.Plan(P("host.exe", 42), [new ServiceInfo { Name = "RpcSs", ProcessId = 42 }]);
        Assert.Equal(TerminateBlock.RedList, plan.Blocked);
    }

    [Fact]
    public void Ordinary_process_lists_hosted_services()
    {
        var plan = ProcessActions.Plan(P("vendor.exe", 42), [new ServiceInfo { Name = "VendorSvc", ProcessId = 42 }]);
        Assert.False(plan.IsBlocked);
        Assert.Equal("VendorSvc", Assert.Single(plan.HostedServices).Name);
    }

    [Fact]
    public void Terminated_process_entry_cannot_be_undone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var history = new HistoryStore(dir);
            var entry = new HistoryEntry { Action = ActionKind.TerminateProcess, Service = "x.exe (PID 1)" };
            history.Add(entry);
            var engine = new ActionEngine(new FakeServiceControl(), history, new BackupStore(dir));
            Assert.Equal(BlockReason.NotUndoable, engine.PlanUndo(entry.Id).Blocked);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Terminate_really_kills_an_ordinary_process()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-proc-" + Guid.NewGuid().ToString("N"));
        using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            var info = new ProcessSampler().Sample().Single(p => p.Pid == child.Id);
            var entry = new ProcessActions(new HistoryStore(dir)).Execute(ProcessActions.Plan(info, []));
            Assert.True(child.WaitForExit(5000));
            Assert.Equal(ActionKind.TerminateProcess, entry.Action);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}

public class LiveProcessTests
{
    [Fact]
    public void Samples_own_process_with_counters()
    {
        var sampler = new ProcessSampler();
        sampler.Sample();
        Thread.Sleep(200);
        var me = sampler.Sample().Single(p => p.Pid == Environment.ProcessId);
        Assert.True(me.CountersAvailable);
        Assert.NotNull(me.Path);
        Assert.True(me.PrivateBytes > 0);
        Assert.InRange(me.CpuPercent, 0, 100);
        Assert.NotNull(me.StartTime);
    }

    [Fact]
    public void Verifies_catalog_and_embedded_signatures_offline()
    {
        var svchost = SignatureVerifier.Verify(Path.Combine(Environment.SystemDirectory, "svchost.exe"));
        Assert.True(svchost.IsTrusted, svchost.ToString());
        Assert.True(svchost.IsMicrosoft, svchost.Signer);

        var dotnet = SignatureVerifier.Verify(Environment.ProcessPath);
        Assert.Equal(SignatureStatus.Signed, dotnet.Status);
    }

    [Fact]
    public void Random_file_is_unsigned_and_missing_file_is_unknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"serwin-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, Guid.NewGuid().ToByteArray());
        try
        {
            Assert.Equal(SignatureStatus.Unsigned, SignatureVerifier.Verify(path).Status);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(SignatureStatus.Unknown, SignatureVerifier.Verify(@"C:\nope\missing.exe").Status);
    }

    [Fact]
    public void Suspicious_locations()
    {
        Assert.True(SuspiciousLocation.IsSuspicious(Path.Combine(Path.GetTempPath(), "a.exe")));
        Assert.True(SuspiciousLocation.IsSuspicious(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "setup.exe")));
        Assert.False(SuspiciousLocation.IsSuspicious(Path.Combine(Environment.SystemDirectory, "svchost.exe")));
        Assert.False(SuspiciousLocation.IsSuspicious(@"C:\Program Files\Vendor\app.exe"));
    }

    [Fact]
    public void Path_is_inferred_from_service_binary_when_access_is_denied()
    {
        var proc = new ProcessInfo { Pid = 77, Name = "vendor.exe" };
        var svc = new ServiceInfo { Name = "V", ProcessId = 77, BinaryPath = "\"C:\\Program Files\\V\\vendor.exe\" -service" };
        var (p, inferred) = Assert.Single(ProcessPathResolver.Resolve([proc], [svc]));
        Assert.True(inferred);
        Assert.Equal(@"C:\Program Files\V\vendor.exe", p.Path);
    }
}

public class ProcessKnowledgeTests
{
    [Fact]
    public void Critical_process_entries_are_on_the_red_list()
    {
        foreach (var e in KnowledgeBase.LoadEmbedded().Processes.Where(e => e.Risk == RiskLevel.Critical))
            Assert.True(SafetyLists.RedProcesses.Contains(e.Name), $"{e.Name} est critique mais absent de la liste rouge processus");
    }

    [Fact]
    public void Process_file_name_matches_entry_name()
    {
        var kb = KnowledgeBase.LoadEmbedded();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(KnowledgeTests.RepoRoot(), "knowledge", "processes"), "*.json"))
            Assert.Equal(Path.GetFileNameWithoutExtension(file), kb.FindProcess(Path.GetFileNameWithoutExtension(file))?.Name);
    }
}
