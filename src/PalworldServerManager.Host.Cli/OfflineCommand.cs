namespace PalworldServerManager.Host.Cli;

public sealed record OfflineCommand(string Kind, string? IntendedSid, string? RecoveryReason)
{
    public static OfflineCommand Parse(string[] args)
    {
        if (args is ["rotate-owner"]) return new("rotate-owner", null, null);
        if (args is ["bootstrap", "--owner-sid", var bootstrapSid]) return new("bootstrap", bootstrapSid, null);
        if (args is ["rehome-owner", "--owner-sid", var rehomeSid]) return new("rehome-owner", rehomeSid, null);
        if (args is ["recover-machine", "--reason", "loss" or "compromise"]) return new("recover-machine", null, args[2]);
        throw new ArgumentException("Use bootstrap --owner-sid SID, rotate-owner, rehome-owner --owner-sid SID, or recover-machine --reason loss|compromise.");
    }
}

// Caller owns its lease through completion. Ordinary cancellation after commit cannot reopen
// the stale-publication window by releasing exclusivity. Hard process termination is separate.
public static class OfflinePublicationBarrier
{
    public static async Task CompleteAsync(Func<Task> reconcile, Action reportPending, Func<Task>? retryDelay = null)
    {
        var reported = false;
        while (true)
        {
            try { await reconcile().ConfigureAwait(false); return; }
            catch (Exception)
            {
                if (!reported)
                {
                    // A broken diagnostic pipe must not release the lease after commit either.
                    try { reportPending(); } catch (Exception) { }
                    reported = true;
                }
                await (retryDelay?.Invoke() ?? Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
        }
    }
}
