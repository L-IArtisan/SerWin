using SerWin.Core.Platform;
using SerWin.Core.Services;
using SerWin.Core.Snapshot;

namespace SerWin.Tests;

public class ExecutablePathTests
{
    private static readonly string Windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Theory]
    [InlineData(@"C:\WINDOWS\system32\svchost.exe -k netsvcs -p", @"C:\WINDOWS\system32\svchost.exe")]
    [InlineData("\"C:\\Program Files\\Foo Bar\\foo.exe\" --service", @"C:\Program Files\Foo Bar\foo.exe")]
    [InlineData(@"C:\WINDOWS\System32\spoolsv.exe", @"C:\WINDOWS\System32\spoolsv.exe")]
    [InlineData(@"\??\C:\Drivers\x.sys", @"C:\Drivers\x.sys")]
    public void Extracts_executable(string commandLine, string expected) =>
        Assert.Equal(expected, ExecutablePath.Extract(commandLine), ignoreCase: true);

    [Fact]
    public void Expands_system_root_forms()
    {
        Assert.Equal(Path.Combine(Windir, @"system32\drivers\x.sys"), ExecutablePath.Extract(@"\SystemRoot\system32\drivers\x.sys"), ignoreCase: true);
        Assert.Equal(Path.Combine(Windir, @"system32\x.exe"), ExecutablePath.Extract(@"%SystemRoot%\system32\x.exe"), ignoreCase: true);
        Assert.Equal(Path.Combine(Windir, @"system32\drivers\y.sys"), ExecutablePath.Extract(@"system32\drivers\y.sys"), ignoreCase: true);
    }

    [Fact]
    public void Null_or_empty_gives_null()
    {
        Assert.Null(ExecutablePath.Extract(null));
        Assert.Null(ExecutablePath.Extract("  "));
    }
}

public class ServiceModelTests
{
    [Fact]
    public void Per_user_instance_maps_to_template()
    {
        var s = new ServiceInfo { Name = "CDPUserSvc_8c16c", ServiceType = 0xE0 };
        Assert.True(s.IsPerUserInstance);
        Assert.Equal("CDPUserSvc", s.TemplateName);

        var template = new ServiceInfo { Name = "CDPUserSvc", ServiceType = 0x60 };
        Assert.True(template.IsPerUserTemplate);
        Assert.Equal("CDPUserSvc", template.TemplateName);
    }

    [Fact]
    public void Third_party_detection_uses_publisher()
    {
        Assert.False(new ServiceInfo { Name = "a", Publisher = "Microsoft Corporation" }.IsThirdParty);
        Assert.True(new ServiceInfo { Name = "b", Publisher = "NVIDIA Corporation" }.IsThirdParty);
        Assert.False(new ServiceInfo { Name = "c", Publisher = null }.IsThirdParty);
    }
}

/// <summary>Tests sur le système réel : lecture seule, sans droits administrateur.</summary>
public class LiveSystemTests
{
    [Fact]
    public void Reads_services_without_admin_rights()
    {
        var services = new ServiceReader().ReadAll();
        Assert.True(services.Count > 100, $"Seulement {services.Count} services lus");

        var rpc = services.Single(s => s.Name.Equals("RpcSs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ServiceRunState.Running, rpc.State);
        Assert.Equal(ServiceStartMode.Automatic, rpc.StartMode);
        Assert.EndsWith("rpcss.dll", rpc.ServiceDll, StringComparison.OrdinalIgnoreCase);
        Assert.True(rpc.IsMicrosoft);
    }

    [Fact]
    public void Reads_triggers()
    {
        // Windows Update démarre sur application de stratégie de groupe (vérifié sur 26300).
        var wu = new ServiceReader().Read("wuauserv");
        Assert.NotNull(wu);
        Assert.Contains(wu!.Triggers, t => t.Kind == TriggerKind.GroupPolicy && t.SubtypeName == "MachinePolicyPresent");
    }

    [Fact]
    public void Detects_windows_edition_and_build()
    {
        var os = OsInfo.Detect();
        Assert.True(os.Build >= 22000, "SerWin cible Windows 11");
        Assert.NotEqual("Unknown", os.EditionId);
        Assert.Equal($"{os.EditionId}:{os.Build}", os.DefaultsKey);
    }
}

public class SnapshotTests
{
    [Fact]
    public void Snapshot_is_created_once_and_never_overwritten()
    {
        var dir = Path.Combine(Path.GetTempPath(), "serwin-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(dir);
            var os = new OsInfo { EditionId = "Professional", Build = 26100 };
            var first = store.EnsureCreated(os, [new ServiceInfo { Name = "A", StartMode = ServiceStartMode.Manual }], "0.1.0");
            Assert.True(store.Exists);

            var second = store.EnsureCreated(os, [new ServiceInfo { Name = "B" }], "0.1.0");
            Assert.Equal(first.TakenAt, second.TakenAt);
            Assert.Equal("A", Assert.Single(second.Services).Name);
            Assert.Equal(ServiceStartMode.Manual, second.Services[0].StartMode);

            // JSON lisible : les énumérations sont écrites en toutes lettres.
            Assert.Contains("\"Manual\"", File.ReadAllText(store.SnapshotPath));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
