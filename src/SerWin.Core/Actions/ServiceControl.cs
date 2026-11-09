using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using SerWin.Core.Services;
using ServiceStartMode = SerWin.Core.Services.ServiceStartMode;
using static SerWin.Core.Interop.NativeMethods;

namespace SerWin.Core.Actions;

/// <summary>Accès en écriture au gestionnaire de services. Interface pour pouvoir tester le moteur sans toucher au système.</summary>
public interface IServiceControl
{
    ServiceInfo? Read(string name);
    IReadOnlyList<ServiceInfo> ReadAll();
    void SetStartMode(string name, ServiceStartMode mode);
    void Start(string name, TimeSpan timeout);
    void Stop(string name, TimeSpan timeout);

    // ── v0.6 : neutralisation et verrouillage ────────────────
    /// <summary>Remplace tous les triggers du service (liste vide = suppression).</summary>
    void SetTriggers(string name, IReadOnlyList<ServiceTrigger> triggers);
    void SetRecovery(string name, RecoveryConfig recovery);
    /// <summary>État d'une tâche planifiée, null si introuvable ou illisible.</summary>
    bool? IsTaskEnabled(string path);
    void SetTaskEnabled(string path, bool enabled);
    /// <summary>Applique la section Access d'un SDDL à la clé HKLM\SYSTEM\CurrentControlSet\Services\&lt;nom&gt;.</summary>
    void SetRegistryAcl(string name, string sddl);

    // ── v1.1 : journal DNS ───────────────────────────────────
    /// <summary>Réglage encodé (EventLogSetting) d'un journal d'événements, null si illisible.</summary>
    string? GetEventLog(string log);
    void SetEventLog(string log, string setting);
}

/// <summary>Implémentation réelle (advapi32 + ServiceController). Requiert les droits administrateur.</summary>
public sealed class WindowsServiceControl : IServiceControl
{
    private readonly ServiceReader _reader = new();

    public ServiceInfo? Read(string name) => _reader.Read(name);
    public IReadOnlyList<ServiceInfo> ReadAll() => _reader.ReadAll();

    public void SetStartMode(string name, ServiceStartMode mode)
    {
        uint startType = mode switch
        {
            ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed => 2,
            ServiceStartMode.Manual => 3,
            ServiceStartMode.Disabled => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Type de démarrage non modifiable"),
        };

        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Fail(name);
        try
        {
            var h = OpenServiceW(scm, name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
            if (h == IntPtr.Zero) throw Fail(name);
            try
            {
                if (!ChangeServiceConfigW(h, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw Fail(name);

                if (startType == 2)
                {
                    var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = mode == ServiceStartMode.AutomaticDelayed ? 1 : 0 };
                    if (!ChangeServiceConfig2W(h, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info)) throw Fail(name);
                }
            }
            finally
            {
                CloseServiceHandle(h);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public void Start(string name, TimeSpan timeout) => Control(name, timeout, sc =>
    {
        if (sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending) return ServiceControllerStatus.Running;
        sc.Start();
        return ServiceControllerStatus.Running;
    });

    public void Stop(string name, TimeSpan timeout) => Control(name, timeout, sc =>
    {
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending) return ServiceControllerStatus.Stopped;
        // Les dépendants sont arrêtés explicitement par le moteur (et journalisés) : jamais implicitement ici.
        sc.Stop(stopDependentServices: false);
        return ServiceControllerStatus.Stopped;
    });

    private static void Control(string name, TimeSpan timeout, Func<ServiceController, ServiceControllerStatus> action)
    {
        try
        {
            using var sc = new ServiceController(name);
            var target = action(sc);
            sc.WaitForStatus(target, timeout);
        }
        catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception w)
        {
            // 1056 : déjà démarré / 1062 : déjà arrêté → l'objectif est atteint.
            if (w.NativeErrorCode is 1056 or 1062) return;
            throw new ServiceActionException(name, ServiceActionException.Map(w.NativeErrorCode), w.NativeErrorCode, w.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new ServiceActionException(name, ServiceActionError.NotFound, 1060, ex.Message);
        }
        catch (System.ServiceProcess.TimeoutException ex)
        {
            throw new ServiceActionException(name, ServiceActionError.Timeout, 1053, ex.Message);
        }
    }

    // ── v0.6 ─────────────────────────────────────────────────
    private static void WithService(string name, uint access, Action<IntPtr> action)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw Fail(name);
        try
        {
            var h = OpenServiceW(scm, name, access);
            if (h == IntPtr.Zero) throw Fail(name);
            try
            {
                action(h);
            }
            finally
            {
                CloseServiceHandle(h);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    public void SetTriggers(string name, IReadOnlyList<ServiceTrigger> triggers)
    {
        var allocations = new List<IntPtr>();
        IntPtr Alloc(int size)
        {
            var p = Marshal.AllocHGlobal(Math.Max(size, 1));
            allocations.Add(p);
            return p;
        }

        try
        {
            var tSize = Marshal.SizeOf<SERVICE_TRIGGER>();
            var dSize = Marshal.SizeOf<SERVICE_TRIGGER_SPECIFIC_DATA_ITEM>();
            var array = triggers.Count == 0 ? IntPtr.Zero : Alloc(tSize * triggers.Count);
            for (var i = 0; i < triggers.Count; i++)
            {
                var t = triggers[i];
                var subtype = IntPtr.Zero;
                if (t.Subtype is { } g)
                {
                    subtype = Alloc(16);
                    Marshal.Copy(g.ToByteArray(), 0, subtype, 16);
                }
                var items = t.RawData.Count == 0 ? IntPtr.Zero : Alloc(dSize * t.RawData.Count);
                for (var j = 0; j < t.RawData.Count; j++)
                {
                    var bytes = t.RawData[j].Bytes;
                    var data = Alloc(bytes.Length);
                    Marshal.Copy(bytes, 0, data, bytes.Length);
                    Marshal.StructureToPtr(new SERVICE_TRIGGER_SPECIFIC_DATA_ITEM
                    {
                        dwDataType = t.RawData[j].DataType,
                        cbData = (uint)bytes.Length,
                        pData = bytes.Length == 0 ? IntPtr.Zero : data,
                    }, items + j * dSize, false);
                }
                Marshal.StructureToPtr(new SERVICE_TRIGGER
                {
                    dwTriggerType = t.RawType,
                    dwAction = t.RawAction,
                    pTriggerSubtype = subtype,
                    cDataItems = (uint)t.RawData.Count,
                    pDataItems = items,
                }, array + i * tSize, false);
            }

            var info = Alloc(Marshal.SizeOf<SERVICE_TRIGGER_INFO>());
            Marshal.StructureToPtr(new SERVICE_TRIGGER_INFO { cTriggers = (uint)triggers.Count, pTriggers = array }, info, false);
            WithService(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG, h =>
            {
                if (!ChangeServiceConfig2Ptr(h, SERVICE_CONFIG_TRIGGER_INFO, info)) throw Fail(name);
            });
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    public void SetRecovery(string name, RecoveryConfig recovery)
    {
        // « Redémarrer l'ordinateur » exige SeShutdownPrivilege ; « redémarrer le service » exige SERVICE_START.
        if (recovery.Actions.Any(a => a.Type == RecoveryActionType.Reboot)) Interop.Privileges.Enable("SeShutdownPrivilege");
        var size = Marshal.SizeOf<SC_ACTION>();
        var actions = Marshal.AllocHGlobal(Math.Max(size * recovery.Actions.Count, 1));
        // Chaîne vide = supprimer la valeur ; null = ne pas modifier. On écrit l'état exact.
        var reboot = Marshal.StringToHGlobalUni(recovery.RebootMessage ?? "");
        var command = Marshal.StringToHGlobalUni(recovery.Command ?? "");
        var fa = Marshal.AllocHGlobal(Marshal.SizeOf<SERVICE_FAILURE_ACTIONS>());
        try
        {
            for (var i = 0; i < recovery.Actions.Count; i++)
                Marshal.StructureToPtr(new SC_ACTION { Type = (int)recovery.Actions[i].Type, Delay = recovery.Actions[i].DelayMs }, actions + i * size, false);
            Marshal.StructureToPtr(new SERVICE_FAILURE_ACTIONS
            {
                dwResetPeriod = recovery.ResetPeriodSeconds,
                lpRebootMsg = reboot,
                lpCommand = command,
                cActions = (uint)recovery.Actions.Count,
                lpsaActions = recovery.Actions.Count == 0 ? IntPtr.Zero : actions,
            }, fa, false);
            WithService(name, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG | SERVICE_START, h =>
            {
                if (!ChangeServiceConfig2Ptr(h, SERVICE_CONFIG_FAILURE_ACTIONS, fa)) throw Fail(name);
            });
        }
        finally
        {
            Marshal.FreeHGlobal(fa);
            Marshal.FreeHGlobal(command);
            Marshal.FreeHGlobal(reboot);
            Marshal.FreeHGlobal(actions);
        }
    }

    public bool? IsTaskEnabled(string path)
    {
        try
        {
            using var ts = new Microsoft.Win32.TaskScheduler.TaskService();
            using var t = ts.GetTask(path);
            return t?.Enabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException or FileNotFoundException)
        {
            return null;
        }
    }

    public void SetTaskEnabled(string path, bool enabled)
    {
        try
        {
            using var ts = new Microsoft.Win32.TaskScheduler.TaskService();
            using var t = ts.GetTask(path) ?? throw new ServiceActionException(path, ServiceActionError.NotFound, 2, "Tâche introuvable");
            t.Enabled = enabled;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or COMException)
        {
            throw new ServiceActionException(path, ServiceActionError.AccessDenied, 5, ex.Message);
        }
    }

    public void SetRegistryAcl(string name, string sddl)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}",
                Microsoft.Win32.RegistryKeyPermissionCheck.ReadWriteSubTree,
                System.Security.AccessControl.RegistryRights.ChangePermissions | System.Security.AccessControl.RegistryRights.ReadPermissions)
                ?? throw new ServiceActionException(name, ServiceActionError.NotFound, 2, "Clé introuvable");
            var rs = new System.Security.AccessControl.RegistrySecurity();
            rs.SetSecurityDescriptorSddlForm(sddl, System.Security.AccessControl.AccessControlSections.Access);
            key.SetAccessControl(rs);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            throw new ServiceActionException(name, ServiceActionError.AccessDenied, 5, ex.Message);
        }
    }

    public string? GetEventLog(string log) =>
        log == Network.DnsLog.LogName ? Network.DnsLog.ReadSetting()?.Encode() : null;

    public void SetEventLog(string log, string setting)
    {
        if (log != Network.DnsLog.LogName || Network.EventLogSetting.Parse(setting) is not { } s)
            throw new ServiceActionException(log, ServiceActionError.NotFound, 2, "Journal non pris en charge");
        try
        {
            Network.DnsLog.WriteSetting(s);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Diagnostics.Eventing.Reader.EventLogException)
        {
            throw new ServiceActionException(log, ServiceActionError.AccessDenied, 5, ex.Message);
        }
    }

    private static ServiceActionException Fail(string name)
    {
        var err = Marshal.GetLastWin32Error();
        return new ServiceActionException(name, ServiceActionException.Map(err), err, new Win32Exception(err).Message);
    }
}
