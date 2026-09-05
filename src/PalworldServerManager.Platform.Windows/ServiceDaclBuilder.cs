using System.Security.AccessControl;
using System.Security.Principal;
using PalworldServerManager.Platform.Windows.Native;

namespace PalworldServerManager.Platform.Windows;

/// <summary>
/// Builds the Host service DACL (SS2a).
///
/// The activation group receives EXACTLY SERVICE_START + SERVICE_QUERY_STATUS - never STOP,
/// PAUSE_CONTINUE, CHANGE_CONFIG, DELETE, WRITE_DAC, WRITE_OWNER, READ_CONTROL, or
/// SERVICE_ALL_ACCESS. Existing SYSTEM/Administrators ACEs are preserved so ordinary
/// administrative maintenance keeps working.
///
/// Privileged provisioning and test code may itself request READ_CONTROL to read a descriptor;
/// that right is deliberately NOT delegated to the activation group.
/// </summary>
public static class ServiceDaclBuilder
{
    /// <summary>The exact access mask the activation group is granted, and nothing more.</summary>
    public const int ActivationGroupAccessMask =
        (int)(ServiceControlManagerNative.SERVICE_START | ServiceControlManagerNative.SERVICE_QUERY_STATUS);

    /// <summary>
    /// Rights that must never appear in the activation group's ACE. Used by both production
    /// assertion and tests, so the two can never drift apart.
    /// </summary>
    public static readonly int[] ForbiddenForActivationGroup =
    [
        (int)ServiceControlManagerNative.SERVICE_STOP,
        (int)ServiceControlManagerNative.SERVICE_PAUSE_CONTINUE,
        (int)ServiceControlManagerNative.SERVICE_CHANGE_CONFIG,
        (int)ServiceControlManagerNative.DELETE,
        (int)ServiceControlManagerNative.WRITE_DAC,
        (int)ServiceControlManagerNative.WRITE_OWNER,
        (int)ServiceControlManagerNative.READ_CONTROL,
    ];

    /// <summary>
    /// Returns a new security descriptor: the existing one with a single added ACE granting the
    /// activation group the bounded rights. Every pre-existing ACE is preserved untouched.
    /// </summary>
    public static RawSecurityDescriptor AddActivationGroupAce(RawSecurityDescriptor existing, SecurityIdentifier activationGroupSid)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(activationGroupSid);

        var dacl = existing.DiscretionaryAcl is RawAcl raw
            ? raw
            : new RawAcl(GenericAcl.AclRevision, 1);

        // Remove any stale ACE for this same SID first, so repeated provisioning is idempotent
        // rather than accumulating duplicates.
        for (var i = dacl.Count - 1; i >= 0; i--)
        {
            if (dacl[i] is CommonAce existingAce && existingAce.SecurityIdentifier == activationGroupSid)
            {
                dacl.RemoveAce(i);
            }
        }

        dacl.InsertAce(dacl.Count, new CommonAce(
            AceFlags.None,
            AceQualifier.AccessAllowed,
            ActivationGroupAccessMask,
            activationGroupSid,
            isCallback: false,
            opaque: null));

        return new RawSecurityDescriptor(
            existing.ControlFlags,
            existing.Owner,
            existing.Group,
            existing.SystemAcl,
            dacl);
    }

    /// <summary>
    /// Reads back the activation group's effective mask from a descriptor, for verification.
    /// Returns null when the group has no ACE at all.
    /// </summary>
    public static int? FindActivationGroupMask(RawSecurityDescriptor descriptor, SecurityIdentifier activationGroupSid)
    {
        if (descriptor.DiscretionaryAcl is not RawAcl dacl)
        {
            return null;
        }

        int? mask = null;
        foreach (var ace in dacl)
        {
            if (ace is CommonAce common
                && common.SecurityIdentifier == activationGroupSid
                && common.AceQualifier == AceQualifier.AccessAllowed)
            {
                mask = (mask ?? 0) | common.AccessMask;
            }
        }

        return mask;
    }

    public static byte[] ToBinaryForm(RawSecurityDescriptor descriptor)
    {
        var buffer = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(buffer, 0);
        return buffer;
    }
}
