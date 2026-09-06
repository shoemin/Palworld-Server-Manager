using System.Security.Cryptography;
using System.Text.Json;
using PalworldServerManager.Client.Platform.Contracts;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class OwnerHandoffTests
{
    public static Task Format()
    {
        var host = Guid.NewGuid(); var ticket = Guid.NewGuid(); var bytes = new byte[73];
        "PSMOH001"u8.CopyTo(bytes); host.TryWriteBytes(bytes.AsSpan(8, 16)); ticket.TryWriteBytes(bytes.AsSpan(24, 16));
        bytes.AsSpan(41).Fill(0xA5);
        foreach (var purpose in Enum.GetValues<OwnerHandoffPurpose>())
        {
            bytes[40] = (byte)purpose;
            using var value = OwnerHandoff.Parse(bytes, host, ticket);
            Check(value.HostId == host && value.TicketId == ticket && value.Purpose == purpose, "Handoff lost its public binding.");
            var secret = value.ExportSecretForTransport();
            Check(secret.SequenceEqual(bytes[41..]) && !JsonSerializer.Serialize(value).Contains(Convert.ToBase64String(secret)) && !value.ToString().Contains(Convert.ToBase64String(secret)), "Handoff secret escaped its explicit transport export.");
            CryptographicOperations.ZeroMemory(secret); value.Dispose();
            try { value.ExportSecretForTransport(); throw new Exception("Disposed handoff exported secret."); } catch (ObjectDisposedException) { }
        }
        static void Reject(byte[] candidate, Guid h, Guid t)
        { try { using var parsed = OwnerHandoff.Parse(candidate, h, t); throw new Exception("Malformed handoff accepted."); } catch (InvalidDataException) { } }
        Reject(bytes, Guid.NewGuid(), ticket); Reject(bytes, host, Guid.NewGuid()); Reject(bytes, Guid.Empty, ticket);
        Reject(bytes[..72], host, ticket); Reject(new byte[8193], host, ticket);
        foreach (byte purpose in new byte[] { 0, 2, 5, 255 }) { bytes[40] = purpose; Reject(bytes, host, ticket); }
        bytes[40] = 1; bytes[0] ^= 1; Reject(bytes, host, ticket); CryptographicOperations.ZeroMemory(bytes);
        return Task.CompletedTask;
    }
}
