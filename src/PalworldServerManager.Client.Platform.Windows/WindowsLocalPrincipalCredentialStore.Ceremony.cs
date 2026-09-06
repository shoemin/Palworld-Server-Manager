using System.Security.Cryptography;
using PalworldServerManager.Client.Platform.Contracts;

namespace PalworldServerManager.Client.Platform.Windows;

public sealed partial class WindowsLocalPrincipalCredentialStore
{
    private sealed class PendingCredential
    {
        public ClientCredentialCeremony Ceremony { get; set; } = null!;
        public byte[] PublicKey { get; set; } = [];
        public byte[] PrivateKey { get; set; } = [];
    }
    private sealed class CompletedCredential
    {
        public ClientCredentialCeremony Ceremony { get; set; } = null!;
        public Guid PrincipalId { get; set; }
        public byte[] PublicKey { get; set; } = [];
    }
    private static void Validate(ClientCredentialCeremony ceremony)
    {
        ArgumentNullException.ThrowIfNull(ceremony);
        if (ceremony.HostId == Guid.Empty || ceremony.TicketId == Guid.Empty || !Enum.IsDefined(ceremony.Purpose) || !Enum.IsDefined(ceremony.KeyUse) ||
            (ceremony.KeyUse == ClientCredentialKeyUse.ExistingForRehome && ceremony.Purpose != ClientCredentialPurpose.OwnerRehome))
            throw new ArgumentException("Invalid client ceremony context.");
    }
    private static void ValidatePayload(Payload value)
    {
        if (value.Version is not 1 and not 2 || value.PublicKey is null || value.PrivateKey is null || value.PrincipalId == Guid.Empty || value.HostId == Guid.Empty ||
            (value.PublicKey.Length == 0) != (value.PrivateKey.Length == 0) || (value.PrincipalId is not null && value.PrivateKey.Length == 0) ||
            (value.HostId is not null && value.PrincipalId is null) ||
            value.RetiredTickets is null ||
            (value.Version == 1 && (value.PrivateKey.Length == 0 || value.HostId is not null || value.Pending is not null || value.Completed is not null || value.RetiredTickets.Count != 0)))
            throw new InvalidDataException("Invalid client credential format.");
        var retired = new HashSet<(Guid, Guid)>();
        foreach (var ticket in value.RetiredTickets)
        {
            Validate(ticket);
            if (ticket.HostId != value.HostId || !retired.Add((ticket.HostId, ticket.TicketId)))
                throw new InvalidDataException("Invalid retired client ticket history.");
        }
        if (value.Pending is { } pending)
        {
            Validate(pending.Ceremony);
            if (retired.Contains((pending.Ceremony.HostId, pending.Ceremony.TicketId)) || pending.PublicKey is not { Length: > 0 } || pending.PrivateKey is not { Length: > 0 } ||
                (value.HostId is not null && value.HostId != pending.Ceremony.HostId) ||
                (pending.Ceremony.KeyUse == ClientCredentialKeyUse.ExistingForRehome &&
                 (value.PrincipalId is null || !pending.PublicKey.AsSpan().SequenceEqual(value.PublicKey) || !pending.PrivateKey.AsSpan().SequenceEqual(value.PrivateKey))))
                throw new InvalidDataException("Invalid pending client credential.");
        }
        if (value.Completed is { } completed)
        {
            Validate(completed.Ceremony);
            if (retired.Contains((completed.Ceremony.HostId, completed.Ceremony.TicketId)) || completed.PrincipalId == Guid.Empty || completed.PrincipalId != value.PrincipalId || completed.Ceremony.HostId != value.HostId ||
                completed.PublicKey is null || !completed.PublicKey.AsSpan().SequenceEqual(value.PublicKey))
                throw new InvalidDataException("Invalid completed client receipt.");
        }
    }
    private static LocalPrincipalKeyPair Copy(byte[] publicKey, byte[] privateKey) => new(publicKey.ToArray(), privateKey.ToArray());
    private static void Clear(Payload? value)
    {
        if (value?.PrivateKey is not null) CryptographicOperations.ZeroMemory(value.PrivateKey);
        if (value?.Pending?.PrivateKey is not null) CryptographicOperations.ZeroMemory(value.Pending.PrivateKey);
    }
    public async Task<LocalPrincipalKeyPair> PrepareAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default)
    {
        Validate(ceremony);
        using var held = await LockAsync(ct); var value = Read() ?? new Payload { Version = 2 };
        try
        {
            // Host consumed-ticket retries confirm identity indefinitely without re-registering
            // the submitted key. Never generate new material for a retired completed ticket.
            if (value.RetiredTickets.Any(t => t.HostId == ceremony.HostId && t.TicketId == ceremony.TicketId))
                throw new InvalidOperationException("This ticket completed against an earlier client credential.");
            if (value.Pending is { } pending)
            {
                if (pending.Ceremony != ceremony) throw new InvalidOperationException("Another client ceremony is unresolved.");
                return Copy(pending.PublicKey, pending.PrivateKey);
            }
            if (value.Completed?.Ceremony == ceremony) return Copy(value.PublicKey, value.PrivateKey);
            if (value.Completed?.Ceremony is { } prior && prior.HostId == ceremony.HostId && prior.TicketId == ceremony.TicketId)
                throw new InvalidOperationException("A completed ticket cannot change purpose or key choice.");
            if (value.HostId is not null && value.HostId != ceremony.HostId) throw new InvalidOperationException("The current credential belongs to another Host.");
            LocalPrincipalKeyPair pair;
            if (ceremony.KeyUse == ClientCredentialKeyUse.ExistingForRehome)
            {
                if (value.PrincipalId is null) throw new InvalidOperationException("Re-home reuse requires an existing bound client credential.");
                pair = Copy(value.PublicKey, value.PrivateKey);
            }
            // A v1 unbound key may already have been submitted before a lost completion
            // reply. Preserve it; the later client must prove the returned principal with it.
            else if (value.PrincipalId is null && value.PrivateKey.Length > 0) pair = Copy(value.PublicKey, value.PrivateKey);
            else pair = _generator.Generate();
            value.Pending = new() { Ceremony = ceremony, PublicKey = pair.PublicKey, PrivateKey = pair.PrivateKey };
            if (ceremony.KeyUse == ClientCredentialKeyUse.Fresh && value.PrincipalId is not null && pair.PublicKey.AsSpan().SequenceEqual(value.PublicKey))
                throw new CryptographicException("Fresh preparation cannot reuse the current key.");
            ct.ThrowIfCancellationRequested(); Write(value);
            return Copy(pair.PublicKey, pair.PrivateKey);
        }
        finally { Clear(value); }
    }
    public async Task ConfirmAsync(ClientCredentialCeremony ceremony, Guid principalId, ReadOnlyMemory<byte> publicKey, CancellationToken ct = default)
    {
        Validate(ceremony);
        if (principalId == Guid.Empty || publicKey.IsEmpty) throw new ArgumentException("Exact Host result and submitted public key are required.");
        using var held = await LockAsync(ct); var value = Read() ?? throw new InvalidOperationException("No prepared client credential.");
        try
        {
            if (value.Pending is null)
            {
                if (value.Completed?.Ceremony == ceremony && value.PrincipalId == principalId && publicKey.Span.SequenceEqual(value.PublicKey)) return;
                throw new InvalidOperationException("Confirmation does not match the completed ceremony.");
            }
            var pending = value.Pending;
            if (pending.Ceremony != ceremony || !publicKey.Span.SequenceEqual(pending.PublicKey) ||
                ((ceremony.Purpose == ClientCredentialPurpose.OwnerRotation || ceremony.KeyUse == ClientCredentialKeyUse.ExistingForRehome) &&
                 value.PrincipalId is not null && value.PrincipalId != principalId))
                throw new InvalidOperationException("Confirmation does not match the prepared credential.");
            CryptographicOperations.ZeroMemory(value.PrivateKey);
            value.PublicKey = pending.PublicKey; value.PrivateKey = pending.PrivateKey; value.PrincipalId = principalId; value.HostId = ceremony.HostId;
            if (value.Completed is { } completed) value.RetiredTickets.Add(completed.Ceremony);
            value.Pending = null; value.Completed = new() { Ceremony = ceremony, PrincipalId = principalId, PublicKey = pending.PublicKey };
            ct.ThrowIfCancellationRequested(); Write(value);
        }
        finally { Clear(value); }
    }
    public async Task DiscardPendingAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default)
    {
        Validate(ceremony); using var held = await LockAsync(ct); var value = Read();
        try
        {
            if (value?.Pending is null) return;
            if (value.Pending.Ceremony != ceremony) throw new InvalidOperationException("Cannot discard another ceremony.");
            CryptographicOperations.ZeroMemory(value.Pending.PrivateKey); value.Pending = null;
            ct.ThrowIfCancellationRequested(); Write(value);
        }
        finally { Clear(value); }
    }
}
