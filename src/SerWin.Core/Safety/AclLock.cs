using System.Security.AccessControl;
using System.Security.Principal;

namespace SerWin.Core.Safety;

/// <summary>
/// Verrouillage ACL de la clé d'un service (spec §5.2, mode Expert) : une règle « Refuser — Définir la valeur » pour
/// Tout le monde, sur cette clé seulement (pas d'héritage), empêche quiconque — y compris Windows et ses réparateurs —
/// de réécrire la valeur Start. Le reste de la clé (Parameters, TriggerInfo…) n'est pas affecté.
/// </summary>
public static class AclLock
{
    private static readonly SecurityIdentifier Everyone = new(WellKnownSidType.WorldSid, null);

    private static RegistryAccessRule LockRule() =>
        new(Everyone, RegistryRights.SetValue, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);

    private static RegistrySecurity Parse(string sddl)
    {
        var rs = new RegistrySecurity();
        rs.SetSecurityDescriptorSddlForm(sddl, AccessControlSections.Access);
        return rs;
    }

    private static string Format(RegistrySecurity rs) => rs.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    public static bool IsLocked(string? sddl)
    {
        if (string.IsNullOrEmpty(sddl)) return false;
        try
        {
            return Parse(sddl).GetAccessRules(true, false, typeof(SecurityIdentifier)).OfType<RegistryAccessRule>()
                .Any(r => r.AccessControlType == AccessControlType.Deny
                          && r.IdentityReference.Equals(Everyone)
                          && (r.RegistryRights & RegistryRights.SetValue) != 0);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Descripteur (section Access) avec la règle de verrouillage ajoutée.</summary>
    public static string Lock(string sddl)
    {
        var rs = Parse(sddl);
        rs.AddAccessRule(LockRule());
        return Format(rs);
    }

    /// <summary>Descripteur (section Access) sans la règle de verrouillage.</summary>
    public static string Unlock(string sddl)
    {
        var rs = Parse(sddl);
        rs.RemoveAccessRuleSpecific(LockRule());
        return Format(rs);
    }

    /// <summary>Section Access seule, sous forme canonique (pour comparer deux descripteurs).</summary>
    public static string Normalize(string sddl) => Format(Parse(sddl));
}
