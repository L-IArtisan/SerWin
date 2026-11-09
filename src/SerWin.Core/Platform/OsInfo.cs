using System.Security.Principal;
using Microsoft.Win32;

namespace SerWin.Core.Platform;

/// <summary>Édition et build de Windows (spec §3 : EditionID / CurrentBuild).</summary>
public sealed record OsInfo
{
    /// <summary>Ex. « Professional », « Core » (Famille), « Enterprise ».</summary>
    public string EditionId { get; init; } = "Unknown";
    public string ProductName { get; init; } = "Windows";
    public string DisplayVersion { get; init; } = "";
    public int Build { get; init; }
    public int Ubr { get; init; }

    public string FullBuild => Ubr > 0 ? $"{Build}.{Ubr}" : Build.ToString();

    /// <summary>Clé utilisée dans les fiches (« defaults ») : « Professional:26100 ».</summary>
    public string DefaultsKey => $"{EditionId}:{Build}";

    /// <summary>Windows 11 commence au build 22000 (ProductName reste « Windows 10 » dans le registre).</summary>
    public bool IsWindows11 => Build >= 22000;

    public string EditionFamily => EditionId switch
    {
        "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Core",
        "Professional" or "ProfessionalN" or "ProfessionalWorkstation" or "ProfessionalEducation" => "Professional",
        "Enterprise" or "EnterpriseN" or "EnterpriseS" or "Education" or "EducationN" => "Enterprise",
        _ => EditionId,
    };

    public string FriendlyName
    {
        get
        {
            var edition = EditionId switch
            {
                "Core" or "CoreN" or "CoreSingleLanguage" or "CoreCountrySpecific" => "Home",
                "Professional" or "ProfessionalN" => "Pro",
                "ProfessionalWorkstation" => "Pro for Workstations",
                _ => EditionId,
            };
            var name = IsWindows11 ? "Windows 11" : ProductName;
            return $"{name} {edition}";
        }
    }

    public static OsInfo Detect()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        if (k is null) return new OsInfo();
        _ = int.TryParse(k.GetValue("CurrentBuild") as string ?? k.GetValue("CurrentBuildNumber") as string, out var build);
        return new OsInfo
        {
            EditionId = k.GetValue("EditionID") as string ?? "Unknown",
            ProductName = k.GetValue("ProductName") as string ?? "Windows",
            DisplayVersion = k.GetValue("DisplayVersion") as string ?? "",
            Build = build,
            Ubr = k.GetValue("UBR") is int ubr ? ubr : 0,
        };
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
