using System.Management;

namespace SerWin.Core.Platform;

public enum RestorePointOutcome
{
    Created,
    /// <summary>Windows n'accepte qu'un point de restauration par 24 h (créé par une application) : aucun nouveau point.</summary>
    Throttled,
    /// <summary>Protection du système désactivée sur le lecteur système.</summary>
    Disabled,
    Failed,
}

public sealed record RestorePointResult(RestorePointOutcome Outcome, string? Detail = null);

/// <summary>Points de restauration Windows (WMI root\default:SystemRestore). Requiert les droits administrateur.</summary>
public static class RestorePoint
{
    private const int ModifySettings = 12;
    private const int BeginSystemChange = 100;

    public static RestorePointResult Create(string description)
    {
        try
        {
            var before = Latest();
            using var mc = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            using var input = mc.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = ModifySettings;
            input["EventType"] = BeginSystemChange;
            using var output = mc.InvokeMethod("CreateRestorePoint", input, null);
            var rv = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);

            // 1058 (ERROR_SERVICE_DISABLED) : protection du système désactivée.
            if (rv == 1058) return new RestorePointResult(RestorePointOutcome.Disabled);
            if (rv != 0) return new RestorePointResult(RestorePointOutcome.Failed, $"0x{rv:X}");

            var after = Latest();
            return after is not null && after != before
                ? new RestorePointResult(RestorePointOutcome.Created)
                : new RestorePointResult(RestorePointOutcome.Throttled, before?.ToString("g"));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return new RestorePointResult(RestorePointOutcome.Failed, ex.Message);
        }
    }

    /// <summary>Date du point de restauration le plus récent, ou null.</summary>
    public static DateTime? Latest()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT CreationTime FROM SystemRestore");
            DateTime? latest = null;
            foreach (var o in searcher.Get())
            {
                using (o)
                {
                    if (o["CreationTime"] is string s)
                    {
                        var t = ManagementDateTimeConverter.ToDateTime(s);
                        if (latest is null || t > latest) latest = t;
                    }
                }
            }
            return latest;
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
