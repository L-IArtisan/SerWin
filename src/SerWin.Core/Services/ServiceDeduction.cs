using System.Text.RegularExpressions;

namespace SerWin.Core.Services;

/// <summary>Rôle probable d'un service sans fiche, déduit de son nom, de sa description et de son exécutable.</summary>
public enum ServiceRole
{
    Unknown,
    Updater,
    Elevation,
    Telemetry,
    Licensing,
    Vpn,
    AntiCheat,
    Security,
    RemoteAccess,
    Virtualization,
    Bluetooth,
    Audio,
    Graphics,
    Printing,
    Hardware,
    Sync,
    Helper,
}

/// <summary>
/// « Fiche déduite » d'un service sans fiche rédigée : uniquement des faits locaux (logiciel d'origine, éditeur signé,
/// exécutable) et un rôle probable dont on donne l'indice (« update » dans le nom). Ce n'est jamais présenté comme sûr.
/// </summary>
/// <param name="RoleClue">Mot qui a permis de deviner le rôle (affiché pour la transparence).</param>
/// <param name="IsSecurityProduct">Service d'un éditeur d'antivirus connu : à ne pas désactiver, quel que soit son rôle.</param>
public sealed record DeducedProfile(
    ServiceRole Role,
    string? RoleClue,
    InstalledProgram? Program,
    string? Publisher,
    bool IsMicrosoft,
    bool IsSecurityProduct)
{
    public bool HasProgram => Program is not null;
}

public static class ServiceDeduction
{
    /// <summary>Indices par rôle, du plus spécifique au plus générique. Les indices courts (≤ 3 lettres) doivent être des mots entiers.</summary>
    private static readonly (ServiceRole Role, string[] Clues)[] Rules =
    [
        (ServiceRole.AntiCheat, ["anticheat", "anti-cheat", "battleye", "vanguard", "faceit", "punkbuster"]),
        (ServiceRole.Elevation, ["elevation", "elevate"]),
        (ServiceRole.Updater, ["update", "updater", "autoupdate", "upgrade", "maintenance", "upd"]),
        (ServiceRole.Telemetry, ["telemetry", "crashpad", "crash", "analytics", "usage report", "feedback", "diagnostic data"]),
        (ServiceRole.Licensing, ["licens", "ilok", "pace anti-piracy", "activation", "drm"]),
        (ServiceRole.Vpn, ["vpn", "wireguard", "openvpn", "tunnel"]),
        (ServiceRole.RemoteAccess, ["teamviewer", "anydesk", "rustdesk", "splashtop", "remote desktop", "remote access", "vnc"]),
        (ServiceRole.Virtualization, ["vmware", "virtualbox", "vbox", "virtual machine", "docker", "hyper-v", "vm"]),
        (ServiceRole.Security, ["antivirus", "anti-virus", "virus", "malware", "threat", "firewall", "protect", "shield", "security", "safepay"]),
        (ServiceRole.Bluetooth, ["bluetooth", "btman"]),
        (ServiceRole.Audio, ["audio", "sound", "asio", "speaker", "microphone"]),
        (ServiceRole.Graphics, ["nvidia", "radeon", "graphics", "display", "gpu"]),
        (ServiceRole.Printing, ["print", "scan", "spooler"]),
        (ServiceRole.Hardware, ["usb", "hid", "hardware", "device", "driver", "tpm", "sensor", "rgb", "keyboard", "mouse", "controller", "dongle"]),
        (ServiceRole.Sync, ["sync", "backup", "cloud", "drive"]),
        (ServiceRole.Helper, ["helper", "agent", "broker", "daemon", "integration", "host"]),
    ];

    /// <summary>Éditeurs d'antivirus : leurs services font partie de la protection, quel que soit leur nom.</summary>
    private static readonly string[] SecurityVendors =
    [
        "bitdefender", "kaspersky", "eset", "avast", "avg", "norton", "gen digital", "nortonlifelock", "symantec", "mcafee", "sophos",
        "trend micro", "malwarebytes", "g data", "f-secure", "panda security", "webroot", "avira", "emsisoft", "comodo", "check point",
    ];

    public static DeducedProfile Deduce(ServiceInfo info, InstalledPrograms? programs = null)
    {
        var exe = ExecutablePath.Extract(info.BinaryPath);
        var publisher = string.IsNullOrWhiteSpace(info.Publisher) ? null : info.Publisher.Trim();
        var isMicrosoft = publisher?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;

        // Le nom et le nom affiché d'abord (les plus parlants), puis la description et le nom du fichier.
        var text = string.Join(" | ", new[]
        {
            SplitWords(info.Name), info.DisplayName, info.Description, exe is null ? null : SplitWords(Path.GetFileNameWithoutExtension(exe)),
        }.Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();

        var (role, clue) = FindRole(text);
        var program = programs?.FindFor(exe ?? info.ServiceDll);
        var vendorText = $"{publisher} {program?.Publisher} {program?.Name}".ToLowerInvariant();
        var security = SecurityVendors.Any(v => vendorText.Contains(v));
        return new DeducedProfile(role, clue, program, publisher ?? program?.Publisher, isMicrosoft, security);
    }

    internal static (ServiceRole Role, string? Clue) FindRole(string text)
    {
        foreach (var (role, clues) in Rules)
            foreach (var clue in clues)
                if (clue.Length <= 3 ? Regex.IsMatch(text, $@"\b{Regex.Escape(clue)}\b") : text.Contains(clue, StringComparison.Ordinal))
                    return (role, clue);
        return (ServiceRole.Unknown, null);
    }

    /// <summary>Découpe « RtkBtManServ » / « NTKDaemonService » / « jhi_service » en mots pour la recherche d'indices.</summary>
    internal static string SplitWords(string s)
    {
        var spaced = Regex.Replace(s, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|[_\-\.]", " ");
        return $"{s} {spaced}";
    }
}
