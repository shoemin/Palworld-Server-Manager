using Google.Protobuf;
using Google.Protobuf.Reflection;
using PalworldServerManager.Contracts;
using Wire = PalworldServerManager.Contracts.Wire;

namespace PalworldServerManager.SelfTest;

public static class ProtocolTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); throw new Exception("Expected rejection: " + typeof(T).Name); } catch (T) { } }
    public static Task Identities()
    {
        var profile = Guid.NewGuid(); var hostA = new HostId(Guid.NewGuid()); var hostB = new HostId(Guid.NewGuid());
        var a = new ServerRef(hostA, profile); var b = new ServerRef(hostB, profile);
        Check(a != b && new HashSet<ServerRef> { a, b }.Count == 2, "Host-qualified domain collision.");
        var wireA = a.ToWire(); var wireB = b.ToWire();
        Check(!wireA.Equals(wireB), "Wire identities collided.");
        Check(ServerRef.FromWire(Wire.ServerRef.Parser.ParseFrom(wireA.ToByteArray())) == a, "Identity serialization failed.");
        Check(!new Wire.ServerRequest { Server = wireA }.Equals(new Wire.ServerRequest { Server = wireB }), "Routing collapsed distinct Hosts.");
        var grantA = Grant(wireA); var grantB = grantA.Clone(); grantB.Server = wireB;
        Check(!grantA.Equals(grantB) && GrantWireValidation.IsValid(grantA) && GrantWireValidation.IsValid(grantB), "Grant target collision/invalid shape.");
        Reject<ArgumentException>(() => ServerRef.FromWire(new Wire.ServerRef { ServerProfileId = profile.ToString("D") }));
        Reject<ArgumentException>(() => new HostId(Guid.Empty));
        Reject<ArgumentException>(() => new ServerRef(hostA, Guid.Empty));
        Reject<ArgumentException>(() => HostId.Parse("server display name"));
        return Task.CompletedTask;
    }
    private static Wire.ServerCapabilityGrant Grant(Wire.ServerRef server) => new()
    {
        GrantId = Guid.NewGuid().ToString("D"), Server = server, Capability = Wire.ServerCapability.ViewServer,
        GranteeActor = new Wire.ActorRef { LocalPrincipalId = Guid.NewGuid().ToString("D") },
        GrantedByActor = new Wire.ActorRef { RemoteHostId = Guid.NewGuid().ToString("D") },
        GrantedUtc = DateTimeOffset.UtcNow.ToString("O"),
    };
    private static Wire.Handshake Offer(uint major, uint minor, string product, params Wire.FeatureCapability[] features)
    {
        var offer = new Wire.Handshake { Protocol = new Wire.ProtocolVersion { Major = major, Minor = minor }, ProductVersion = product };
        offer.Capabilities.AddRange(features); return offer;
    }
    public static Task Negotiation()
    {
        var local = Offer(1, 2, "totally unrelated", Wire.FeatureCapability.ServerInventory, Wire.FeatureCapability.ServerIdentity);
        var remote = Offer(1, 9, "0.0.0", Wire.FeatureCapability.ServerInventory);
        var result = NegotiatedProtocol.Negotiate(local, remote);
        Check(result.Major == 1 && result.Minor == 2 && result.Supports(Wire.FeatureCapability.ServerInventory), "Additive minor compatibility failed.");
        Check(!result.Supports(Wire.FeatureCapability.ServerIdentity), "Unnegotiated feature accepted.");
        Reject<InvalidOperationException>(() => result.Require(Wire.FeatureCapability.ServerIdentity));
        local.Capabilities.Clear(); remote.Capabilities.Add(Wire.FeatureCapability.ServerIdentity);
        Check(result.Supports(Wire.FeatureCapability.ServerInventory) && !result.Supports(Wire.FeatureCapability.ServerIdentity), "Mutable offers changed negotiated state.");
        Reject<ProtocolCompatibilityException>(() => NegotiatedProtocol.Negotiate(Offer(1, 0, "same"), Offer(2, 0, "same")));
        Reject<ArgumentException>(() => NegotiatedProtocol.Negotiate(new Wire.Handshake(), Offer(1, 0, "x")));
        Check(!NegotiatedProtocol.Negotiate(Offer(1, 0, "a", (Wire.FeatureCapability)999), Offer(1, 0, "b", (Wire.FeatureCapability)999)).Supports((Wire.FeatureCapability)999), "Unknown negotiated capability authorized.");
        return Task.CompletedTask;
    }
    public static Task UnknownValues()
    {
        var offer = Offer(1, 0, "display");
        using var stream = new MemoryStream();
        offer.WriteTo(stream);
        using (var writer = new CodedOutputStream(stream, true)) { writer.WriteTag(100, WireFormat.WireType.LengthDelimited); writer.WriteString("future"); writer.Flush(); }
        var data = stream.ToArray(); var parsed = Wire.Handshake.Parser.ParseFrom(data);
        Check(parsed.ToByteArray().SequenceEqual(data), "Unknown optional field was discarded/corrupted.");
        foreach (var value in new[] { -1, 0, 999 })
        {
            Check(!GrantWireValidation.IsKnown((Wire.HostCapability)value) && !GrantWireValidation.IsKnown((Wire.ServerCapability)value), "Unknown authority enum accepted.");
            var grant = Grant(new ServerRef(new HostId(Guid.NewGuid()), Guid.NewGuid()).ToWire()); grant.Capability = (Wire.ServerCapability)value;
            var roundTrip = Wire.ServerCapabilityGrant.Parser.ParseFrom(grant.ToByteArray());
            Check(!GrantWireValidation.IsValid(roundTrip), "Serialized unknown enum became authority.");
        }
        Check(!GrantWireValidation.IsValid(new Wire.HostCapabilityGrant { Capability = Wire.HostCapability.CreateServer }), "Missing Host target accepted.");
        return Task.CompletedTask;
    }
    public static Task SchemaEvolution()
    {
        var baseline = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "host-v1.pb"))).File.Single();
        var current = Wire.HostReflection.Descriptor.ToProto();
        // C# codegen omits inferred json_name fields to shrink its embedded descriptor;
        // protoc descriptor-set output includes them. Restore the reflection-computed names
        // before comparing full snapshots; explicit JSON-name changes remain detectable.
        foreach (var message in Wire.HostReflection.Descriptor.MessageTypes)
            foreach (var field in message.Fields.InDeclarationOrder())
                current.MessageType.Single(m => m.Name == message.Name).Field.Single(f => f.Number == field.FieldNumber).JsonName = field.JsonName;
        AssertAdditive(baseline, current);
        var snapshots = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "host-*.pb")
            .Select(path => FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(path)).File.Single()).ToArray();
        foreach (var snapshot in snapshots) AssertAdditive(snapshot, current);
        Check(snapshots.Any(snapshot => snapshot.Equals(current)), "Schema changes require a new immutable history snapshot; retain all earlier snapshots.");
        var reused = current.Clone(); reused.MessageType.Single(m => m.Name == "ServerRef").Field[0].Name = "other_host";
        Reject<InvalidDataException>(() => AssertAdditive(baseline, reused));
        var removed = current.Clone(); var message = removed.MessageType.Single(m => m.Name == "ServerRef"); message.Field.RemoveAt(0);
        Reject<InvalidDataException>(() => AssertAdditive(baseline, removed));
        message.ReservedRange.Add(new DescriptorProto.Types.ReservedRange { Start = 1, End = 2 }); message.ReservedName.Add("authoritative_host_id");
        Reject<InvalidDataException>(() => AssertAdditive(baseline, removed)); // reservations never authorize a same-major removal
        var changedEnum = current.Clone(); changedEnum.EnumType[0].Value[1].Number = 99;
        Reject<InvalidDataException>(() => AssertAdditive(baseline, changedEnum));
        var changedRpc = current.Clone(); changedRpc.Service[0].Method[1].OutputType = ".wrong.Type";
        Reject<InvalidDataException>(() => AssertAdditive(baseline, changedRpc));
        return Task.CompletedTask;
    }
    private static void AssertAdditive(FileDescriptorProto baseline, FileDescriptorProto current)
    {
        void Require(bool okay) { if (!okay) throw new InvalidDataException("Breaking protocol schema change or unreserved field removal."); }
        foreach (var oldMessage in baseline.MessageType)
        {
            var next = current.MessageType.SingleOrDefault(m => m.Name == oldMessage.Name); Require(next is not null);
            foreach (var field in oldMessage.Field)
            {
                var sameNumber = next!.Field.SingleOrDefault(f => f.Number == field.Number);
                if (sameNumber is null)
                { Require(current.Package != baseline.Package && next.ReservedRange.Any(r => r.Start <= field.Number && r.End > field.Number) && next.ReservedName.Contains(field.Name)); continue; }
                Require(sameNumber.Name == field.Name && sameNumber.Type == field.Type && sameNumber.TypeName == field.TypeName && sameNumber.Label == field.Label && sameNumber.Proto3Optional == field.Proto3Optional && sameNumber.HasOneofIndex == field.HasOneofIndex && sameNumber.OneofIndex == field.OneofIndex);
            }
            foreach (var range in oldMessage.ReservedRange) Require(next!.ReservedRange.Any(r => r.Start <= range.Start && r.End >= range.End));
            foreach (var name in oldMessage.ReservedName) Require(next!.ReservedName.Contains(name));
        }
        foreach (var oldEnum in baseline.EnumType)
        {
            var next = current.EnumType.SingleOrDefault(e => e.Name == oldEnum.Name); Require(next is not null);
            foreach (var value in oldEnum.Value) Require(next!.Value.Any(v => v.Name == value.Name && v.Number == value.Number));
        }
        foreach (var oldService in baseline.Service)
        {
            var next = current.Service.SingleOrDefault(s => s.Name == oldService.Name); Require(next is not null);
            foreach (var method in oldService.Method) Require(next!.Method.Any(m => m.Name == method.Name && m.InputType == method.InputType && m.OutputType == method.OutputType && m.ClientStreaming == method.ClientStreaming && m.ServerStreaming == method.ServerStreaming));
        }
    }
}
