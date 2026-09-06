using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Grpc.Core;
using PalworldServerManager.Client.Platform.Contracts;
using PalworldServerManager.Contracts;

namespace PalworldServerManager.Client.Security;

public static class LocalSecurityCommands
{
    public const string Usage = "identity | invite --user-sid SID | revoke --principal UUID | complete-enrollment --ticket UUID (code on stdin) | complete-handoff --ticket UUID";
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty ? id : throw new ArgumentException();
    public static async Task<int> RunAsync(string[] args, Func<LocalSecurityClient> create, TextReader input, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        if (args.Length == 0 || args is ["--help"]) { await output.WriteLineAsync(Usage).ConfigureAwait(false); return 0; }
        try
        {
            LocalIdentity identity;
            switch (args)
            {
                case ["identity"]: identity = await create().GetIdentityAsync(ct).ConfigureAwait(false); break;
                case ["invite", "--user-sid", var sid] when sid.Length is > 0 and <= 256:
                    using (var invitation = await create().CreateEnrollmentAsync(sid, ct).ConfigureAwait(false))
                    {
                        var code = invitation.ExportCodeForDelivery();
                        try
                        {
                            // Deliberate bearer delivery to the authorized Owner. Never reuse this
                            // serialization in diagnostics; the caller chooses its secure out-of-band transfer.
                            await output.WriteLineAsync(JsonSerializer.Serialize(new { ticketId = invitation.TicketId, expiresUtc = invitation.ExpiresUtc, code = Convert.ToBase64String(code) })).ConfigureAwait(false);
                        }
                        finally { CryptographicOperations.ZeroMemory(code); }
                    }
                    return 0;
                case ["revoke", "--principal", var principal]:
                    var id = Id(principal); await create().RevokeAsync(id, ct).ConfigureAwait(false);
                    await output.WriteLineAsync(JsonSerializer.Serialize(new { localPrincipalId = id, result = "Revoked" })).ConfigureAwait(false); return 0;
                case ["complete-enrollment", "--ticket", var ticket]:
                    var enrollment = Id(ticket); var secret = await ReadCode(input, ct).ConfigureAwait(false);
                    try { identity = await create().CompleteEnrollmentAsync(enrollment, secret, ct).ConfigureAwait(false); }
                    finally { CryptographicOperations.ZeroMemory(secret); }
                    break;
                case ["complete-handoff", "--ticket", var ticket]:
                    var handoff = Id(ticket); identity = await create().CompleteHandoffAsync(handoff, ct).ConfigureAwait(false); break;
                default: throw new ArgumentException();
            }
            await output.WriteLineAsync(JsonSerializer.Serialize(new { localPrincipalId = identity.LocalPrincipalId, isOwner = identity.IsOwner })).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var message = LocalSecurityClient.FindCause<LocalHostAuthenticationException>(ex) is not null ? "Local Host authentication failed." : ex switch
            {
                OperationCanceledException => "Local request canceled.",
                ClientActivationException failed => "Local Host activation failed: " + failed.Status + ".",
                LocalHostTrustUnavailableException => "Machine bootstrap has not published local trust.",
                ProtocolCompatibilityException => "Incompatible local protocol.",
                AuthenticationException => "Local principal or ceremony authentication failed.",
                UnauthorizedAccessException => "Local credential or handoff access refused.",
                ArgumentException or FormatException or InvalidDataException => "Invalid local command or security data.",
                RpcException rpc => "Local request refused or failed: " + rpc.StatusCode + ".",
                _ => "Local request failed."
            };
            await error.WriteLineAsync(message).ConfigureAwait(false); return 1;
        }
    }
    private static async Task<byte[]> ReadCode(TextReader input, CancellationToken ct)
    {
        var chars = new char[45]; var one = new char[1]; var count = 0;
        try
        {
            while (count < chars.Length && await input.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false) != 0)
            {
                if (one[0] is '\r' or '\n') break;
                chars[count++] = one[0];
            }
            var result = new byte[32];
            if (count == 44 && Convert.TryFromBase64Chars(chars.AsSpan(0, count), result, out var written) && written == result.Length) return result;
            CryptographicOperations.ZeroMemory(result); throw new ArgumentException("Invalid enrollment code.");
        }
        finally { Array.Clear(chars); Array.Clear(one); }
    }
}
