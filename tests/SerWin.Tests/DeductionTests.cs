using SerWin.Core.Services;
using SerWin.Knowledge;

namespace SerWin.Tests;

public class ServiceDeductionTests
{
    private static ServiceInfo Svc(string name, string display = "", string? description = null, string? binary = null, string? publisher = null) =>
        new() { Name = name, DisplayName = display, Description = description, BinaryPath = binary, Publisher = publisher };

    [Theory]
    // Cas réels relevés sur une machine (services tiers sans fiche).
    [InlineData("UPDATESRV", "Bitdefender Update Service", ServiceRole.Updater)]
    [InlineData("BraveElevationService1d8c86c3484a902", "Brave Elevation Service (BraveElevationService)", ServiceRole.Elevation)]
    [InlineData("EasyAntiCheat", "EasyAntiCheat", ServiceRole.AntiCheat)]
    [InlineData("PaceLicenseDServices", "PACE License Services", ServiceRole.Licensing)]
    [InlineData("ProtonVPN WireGuard", "ProtonVPN WireGuard", ServiceRole.Vpn)]
    [InlineData("RtkBtManServ", "Realtek Bluetooth Device Manager Service", ServiceRole.Bluetooth)]
    [InlineData("VSSERV", "Bitdefender Virus Shield", ServiceRole.Security)]
    [InlineData("NIHardwareService", "NIHardwareService", ServiceRole.Hardware)]
    [InlineData("DiscordSystemHelper", "Discord System Helper", ServiceRole.Helper)]
    [InlineData("Foo", "Foo", ServiceRole.Unknown)]
    public void Guesses_role_from_names(string name, string display, ServiceRole expected) =>
        Assert.Equal(expected, ServiceDeduction.Deduce(Svc(name, display)).Role);

    [Fact]
    public void Short_clues_must_be_whole_words()
    {
        Assert.Equal(ServiceRole.Unknown, ServiceDeduction.FindRole("nvmexpress storage").Role); // pas « vm »
        Assert.Equal(ServiceRole.Virtualization, ServiceDeduction.FindRole("my vm agent").Role);
    }

    [Fact]
    public void Camel_case_names_are_split_into_words() =>
        Assert.Contains("Rtk Bt Man Serv", ServiceDeduction.SplitWords("RtkBtManServ"));

    [Fact]
    public void Antivirus_vendor_is_flagged_whatever_the_role()
    {
        var p = ServiceDeduction.Deduce(Svc("UPDATESRV", "Bitdefender Update Service", publisher: "Bitdefender"));
        Assert.Equal(ServiceRole.Updater, p.Role);
        Assert.True(p.IsSecurityProduct);
        Assert.False(ServiceDeduction.Deduce(Svc("brave", "Brave Update", publisher: "BraveSoftware Inc.")).IsSecurityProduct);
    }

    [Fact]
    public void Finds_the_installed_program_owning_the_executable()
    {
        var programs = new InstalledPrograms(
        [
            new InstalledProgram("Native Instruments Native Access", "Native Instruments", "3.0", @"C:\Program Files\Native Instruments\Native Access"),
            new InstalledProgram("NI Hardware Service", "Native Instruments", "1.2", @"C:\Program Files\Native Instruments\Hardware"),
            new InstalledProgram("Windows thing", "Microsoft", "1", @"C:\Program Files"), // dossier générique : ignoré
        ]);
        var p = ServiceDeduction.Deduce(Svc("NIHardwareService", binary: @"""C:\Program Files\Native Instruments\Hardware\NIHardwareService.exe"""), programs);
        Assert.Equal("NI Hardware Service", p.Program?.Name);
        Assert.Equal("Native Instruments", p.Publisher);
        Assert.Null(programs.FindFor(@"C:\Program Files\Other\x.exe"));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"",0", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\uninst.exe /S", @"C:\Program Files\App\uninst.exe")]
    [InlineData(@"C:\Tools\icon.ico,1", @"C:\Tools\icon.ico")]
    public void Extracts_exe_from_uninstall_values(string value, string expected) =>
        Assert.Equal(expected, InstalledPrograms.ExePath(value));
}

public sealed class WildcardAliasTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "serwin-kb-" + Guid.NewGuid().ToString("N"));

    public WildcardAliasTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "services"));
        File.WriteAllText(Path.Combine(_dir, "services", "BraveElevationService.json"), """
            { "name": "BraveElevationService", "aliases": ["BraveElevationService*", "brave?"],
              "displayName": { "fr": "x", "en": "x" }, "category": "maintenance", "risk": "faible",
              "summary": { "fr": "x", "en": "x" },
              "levels": { "simple": { "fr": "x", "en": "x" }, "avance": { "fr": "x", "en": "x" }, "expert": { "fr": "x", "en": "x" } },
              "verification": { "status": "draft" } }
            """);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Wildcard_alias_covers_machine_specific_names()
    {
        var kb = KnowledgeBase.LoadFromDirectory(_dir);
        Assert.Equal("BraveElevationService", kb.FindService("BraveElevationService1d8c86c3484a902")?.Name);
        Assert.Equal("BraveElevationService", kb.FindService("braveelevationservice")?.Name);
        Assert.Equal("BraveElevationService", kb.FindService("bravem")?.Name);   // « ? » = un caractère
        Assert.Null(kb.FindService("bravemx"));
        Assert.Null(kb.FindService("NotBraveElevationService1"));
        Assert.Equal(1, kb.ServiceCount);
    }
}
