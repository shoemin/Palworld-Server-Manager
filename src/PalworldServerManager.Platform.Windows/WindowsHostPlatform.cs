using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.Platform.Windows;

public sealed class WindowsHostPlatform : IHostServiceLifecycle, IBootStartPlatform, IHostDataRootPlatform
{
    public const string ProductServiceName = "PalworldServerManagerHost";
    public const string ProductActivationGroup = "PalworldServerManagerUsers";
    public const int ActivationAccessMask = 0x14; // SERVICE_START | SERVICE_QUERY_STATUS only
    private readonly string _service;
    private readonly string _group;
    private readonly string _root;
    public bool ActivationGroupCreated { get; private set; }
    public WindowsHostPlatform() : this(ProductServiceName, ProductActivationGroup,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PalworldServerManager", "Host")) { }
    // Explicit names/root enable an isolated, uniquely named integration service. Never RPC input.
    public WindowsHostPlatform(string serviceName, string activationGroup, string hostDataRoot)
    {
        if (string.IsNullOrWhiteSpace(serviceName) || serviceName.Length > 80 || serviceName.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid service name.");
        if (string.IsNullOrWhiteSpace(activationGroup) || activationGroup.Length > 80 || activationGroup.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new ArgumentException("Invalid activation group name.");
        if (!Path.IsPathFullyQualified(hostDataRoot)) throw new ArgumentException("Absolute Host root required.");
        _service = serviceName; _group = activationGroup; _root = Path.GetFullPath(hostDataRoot);
    }
    public string GetHostDataRoot() => _root;
    public static void RequireOfflineElevation(CancellationToken ct = default) => Privileged(ct);
    // Read-only validation before an offline caller opens or creates any authoritative file.
    public void ValidateOfflineDataRoot(SecurityIdentifier serviceSid, CancellationToken ct = default)
    {
        Privileged(ct);
        ValidateProtectedDataRoot(serviceSid, ct);
    }
    // Read-only service startup validation. This does not grant elevation or repair any ACL.
    public void ValidateProtectedDataRoot(SecurityIdentifier serviceSid, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = new DirectoryInfo(_root);
        new PublicTrustFileSecurity(serviceSid).Ancestors(root.Parent);
        void Validate(FileSystemInfo item)
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Offline Host state traverses a reparse point.");
            FileSystemSecurity acl = item is DirectoryInfo directory ? directory.GetAccessControl() : new FileInfo(item.FullName).GetAccessControl();
            ValidateExistingStateAcl(acl, serviceSid);
            var raw = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
            if (raw.DiscretionaryAcl is null) throw new UnauthorizedAccessException("Offline Host state requires an explicit DACL.");
            foreach (GenericAce ace in raw.DiscretionaryAcl)
                if (ace is not CommonAce common || common.IsCallback || common.AceQualifier != AceQualifier.AccessAllowed ||
                    (common.SecurityIdentifier != serviceSid && !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) &&
                     !common.SecurityIdentifier.IsWellKnown(WellKnownSidType.LocalSystemSid)))
                    throw new UnauthorizedAccessException("Offline Host state grants access outside the privileged boundary.");
            if (item is DirectoryInfo container) foreach (var child in container.EnumerateFileSystemInfos()) Validate(child);
        }
        if (!root.GetAccessControl().AreAccessRulesProtected) throw new UnauthorizedAccessException("Offline Host root must be protected from inheritance.");
        Validate(root);
    }
    public static string QuoteExecutable(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.StartsWith(@"\\") || executablePath.Contains('"') || executablePath.Contains('\0'))
            throw new ArgumentException("An absolute local executable path is required.");
        return "\"" + executablePath + "\"";
    }
    public static string BuildServiceDacl(SecurityIdentifier activationGroup)
        => $"D:P(A;;0xF01FF;;;SY)(A;;0xF01FF;;;BA)(A;;0x14;;;{activationGroup.Value})";
    public static DirectorySecurity BuildHostDirectoryAcl(SecurityIdentifier serviceSid)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { serviceSid, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
        return acl;
    }
    private static void Privileged(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Elevated Administrator provisioning is required.");
    }
    public Task InstallAsync(string executablePath, CancellationToken ct = default)
        => InstallForServiceAsync(executablePath, [], ct);

    // Separate executable/arguments, for an integration service workload as well as future
    // explicitly authorized composition. Never parsed as a shell command.
    public Task InstallForServiceAsync(string executablePath, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        Privileged(ct);
        var binaryPath = QuoteExecutable(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Host executable must already exist.", executablePath);
        if (arguments.Count > 0) binaryPath += " " + string.Join(" ", arguments.Select(QuoteArgument));
        using var manager = Native.OpenManager(0x3);
        // Never adopt/overwrite an existing service registration under a matching name.
        using var existing = Native.OpenService(manager, _service, 0x4);
        if (!existing.IsInvalid) throw new InvalidOperationException("Service already exists; explicit maintenance is required.");
        var error = Marshal.GetLastWin32Error();
        if (error != 1060) throw new Win32Exception(error);

        var info = new Native.LocalGroupInfo { Name = _group, Comment = "Palworld Server Manager query/start eligibility only; membership managed by Administrators." };
        var result = Native.NetLocalGroupAdd(null, 1, ref info, out _);
        if (result is not 0 and not 2223) throw new Win32Exception((int)result);
        ActivationGroupCreated = result == 0;
        // Query the local SAM directly as well: no accidental domain-group resolution.
        result = Native.NetLocalGroupGetInfo(null, _group, 0, out var groupInfo);
        if (result != 0) throw new Win32Exception((int)result);
        Native.NetApiBufferFree(groupInfo);
        var groupSid = (SecurityIdentifier)new NTAccount(Environment.MachineName, _group).Translate(typeof(SecurityIdentifier));
        using var service = Native.CreateService(manager, _service, _service, 0xF01FF, 0x10, 3, 1,
            binaryPath, null, IntPtr.Zero, null, @"NT SERVICE\" + _service, null);
        Native.CheckHandle(service);
        try
        {
            var sidInfo = 1; // SERVICE_SID_TYPE_UNRESTRICTED; dedicated virtual service identity
            Native.Check(Native.ChangeServiceConfig2(service, 5, ref sidInfo));
            // Make the data boundary safe before delegating SERVICE_START. Otherwise an
            // existing group member can race provisioning and start against an unsafe root.
            var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", _service).Translate(typeof(SecurityIdentifier));
            ProtectHostRoot(serviceSid);
            var descriptor = new RawSecurityDescriptor(BuildServiceDacl(groupSid));
            var bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
            Native.Check(Native.SetServiceObjectSecurity(service, 4, bytes));
        }
        catch
        {
            // Preserve state and group. Failure to unregister is surfaced, never hidden as a
            // successful cleanup. No new service has started at this point.
            Native.Check(Native.DeleteService(service));
            throw;
        }
        return Task.CompletedTask;
    }
    private void ProtectHostRoot(SecurityIdentifier serviceSid)
    {
        // Never follow a pre-existing reparse path while setting privileged filesystem ACLs.
        for (var cursor = new DirectoryInfo(_root); cursor is not null; cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Host root cannot traverse a reparse point.");
        var directory = new DirectoryInfo(_root);
        if (directory.Exists)
        {
            // Existing state may originate in #40. Do not silently recurse through or take
            // ownership of arbitrary pre-existing data. Root must already be privileged-owned.
            var owner = directory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            if (owner is not SecurityIdentifier sid ||
                (!sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && !sid.IsWellKnown(WellKnownSidType.LocalSystemSid) && !sid.Equals(serviceSid)))
                throw new UnauthorizedAccessException("Existing Host root is not owned by the service, SYSTEM, or Administrators.");
            // Replacing the root DACL does not remove explicit grants on an existing SQLite
            // file or protected child directory. Reject those layouts instead of silently
            // leaving a direct ordinary-user persistence path below an apparently safe root.
            ValidateExistingChildren(directory, serviceSid);
        }
        else directory.Create(BuildHostDirectoryAcl(serviceSid));
        directory.SetAccessControl(BuildHostDirectoryAcl(serviceSid));
    }
    private static void ValidateExistingChildren(DirectoryInfo directory, SecurityIdentifier serviceSid)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Existing Host state contains a reparse point.");
            FileSystemSecurity acl = entry is DirectoryInfo child
                ? child.GetAccessControl() : new FileInfo(entry.FullName).GetAccessControl();
            ValidateExistingStateAcl(acl, serviceSid);
            if (entry is DirectoryInfo subdirectory) ValidateExistingChildren(subdirectory, serviceSid);
        }
    }
    public static void ValidateExistingStateAcl(FileSystemSecurity acl, SecurityIdentifier serviceSid)
    {
        static bool PrivilegedSid(SecurityIdentifier sid, SecurityIdentifier service) => sid.Equals(service) ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        // Ownership is itself a DACL-changing authority even when no write ACE is present.
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !PrivilegedSid(owner, serviceSid))
            throw new UnauthorizedAccessException("Existing Host state has an unprivileged owner.");
        var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        // Do not guess effective group membership when deny ACEs are present. Require explicit
        // privileged repair of that layout rather than claiming a successful service install.
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Deny))
            throw new UnauthorizedAccessException("Existing Host state contains deny permissions requiring explicit repair.");
        foreach (var rule in rules.Where(rule => !rule.IsInherited || acl.AreAccessRulesProtected))
            if (!PrivilegedSid((SecurityIdentifier)rule.IdentityReference, serviceSid))
                throw new UnauthorizedAccessException("Existing Host state has access outside the service/Administrator boundary.");
        // Protected descendants retain their ACL when the root is secured. They must already
        // grant the virtual service account usable rights on this object, not just its children.
        if (acl.AreAccessRulesProtected && !rules.Any(rule => rule.IdentityReference.Equals(serviceSid) &&
            (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
            (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl))
            throw new UnauthorizedAccessException("Protected Host state does not grant the service full access.");
    }
    private static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL in argument.");
        var result = new System.Text.StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    public Task UninstallAsync(CancellationToken ct = default)
    {
        Privileged(ct);
        using var manager = Native.OpenManager(1);
        using var service = Native.OpenService(manager, _service, 0x10000 | 0x4);
        Native.CheckHandle(service);
        using var controller = new ServiceController(_service, ".");
        if (controller.Status != ServiceControllerStatus.Stopped) throw new InvalidOperationException("Stop the Host before removing its registration.");
        Native.Check(Native.DeleteService(service));
        // Group retained for Administrator/installer cleanup; authoritative state untouched.
        return Task.CompletedTask;
    }
    public Task StartAsync(CancellationToken ct = default)
    {
        Privileged(ct); using var service = new ServiceController(_service, ".");
        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken ct = default)
    {
        Privileged(ct); using var service = new ServiceController(_service, ".");
        if (service.Status == ServiceControllerStatus.Stopped) return Task.CompletedTask;
        service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }
    public Task<HostServiceState> GetStateAsync(CancellationToken ct = default)
    {
        Privileged(ct); using var service = new ServiceController(_service, ".");
        return Task.FromResult(service.Status switch {
            ServiceControllerStatus.Stopped => HostServiceState.Stopped,
            ServiceControllerStatus.Running => HostServiceState.Running,
            ServiceControllerStatus.StartPending => HostServiceState.StartPending,
            ServiceControllerStatus.StopPending => HostServiceState.StopPending,
            _ => HostServiceState.Other });
    }
    public Task<bool> IsEnabledAsync(CancellationToken ct = default)
    { Privileged(ct); using var service = new ServiceController(_service, "."); return Task.FromResult(service.StartType == ServiceStartMode.Automatic); }
    public Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        Privileged(ct); using var manager = Native.OpenManager(1); using var service = Native.OpenService(manager, _service, 2);
        Native.CheckHandle(service);
        Native.Check(Native.ChangeServiceConfig(service, uint.MaxValue, enabled ? 2u : 3u, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null));
        return Task.CompletedTask;
    }

    public byte[] ReadServiceSecurityDescriptor()
    {
        Privileged(default); using var manager = Native.OpenManager(1); using var service = Native.OpenService(manager, _service, 0x20000);
        Native.CheckHandle(service);
        Native.QueryServiceObjectSecurity(service, 4, null, 0, out var needed);
        if (Marshal.GetLastWin32Error() != 122) throw new Win32Exception(Marshal.GetLastWin32Error());
        var data = new byte[needed]; Native.Check(Native.QueryServiceObjectSecurity(service, 4, data, needed, out _)); return data;
    }

    private static class Native
    {
        internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public ServiceHandle() : base(true) { }
            protected override bool ReleaseHandle() => CloseServiceHandle(handle);
        }
        internal static void Check(bool result) { if (!result) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal static void CheckHandle(ServiceHandle handle) { if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal static ServiceHandle OpenManager(uint access) { var handle = OpenSCManager(null, null, access); CheckHandle(handle); return handle; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ServiceHandle CreateService(ServiceHandle manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, IntPtr tag, string? dependencies, string account, string? password);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ChangeServiceConfig(ServiceHandle service, uint type, uint start, uint error, string? binary, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ChangeServiceConfig2(ServiceHandle service, uint level, ref int info);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool QueryServiceObjectSecurity(ServiceHandle service, uint info, byte[]? descriptor, uint size, out uint needed);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool SetServiceObjectSecurity(ServiceHandle service, uint info, byte[] descriptor);
        [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool DeleteService(ServiceHandle service);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr service);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct LocalGroupInfo { public string Name; public string Comment; }
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] internal static extern uint NetLocalGroupAdd(string? server, uint level, ref LocalGroupInfo info, out uint parameterError);
        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)] internal static extern uint NetLocalGroupGetInfo(string? server, string group, uint level, out IntPtr buffer);
        [DllImport("netapi32.dll")] internal static extern uint NetApiBufferFree(IntPtr buffer);
    }
}
