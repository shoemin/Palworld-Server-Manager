namespace PalworldServerManager.Client.Platform.Contracts;

public enum ClientCredentialPurpose { Bootstrap = 1, Enrollment = 2, OwnerRotation = 3, OwnerRehome = 4 }
public enum ClientCredentialKeyUse { Fresh = 1, ExistingForRehome = 2 }

// Public context only. A ticket/receipt is not authority and contains no handoff secret.
public sealed record ClientCredentialCeremony(Guid HostId, Guid TicketId, ClientCredentialPurpose Purpose, ClientCredentialKeyUse KeyUse);

public interface ILocalPrincipalCredentialCeremonyStore
{
    // Resume the durable key choice after a lost result; never infer it from stale current keys.
    Task<ClientCredentialCeremony?> ReadPreparedAsync(Guid hostId, Guid ticketId, ClientCredentialPurpose purpose, CancellationToken ct = default);
    // Persist before sending the public key. Caller owns and clears returned private material.
    Task<LocalPrincipalKeyPair> PrepareAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default);
    // Call only after authenticated Host confirmation AND proof that this prepared key
    // authenticates the returned principal. A historical consumed-ticket ID alone is insufficient.
    Task ConfirmAsync(ClientCredentialCeremony ceremony, Guid principalId, ReadOnlyMemory<byte> publicKey, CancellationToken ct = default);
    // Explicit recovery after authoritative terminal refusal, never on timeout or lost reply.
    Task DiscardPendingAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default);
}
