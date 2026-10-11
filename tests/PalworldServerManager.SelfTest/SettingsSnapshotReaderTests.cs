using System.Security.Authentication;
using System.Text;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using PalworldServerManager.Core.Authorization;
using PalworldServerManager.Core.Security;
using PalworldServerManager.Host;
using PalworldServerManager.Host.Persistence;
using PalworldServerManager.Platform.Contracts;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.SelfTest;

internal static class SettingsSnapshotReaderTests
{
    private static void Check(bool ok) { if (!ok) throw new Exception("Settings snapshot assertion failed."); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected settings snapshot refusal: " + typeof(T).Name); }
    private static byte[] Config(string value) => Encoding.UTF8.GetBytes("[/Script/Pal.PalGameWorldSettings]\nOptionSettings=(" + value + ")\n");
    private sealed class Files : IManagedServerSettingsReader
    {
        internal int Calls;
        internal Guid Expected;
        internal string Active = "ServerName=\"SYNTHETIC-NAME\",ServerPassword=\"PRIVATE-ACTIVE\",FutureSecret=\"PRIVATE-UNKNOWN\",DayTimeSpeedRate=2";
        internal string? Defaults = "ServerName=\"SYNTHETIC-DEFAULT\",AdminPassword=\"PRIVATE-DEFAULT\"";
        internal Action? DuringRead;
        internal Exception? Failure;
        internal readonly List<byte[]> Owned = [];
        public ManagedServerSettingsFiles Read(Guid profile, string? path, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Check(profile == Expected && path == "managed-fixture"); Calls++;
            if (Failure is not null) throw Failure;
            var active = Config(Active); var defaults = Defaults is null ? null : Config(Defaults);
            Owned.Add(active); if (defaults is not null) Owned.Add(defaults);
            try { DuringRead?.Invoke(); return new(active, defaults); }
            catch { Array.Clear(active); if (defaults is not null) Array.Clear(defaults); throw; }
        }
    }
    private sealed class Rig : IDisposable
    {
        internal readonly PeerTrustTests.Fixture F = new();
        internal readonly Guid User = Guid.NewGuid();
        internal readonly ServerRef Server;
        internal readonly Files Files = new();
        internal readonly SettingsSnapshotReader Reader;
        internal GrantPolicyRepository Policy => new(F.Database, F.HostId, F.Time);
        internal LocalPrincipalMutationActor Owner => new(F.HostId, F.OwnerId, "native-owner", "fixture-public");
        internal LocalPrincipalMutationActor Local => new(F.HostId, User, "reader-native", "reader-public");
        internal PeerGrantMutationActor Peer => new(F.HostId, F.PeerId, new('B', 64), new('A', 64),
            HostDatabase.QueryScalarLong(F.Writer, $"SELECT Incarnation FROM PeerRelationshipIncarnations WHERE PeerHostId='{F.PeerId:D}';"));
        internal Wire.ServerRef WireServer => new() { AuthoritativeHostId = F.HostId.ToString("D"), ServerProfileId = Server.ServerProfileId.ToString("D") };
        internal Rig()
        {
            try
            {
            Server = new(F.HostId, Guid.NewGuid()); Files.Expected = Server.ServerProfileId;
            F.Execute($"INSERT INTO LocalPrincipals (LocalPrincipalId,OsPrincipalRef,PublicVerificationKey,IsOwner,State,CreatedUtc) VALUES ('{User:D}','reader-native','reader-public',0,'Active','{F.Time.Now:O}');");
            F.Execute($"INSERT INTO TrustedManagers (PeerHostId,State,CurrentTrustedPublicKeyFingerprint,CreatedUtc) VALUES ('{F.PeerId:D}','Active','{new string('B',64)}','{F.Time.Now:O}');");
            F.Execute($"INSERT INTO ServerInventory (ServerProfileId,AuthoritativeHostId,DisplayName,InstallPath,CreatedUtc) VALUES ('{Server.ServerProfileId:D}','{F.HostId:D}','fixture','managed-fixture','{F.Time.Now:O}');");
            Reader = new(F.Database, F.HostId, Files);
            }
            catch { F.Dispose(); throw; }
        }
        internal Guid Grant(bool peer, ServerCapability capability = ServerCapability.ViewServer)
        {
            var id = Guid.NewGuid(); Policy.IssueServer(Owner, Policy.Read().Revision, id,
                peer ? ActorRef.RemoteManager(F.PeerId) : ActorRef.LocalPrincipal(User), capability, Server, new(false, false), null); return id;
        }
        internal Wire.SettingsSnapshot Read(bool peer = false, Action<SqliteConnection, SqliteTransaction, ServerRef>? validate = null,
            SettingsSnapshotReader? reader = null, CancellationToken ct = default)
        {
            var proof = peer ? Peer : null;
            return (reader ?? Reader).Read(WireServer,
                s => { if (peer) Policy.RequireRemoteServerCapability(proof!, ServerCapability.ViewServer, s, ct); else Policy.RequireLocalServerCapability(Local, ServerCapability.ViewServer, s, ct); },
                validate ?? ((c, tx, s) => { if (peer) Policy.ValidateRemoteServerCapability(c, tx, proof!, ServerCapability.ViewServer, s, ct); else Policy.ValidateLocalServerCapability(c, tx, Local, ServerCapability.ViewServer, s, ct); }), ct);
        }
        internal void Unobserved() => Check(F.Count("ConfigurationRevisions") == 0 && F.Count("ConfigurationContentObservations") == 0);
        public void Dispose() => F.Dispose();
    }
    internal static Task ViewOnlyPrivacyAndDurableRevisions()
    {
        foreach (var peer in new[] { false, true })
        {
            using var r = new Rig(); r.Grant(peer); var grants = r.F.Count("ServerCapabilityGrants");
            var result = r.Read(peer); Check(result.Revision == "1" && result.Server.Equals(r.WireServer));
            Check(result.Entries.Single(e => e.Definition.Key == "ServerName").Configured.Value == "SYNTHETIC-NAME");
            Check(result.Entries.Single(e => e.Definition.Key == "ServerName").InstallationDefault.Value == "SYNTHETIC-DEFAULT");
            Check(result.Entries.All(e => e.Effective is null));
            foreach (var key in new[] { "ServerPassword", "AdminPassword", "FutureSecret" })
            {
                var entry = result.Entries.Single(e => e.Definition.Key == key);
                Check(entry.Configured is null && entry.InstallationDefault is null && !entry.Definition.HasCatalogDefault);
            }
            Check(!Encoding.UTF8.GetString(result.ToByteArray()).Contains("PRIVATE", StringComparison.Ordinal));
            Check(r.Files.Owned.All(a => a.All(b => b == 0)) && r.Read(peer).Revision == "1");
            var first = r.Files.Active; r.Files.Active = "ServerName=\"CHANGED\""; Check(r.Read(peer).Revision == "2");
            r.Files.Active = first; Check(r.Read(peer).Revision == "3");
            Check(r.Read(peer, reader: new(r.F.Database, r.F.HostId, r.Files)).Revision == "3");
            r.Files.Defaults = null; var absent = r.Read(peer); Check(absent.Revision == "3" && absent.Entries.All(e => e.InstallationDefault is null));
            Check(r.F.Count("ServerCapabilityGrants") == grants && r.F.Count("ConfigurationContentObservations") == 1);
            r.Files.Active = "DayTimeSpeedRate=NaN";
            var invalid = r.Read(peer).Entries.Single();
            Check(invalid.Configured.Value == "NaN" && invalid.Configured.Validation == Wire.SettingsValidationCode.FiniteNumberRequired);
        }
        return Task.CompletedTask;
    }
    internal static Task ScopeAndNoReadWithoutView()
    {
        foreach (var peer in new[] { false, true }) foreach (var edit in new[] { false, true })
        {
            using var r = new Rig(); if (edit) r.Grant(peer, ServerCapability.EditSettings);
            Reject<UnauthorizedAccessException>(() => r.Read(peer)); Check(r.Files.Calls == 0); r.Unobserved();
        }
        using (var r = new Rig())
        {
            r.Grant(false);
            foreach (var requested in new Wire.ServerRef?[] { null, new(), new() { AuthoritativeHostId = Guid.NewGuid().ToString("D"), ServerProfileId = r.Server.ServerProfileId.ToString("D") } })
                Reject<ArgumentException>(() => r.Reader.Read(requested, _ => throw new Exception("Wrong scope reached authority."), (_, _, _) => { }));
            r.F.Execute("DELETE FROM ServerInventory;"); Reject<InvalidOperationException>(() => r.Read()); Check(r.Files.Calls == 0); r.Unobserved();
        }
        return Task.CompletedTask;
    }
    internal static Task ChangedProofOrRegistrationCannotCommit()
    {
        foreach (var peer in new[] { false, true })
        {
            using var r = new Rig(); r.Grant(peer);
            r.Files.DuringRead = () => r.F.Execute(peer ? "UPDATE TrustedManagers SET PeerRecoveryRequired=1;" : "UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=0;");
            Reject<AuthenticationException>(() => r.Read(peer)); r.Unobserved(); Check(r.Files.Owned.All(a => a.All(b => b == 0)));
        }
        foreach (var change in new[] { "InstallPath='rebound'", "DisplayName='changed'", "GamePort=7777", "RestApiPort=8888", "ImportProvenance='changed'", "CreatedUtc='2026-10-01T00:00:00.0000000+00:00'" })
        {
            using var r = new Rig(); r.Grant(false); r.Files.DuringRead = () => r.F.Execute("UPDATE ServerInventory SET " + change + ";");
            Reject<InvalidOperationException>(() => r.Read()); r.Unobserved(); Check(r.Files.Owned.All(a => a.All(b => b == 0)));
        }
        using (var r = new Rig())
        {
            var grant = r.Grant(false); r.Files.DuringRead = () => r.Policy.InvalidateServerSubtree(r.Owner, r.Policy.Read().Revision, grant);
            Reject<UnauthorizedAccessException>(() => r.Read()); r.Unobserved();
        }
        return Task.CompletedTask;
    }
    internal static Task PendingChangesRollBackOnFinalValidation()
    {
        foreach (var change in new[] { "UPDATE LocalPrincipals SET PublicVerificationKey='changed' WHERE IsOwner=0;", "UPDATE ServerInventory SET InstallPath='rebound';", "DELETE FROM ServerInventory;", "UPDATE ServerCapabilityGrants SET InvalidatedUtc='2026-10-01T00:00:00.0000000+00:00';" })
        {
            using var r = new Rig(); r.Grant(false);
            var auditCount = r.F.Count("AuditEvents");
            r.F.Execute("CREATE TRIGGER SettingsFault AFTER INSERT ON ConfigurationContentObservations BEGIN " + change + " END;");
            try { r.Read(); throw new Exception("Pending mutation escaped final validation."); }
            catch (Exception ex) when (ex is AuthenticationException or UnauthorizedAccessException or InvalidOperationException) { }
            r.Unobserved(); Check(r.Files.Owned.All(a => a.All(b => b == 0)) && r.F.Count("AuditEvents") == auditCount);
            r.F.Execute("DROP TRIGGER SettingsFault;"); Check(r.Read().Revision == "1");
        }
        using (var r = new Rig())
        {
            r.Grant(true); r.F.Execute("CREATE TRIGGER SettingsFault AFTER INSERT ON ConfigurationContentObservations BEGIN UPDATE TrustedManagers SET PeerRecoveryRequired=1; END;");
            Reject<AuthenticationException>(() => r.Read(true)); r.Unobserved();
            Check(HostDatabase.QueryScalarLong(r.F.Writer, "SELECT PeerRecoveryRequired FROM TrustedManagers;") == 0);
        }
        using (var r = new Rig())
        {
            r.Grant(false); using var cancel = new CancellationTokenSource(); var checks = 0;
            Reject<OperationCanceledException>(() => r.Read(validate: (c, tx, s) =>
            {
                r.Policy.ValidateLocalServerCapability(c, tx, r.Local, ServerCapability.ViewServer, s);
                if (++checks == 2) cancel.Cancel();
            }, ct: cancel.Token));
            r.Unobserved(); Check(checks == 2 && r.Files.Owned.All(a => a.All(b => b == 0)));
        }
        return Task.CompletedTask;
    }
    internal static Task AmbiguityParseBoundsAndFailuresPreserveMetadata()
    {
        foreach (var text in new[] { "ServerName=\"A\",servername=\"B\"", "ServerName=\"unterminated", string.Join(',', Enumerable.Range(0, 1025).Select(i => "Future" + i + "=1")) })
        {
            using var r = new Rig(); r.Grant(false); r.Files.Active = text;
            try { r.Read(); throw new Exception("Invalid source returned."); } catch (Exception ex) when (ex is InvalidDataException or FormatException) { }
            r.Unobserved(); Check(r.Files.Owned.All(a => a.All(b => b == 0)));
        }
        using (var r = new Rig())
        {
            r.Grant(false); r.Files.Active = "ServerPassword=\"PRIVATE1\",serverpassword=\"PRIVATE2\",FutureSecret=1,futuresecret=2";
            var result = r.Read(); Check(result.Entries.Count == 4 && result.Entries.All(e => e.Definition.Key == "ServerName" || e.Configured is null));
            r.Files.Defaults = "ServerName=1,ServerName=2";
            Reject<InvalidDataException>(() => r.Read()); Check(r.F.Count("ConfigurationRevisions") == 1);
        }
        using (var r = new Rig())
        {
            r.Grant(false); r.Files.Active = "ServerName=\"" + new string('x', 600000) + "\""; r.Files.Defaults = r.Files.Active;
            Reject<InvalidDataException>(() => r.Read()); r.Unobserved(); Check(r.Files.Owned.All(a => a.All(b => b == 0)));
        }
        foreach (var failure in Enum.GetValues<FileReadFailure>())
        {
            using var r = new Rig(); r.Grant(false); r.Files.Failure = new BoundedFileReadException(failure);
            Reject<BoundedFileReadException>(() => r.Read()); r.Unobserved();
        }
        using (var r = new Rig())
        {
            r.Grant(false); var calls = 0;
            Reject<UnauthorizedAccessException>(() => r.Reader.Read(r.WireServer, s =>
            {
                r.Policy.RequireLocalServerCapability(r.Local, ServerCapability.ViewServer, s);
                if (++calls == 3) throw new UnauthorizedAccessException();
            }, (c, tx, s) => r.Policy.ValidateLocalServerCapability(c, tx, r.Local, ServerCapability.ViewServer, s)));
            Check(r.F.Count("ConfigurationRevisions") == 1 && r.Files.Owned.All(a => a.All(b => b == 0))); // metadata committed, response refused
        }
        return Task.CompletedTask;
    }
    internal static async Task OrderedReadsAndQueuedCancellation()
    {
        using var r = new Rig(); r.Grant(false);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var queued = new ManualResetEventSlim(); using var cancel = new CancellationTokenSource();
        r.Files.DuringRead = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); };
        var first = Task.Run(() => r.Read());
        Task<Wire.SettingsSnapshot>? second = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => r.Reader.Read(r.WireServer, s =>
            {
                r.Policy.RequireLocalServerCapability(r.Local, ServerCapability.ViewServer, s, cancel.Token); queued.Set();
            }, (c, tx, s) => r.Policy.ValidateLocalServerCapability(c, tx, r.Local, ServerCapability.ViewServer, s, cancel.Token), cancel.Token));
            Check(queued.Wait(TimeSpan.FromSeconds(10))); cancel.Cancel();
            try { await second; throw new Exception("Queued cancellation succeeded."); } catch (OperationCanceledException) { }
            Check(r.Files.Calls == 1);
        }
        finally
        {
            release.Set();
            // Drain even when an assertion fails, before disposing the database/gates.
            try { await first; } catch { }
            if (second is not null) { try { await second; } catch { } }
        }
        Check((await first).Revision == "1"); r.Files.DuringRead = null;
        var many = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => r.Read())));
        Check(many.All(x => x.Revision == "1") && r.Files.Owned.All(a => a.All(b => b == 0)));
        using var canceled = new CancellationTokenSource(); r.Files.DuringRead = () => canceled.Cancel();
        Reject<OperationCanceledException>(() => r.Read(ct: canceled.Token)); Check(r.Files.Owned.All(a => a.All(b => b == 0)));
    }
    internal static Task CurrentReadValidatorsAreReadOnlyAndScoped()
    {
        using var r = new Rig(); using var other = new Rig(); r.Grant(false); r.Grant(true);
        var audits = r.F.Count("AuditEvents"); var revision = r.Policy.Read().Revision; var peer = r.Peer;
        var registered = new ServerInventoryRepository(r.F.Database, r.F.HostId).Read(r.Server)!;
        using (var tx = r.F.Writer.BeginTransaction(deferred: false))
        {
            void Local(ServerCapability cap = ServerCapability.ViewServer) => r.Policy.ValidateLocalServerCapability(r.F.Writer, tx, r.Local, cap, r.Server);
            void Remote(PeerGrantMutationActor? proof = null, ServerRef? target = null) => r.Policy.ValidateRemoteServerCapability(r.F.Writer, tx, proof ?? peer, ServerCapability.ViewServer, target ?? r.Server);
            Local(); Remote();
            using (var call = new PeerPermissionCall(r.Policy, peer, default)) call.ValidateServer(r.F.Writer, tx, ServerCapability.ViewServer, r.Server);
            Reject<UnauthorizedAccessException>(() => Local(ServerCapability.EditSettings));
            Reject<ArgumentException>(() => Local((ServerCapability)int.MaxValue));
            Reject<UnauthorizedAccessException>(() => Remote(target: new(r.F.PeerId, r.Server.ServerProfileId)));
            Reject<AuthenticationException>(() => Remote(peer with { PeerFingerprint = new('C', 64) }));
            Reject<AuthenticationException>(() => Remote(peer with { LocalFingerprint = new('C', 64) }));
            Reject<AuthenticationException>(() => Remote(peer with { Incarnation = peer.Incarnation + 1 }));
            HostDatabase.Execute(r.F.Writer, $"UPDATE TrustedManagers SET PendingTrustedPublicKeyFingerprint='{new string('C',64)}',PendingRotationId='{Guid.NewGuid():D}',PendingRotationExpiresUtc='{r.F.Time.Now.AddHours(1):O}';", tx);
            Remote(peer with { PeerFingerprint = new('C', 64) });
            HostDatabase.Execute(r.F.Writer, "UPDATE TrustedManagers SET State='PeerBound';", tx);
            Reject<AuthenticationException>(() => Remote());
            // Facades retain their synchronous callback/cancellation/disposal guards in this path too.
            using (var call = new LocalPermissionCall(r.Policy, r.Local, default)) call.ValidateServer(r.F.Writer, tx, ServerCapability.ViewServer, r.Server);
            using var disposed = new LocalPermissionCall(r.Policy, r.Local, default); disposed.Dispose();
            Reject<ObjectDisposedException>(() => disposed.ValidateServer(r.F.Writer, tx, ServerCapability.ViewServer, r.Server));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            using var canceledCall = new PeerPermissionCall(r.Policy, peer, canceled.Token);
            Reject<OperationCanceledException>(() => canceledCall.ValidateServer(r.F.Writer, tx, ServerCapability.ViewServer, r.Server));
            using var validCall = new LocalPermissionCall(r.Policy, r.Local, default);
            Exception? threadFailure = null;
            var thread = new Thread(() =>
            {
                try { Reject<InvalidOperationException>(() => validCall.ValidateServer(r.F.Writer, tx, ServerCapability.ViewServer, r.Server)); }
                catch (Exception ex) { threadFailure = ex; }
            }) { IsBackground = true };
            thread.Start(); Check(thread.Join(TimeSpan.FromSeconds(10)) && threadFailure is null);
            Check(ReferenceEquals(tx.Connection, r.F.Writer)); tx.Rollback();
        }
        Check(r.F.Count("AuditEvents") == audits && r.Policy.Read().Revision == revision);
        using (var tx = other.F.Writer.BeginTransaction(deferred: false))
        {
            Reject<ArgumentException>(() => r.Policy.ValidateLocalServerCapability(other.F.Writer, tx, r.Local, ServerCapability.ViewServer, r.Server));
            Reject<ArgumentException>(() => r.Policy.ValidateLocalServerCapability(r.F.Writer, tx, r.Local, ServerCapability.ViewServer, r.Server));
            Reject<ArgumentException>(() => new ServerInventoryRepository(r.F.Database, r.F.HostId).ValidateUnchanged(other.F.Writer, tx, registered));
        }
        r.Unobserved(); return Task.CompletedTask;
    }
    internal static async Task QueuedRevocationRefusesBeforeFileAccess()
    {
        using var r = new Rig(); var grant = r.Grant(false);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var queued = new ManualResetEventSlim();
        r.Files.DuringRead = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException(); };
        // An Owner read holds the observation order while the independently authorized user's read queues.
        var first = Task.Run(() => r.Reader.Read(r.WireServer,
            s => r.Policy.RequireLocalServerCapability(r.Owner, ServerCapability.ViewServer, s),
            (c, tx, s) => r.Policy.ValidateLocalServerCapability(c, tx, r.Owner, ServerCapability.ViewServer, s)));
        Task<Wire.SettingsSnapshot>? second = null;
        try
        {
            Check(entered.Wait(TimeSpan.FromSeconds(10)));
            second = Task.Run(() => r.Reader.Read(r.WireServer, s =>
            {
                r.Policy.RequireLocalServerCapability(r.Local, ServerCapability.ViewServer, s); queued.Set();
            }, (c, tx, s) => r.Policy.ValidateLocalServerCapability(c, tx, r.Local, ServerCapability.ViewServer, s)));
            Check(queued.Wait(TimeSpan.FromSeconds(10)));
            r.Policy.InvalidateServerSubtree(r.Owner, r.Policy.Read().Revision, grant);
        }
        finally
        {
            release.Set();
            try { await first; } catch { }
            if (second is not null) { try { await second; } catch { } }
        }
        Check((await first).Revision == "1");
        Check(second is not null);
        try { await second!; throw new Exception("Queued revoked read succeeded."); } catch (UnauthorizedAccessException) { }
        Check(r.Files.Calls == 1 && r.Files.Owned.All(a => a.All(b => b == 0)));
    }
}
