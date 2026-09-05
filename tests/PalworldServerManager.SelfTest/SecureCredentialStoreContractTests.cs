using System.Security.Cryptography;
using PalworldServerManager.Platform.Contracts;

namespace PalworldServerManager.SelfTest;

public static class SecureCredentialStoreContractTests
{
    // No Windows dependency in the reusable contract body. Future Linux tests supply their factory.
    public static async Task Run(Func<ISecureCredentialStore> reopen)
    {
        var secret = RandomNumberGenerator.GetBytes(48); var replacement = RandomNumberGenerator.GetBytes(73);
        var store = reopen();
        Check(await store.RetrieveAsync("contract-a") is null, "Missing credential was not null.");
        await store.DeleteAsync("contract-a");
        await store.StoreAsync("contract-a", secret);
        Check((await reopen().RetrieveAsync("contract-a"))!.SequenceEqual(secret), "Reopened store lost credential.");
        await store.StoreAsync("contract-A", replacement);
        Check((await store.RetrieveAsync("contract-a"))!.SequenceEqual(secret), "Key case was collapsed.");
        await store.StoreAsync("contract-a", replacement);
        Check((await reopen().RetrieveAsync("contract-a"))!.SequenceEqual(replacement), "Replacement failed.");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Reject<OperationCanceledException>(() => store.StoreAsync("contract-a", secret, cancelled.Token));
        await Reject<OperationCanceledException>(() => store.DeleteAsync("contract-a", cancelled.Token));
        Check((await store.RetrieveAsync("contract-a"))!.SequenceEqual(replacement), "Cancellation changed stored value.");
        await store.StoreAsync("empty", ReadOnlyMemory<byte>.Empty);
        Check((await reopen().RetrieveAsync("empty")) is { Length: 0 }, "Empty and missing were conflated.");
        foreach (var key in new[] { "contract-a", "contract-A", "empty" }) { await store.DeleteAsync(key); await store.DeleteAsync(key); }
        Check(await reopen().RetrieveAsync("contract-a") is null, "Delete did not persist.");
        CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(replacement);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
