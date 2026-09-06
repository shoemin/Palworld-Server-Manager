namespace PalworldServerManager.Client.Platform.Contracts;

public enum ClientCredentialPurpose { Bootstrap = 1, Enrollment = 2, OwnerRotation = 3, OwnerRehome = 4 }
public enum ClientCredentialKeyUse { Fresh = 1, ExistingForRehome = 2 }

// Public context only. A ticket/receipt is not authority and contains no handoff secret.
public sealed record ClientCredentialCeremony(Guid HostId, Guid TicketId, ClientCredentialPurpose Purpose, ClientCredentialKeyUse KeyUse);

public interface ILocalPrincipalCredentialCeremonyStore
{
    // Persist before sending the public key. Caller owns and clears returned private material.
    Task<LocalPrincipalKeyPair> PrepareAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default);
    // Call only after authenticated Host confirmation of this exact submitted public key.
    Task ConfirmAsync(ClientCredentialCeremony ceremony, Guid principalId, ReadOnlyMemory<byte> publicKey, CancellationToken ct = default);
    // Explicit recovery after authoritative terminal refusal, never on timeout or lost reply.
    Task DiscardPendingAsync(ClientCredentialCeremony ceremony, CancellationToken ct = default);
}
