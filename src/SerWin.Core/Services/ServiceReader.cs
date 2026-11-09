using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32;
using static SerWin.Core.Interop.NativeMethods;

namespace SerWin.Core.Services;

/// <summary>
/// Lit l'état et la configuration des services via le SCM (advapi32), en lecture seule.
/// Fonctionne sans droits administrateur : les informations inaccessibles sont simplement omises.
/// </summary>
public sealed class ServiceReader
{
    /// <summary>Énumère tous les services Win32 et lit leur configuration complète.</summary>
    public List<ServiceInfo> ReadAll()
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var entries = Enumerate(scm);
            var result = new List<ServiceInfo>(entries.Count);
            foreach (var (name, display, status) in entries)
                result.Add(ReadOne(scm, name, display, status));
            result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            return result;
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    /// <summary>Relit un seul service (après changement d'état, par exemple).</summary>
    public ServiceInfo? Read(string serviceName)
    {
        var scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return null;
        try
        {
            var match = Enumerate(scm).FirstOrDefault(e => e.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            return match.Name is null ? null : ReadOne(scm, match.Name, match.Display, match.Status);
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static List<(string Name, string Display, SERVICE_STATUS_PROCESS Status)> Enumerate(IntPtr scm)
    {
        var list = new List<(string, string, SERVICE_STATUS_PROCESS)>();
        uint resume = 0;
        uint bufSize = 256 * 1024;
        while (true)
        {
            var buf = Marshal.AllocHGlobal((int)bufSize);
            try
            {
                var ok = EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                    buf, bufSize, out var needed, out var count, ref resume, null);
                var err = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && err != ERROR_MORE_DATA) throw new Win32Exception(err);

                var size = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();
                for (var i = 0; i < count; i++)
                {
                    var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buf + i * size);
                    list.Add((Marshal.PtrToStringUni(e.lpServiceName) ?? "",
                              Marshal.PtrToStringUni(e.lpDisplayName) ?? "",
                              e.ServiceStatusProcess));
                }
                if (ok) break;
                if (needed > bufSize) bufSize = needed;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return list;
    }

    private static ServiceInfo ReadOne(IntPtr scm, string name, string display, SERVICE_STATUS_PROCESS status)
    {
        var state = MapState(status.dwCurrentState);
        var h = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_ENUMERATE_DEPENDENTS | READ_CONTROL);
        if (h == IntPtr.Zero)
            h = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG | SERVICE_ENUMERATE_DEPENDENTS);
        if (h == IntPtr.Zero)
            h = OpenServiceW(scm, name, SERVICE_QUERY_CONFIG);

        if (h == IntPtr.Zero)
        {
            return new ServiceInfo
            {
                Name = name, DisplayName = display, State = state,
                ServiceType = status.dwServiceType, ProcessId = status.dwProcessId,
                ConfigReadable = false,
            };
        }

        try
        {
            var cfg = QueryConfig(h);
            var delayed = QueryDelayed(h);
            // Les instances par utilisateur (Xxx_1a2b3c) héritent de la DLL de leur modèle (Xxx).
            var serviceDll = ReadServiceDll(name);
            if (serviceDll is null && (status.dwServiceType & 0x80) != 0 && name.LastIndexOf('_') is > 0 and var i)
                serviceDll = ReadServiceDll(name[..i]);
            var binary = cfg?.BinaryPath;

            var startMode = cfg is null ? ServiceStartMode.Unknown : MapStartMode(cfg.Value.StartType, delayed);

            return new ServiceInfo
            {
                Name = name,
                DisplayName = display,
                Description = QueryDescription(h),
                State = state,
                StartMode = startMode,
                ServiceType = cfg?.ServiceType ?? status.dwServiceType,
                ErrorControl = cfg?.ErrorControl ?? 0,
                BinaryPath = binary,
                ServiceDll = serviceDll,
                Account = cfg?.Account,
                LoadOrderGroup = cfg?.LoadOrderGroup,
                ProcessId = status.dwProcessId,
                Dependencies = cfg?.Dependencies ?? [],
                DependentServices = QueryDependents(h),
                Triggers = QueryTriggers(h),
                Recovery = QueryRecovery(h),
                Publisher = ReadPublisher(serviceDll ?? ExecutablePath.Extract(binary)),
                RegistryKeySddl = ReadRegistrySddl(name),
                ServiceSddl = QueryServiceSddl(h),
                ConfigReadable = cfg is not null,
            };
        }
        finally
        {
            CloseServiceHandle(h);
        }
    }

    private readonly record struct RawConfig(uint ServiceType, uint StartType, uint ErrorControl,
        string? BinaryPath, string? LoadOrderGroup, List<string> Dependencies, string? Account);

    private static RawConfig? QueryConfig(IntPtr h)
    {
        QueryServiceConfigW(h, IntPtr.Zero, 0, out var needed);
        if (needed == 0) return null;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(h, buf, needed, out _)) return null;
            var c = Marshal.PtrToStructure<QUERY_SERVICE_CONFIG>(buf);
            return new RawConfig(c.dwServiceType, c.dwStartType, c.dwErrorControl,
                Marshal.PtrToStringUni(c.lpBinaryPathName),
                NullIfEmpty(Marshal.PtrToStringUni(c.lpLoadOrderGroup)),
                ReadMultiSz(c.lpDependencies),
                NullIfEmpty(Marshal.PtrToStringUni(c.lpServiceStartName)));
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Appelle QueryServiceConfig2 avec un tampon dimensionné et passe le pointeur à <paramref name="read"/>.</summary>
    private static T? QueryConfig2<T>(IntPtr h, uint level, Func<IntPtr, T> read)
    {
        QueryServiceConfig2W(h, level, IntPtr.Zero, 0, out var needed);
        if (needed == 0) return default;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            return QueryServiceConfig2W(h, level, buf, needed, out _) ? read(buf) : default;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? QueryDescription(IntPtr h) =>
        QueryConfig2(h, SERVICE_CONFIG_DESCRIPTION, p =>
            NullIfEmpty(Marshal.PtrToStringUni(Marshal.PtrToStructure<SERVICE_DESCRIPTION>(p).lpDescription)));

    private static bool QueryDelayed(IntPtr h) =>
        QueryConfig2(h, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, p =>
            Marshal.PtrToStructure<SERVICE_DELAYED_AUTO_START_INFO>(p).fDelayedAutostart != 0);

    private static RecoveryConfig? QueryRecovery(IntPtr h) =>
        QueryConfig2(h, SERVICE_CONFIG_FAILURE_ACTIONS, p =>
        {
            var fa = Marshal.PtrToStructure<SERVICE_FAILURE_ACTIONS>(p);
            var actions = new List<RecoveryAction>();
            var size = Marshal.SizeOf<SC_ACTION>();
            for (var i = 0; i < fa.cActions; i++)
            {
                var a = Marshal.PtrToStructure<SC_ACTION>(fa.lpsaActions + i * size);
                actions.Add(new RecoveryAction(a.Type switch
                {
                    1 => RecoveryActionType.Restart,
                    2 => RecoveryActionType.Reboot,
                    3 => RecoveryActionType.RunCommand,
                    _ => RecoveryActionType.None,
                }, a.Delay));
            }
            return new RecoveryConfig
            {
                ResetPeriodSeconds = fa.dwResetPeriod,
                Command = NullIfEmpty(Marshal.PtrToStringUni(fa.lpCommand)),
                RebootMessage = NullIfEmpty(Marshal.PtrToStringUni(fa.lpRebootMsg)),
                Actions = actions,
            };
        });

    private static List<ServiceTrigger> QueryTriggers(IntPtr h) =>
        QueryConfig2(h, SERVICE_CONFIG_TRIGGER_INFO, p =>
        {
            var info = Marshal.PtrToStructure<SERVICE_TRIGGER_INFO>(p);
            var list = new List<ServiceTrigger>();
            var tSize = Marshal.SizeOf<SERVICE_TRIGGER>();
            var dSize = Marshal.SizeOf<SERVICE_TRIGGER_SPECIFIC_DATA_ITEM>();
            for (var i = 0; i < info.cTriggers; i++)
            {
                var t = Marshal.PtrToStructure<SERVICE_TRIGGER>(info.pTriggers + i * tSize);
                Guid? subtype = t.pTriggerSubtype == IntPtr.Zero ? null : Marshal.PtrToStructure<Guid>(t.pTriggerSubtype);
                var data = new List<string>();
                var raw = new List<TriggerDataRaw>();
                for (var j = 0; j < t.cDataItems; j++)
                {
                    var d = Marshal.PtrToStructure<SERVICE_TRIGGER_SPECIFIC_DATA_ITEM>(t.pDataItems + j * dSize);
                    data.Add(DecodeTriggerData(d));
                    raw.Add(new TriggerDataRaw(d.dwDataType, d.pData == IntPtr.Zero ? "" : Convert.ToBase64String(Bytes(d.pData, d.cbData))));
                }
                list.Add(new ServiceTrigger
                {
                    Kind = TriggerCatalog.MapKind(t.dwTriggerType),
                    Action = t.dwAction switch { 1 => TriggerAction.Start, 2 => TriggerAction.Stop, _ => TriggerAction.Unknown },
                    Subtype = subtype,
                    SubtypeName = subtype is { } g ? TriggerCatalog.NameOf(g) : null,
                    Data = data,
                    RawType = t.dwTriggerType,
                    RawAction = t.dwAction,
                    RawData = raw,
                });
            }
            return list;
        }) ?? [];

    private static byte[] Bytes(IntPtr p, uint count)
    {
        var b = new byte[count];
        Marshal.Copy(p, b, 0, (int)count);
        return b;
    }

    private static string DecodeTriggerData(SERVICE_TRIGGER_SPECIFIC_DATA_ITEM d)
    {
        if (d.pData == IntPtr.Zero || d.cbData == 0) return "";
        switch (d.dwDataType)
        {
            case 2: // STRING (REG_SZ ou REG_MULTI_SZ) — borné à cbData, pas forcément terminé par double NUL
            {
                var raw = Marshal.PtrToStringUni(d.pData, (int)(d.cbData / 2));
                return string.Join("; ", raw.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            }
            case 3: // LEVEL
                return $"level={Marshal.ReadByte(d.pData)}";
            case 4: // KEYWORD_ANY
            case 5: // KEYWORD_ALL
                return $"keyword=0x{Marshal.ReadInt64(d.pData):X16}";
            default: // BINARY
                var bytes = new byte[Math.Min(d.cbData, 64u)];
                Marshal.Copy(d.pData, bytes, 0, bytes.Length);
                return "0x" + Convert.ToHexString(bytes) + (d.cbData > 64 ? "…" : "");
        }
    }

    private static List<string> QueryDependents(IntPtr h)
    {
        var result = new List<string>();
        EnumDependentServicesW(h, SERVICE_STATE_ALL, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0) return result;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumDependentServicesW(h, SERVICE_STATE_ALL, buf, needed, out _, out var count)) return result;
            var size = Marshal.SizeOf<ENUM_SERVICE_STATUS>();
            for (var i = 0; i < count; i++)
            {
                var e = Marshal.PtrToStructure<ENUM_SERVICE_STATUS>(buf + i * size);
                var n = Marshal.PtrToStringUni(e.lpServiceName);
                if (n is not null) result.Add(n);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    /// <summary>DACL de l'objet service (équivalent de « sc sdshow »). Nécessite READ_CONTROL.</summary>
    private static string? QueryServiceSddl(IntPtr h)
    {
        QueryServiceObjectSecurity(h, DACL_SECURITY_INFORMATION, IntPtr.Zero, 0, out var needed);
        if (needed == 0) return null;
        var buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceObjectSecurity(h, DACL_SECURITY_INFORMATION, buf, needed, out _)) return null;
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(buf, 1, DACL_SECURITY_INFORMATION, out var str, out _)) return null;
            try
            {
                return Marshal.PtrToStringUni(str);
            }
            finally
            {
                LocalFree(str);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string? ReadServiceDll(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}\Parameters");
            var v = key?.GetValue("ServiceDll", null, RegistryValueOptions.None) as string;
            return string.IsNullOrWhiteSpace(v) ? null : Environment.ExpandEnvironmentVariables(v);
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadRegistrySddl(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}",
                RegistryKeyPermissionCheck.ReadSubTree, RegistryRights.ReadPermissions);
            return key?.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch
        {
            return null;
        }
    }

    private static readonly Dictionary<string, string?> PublisherCache = new(StringComparer.OrdinalIgnoreCase);

    private static string? ReadPublisher(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        lock (PublisherCache)
        {
            if (PublisherCache.TryGetValue(path, out var cached)) return cached;
        }
        string? company = null;
        try
        {
            if (File.Exists(path))
                company = NullIfEmpty(FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim());
        }
        catch
        {
            // Fichier inaccessible : éditeur inconnu.
        }
        lock (PublisherCache) PublisherCache[path] = company;
        return company;
    }

    internal static ServiceStartMode MapStartMode(uint startType, bool delayed) => startType switch
    {
        0 => ServiceStartMode.Boot,
        1 => ServiceStartMode.System,
        2 => delayed ? ServiceStartMode.AutomaticDelayed : ServiceStartMode.Automatic,
        3 => ServiceStartMode.Manual,
        4 => ServiceStartMode.Disabled,
        _ => ServiceStartMode.Unknown,
    };

    internal static ServiceRunState MapState(uint s) => s switch
    {
        1 => ServiceRunState.Stopped,
        2 => ServiceRunState.StartPending,
        3 => ServiceRunState.StopPending,
        4 => ServiceRunState.Running,
        5 => ServiceRunState.ContinuePending,
        6 => ServiceRunState.PausePending,
        7 => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
