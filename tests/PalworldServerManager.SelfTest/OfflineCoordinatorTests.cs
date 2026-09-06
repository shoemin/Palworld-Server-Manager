using PalworldServerManager.Host.Cli;
using PalworldServerManager.Host.Persistence;
using static PalworldServerManager.SelfTest.WindowsPlatformTests;

namespace PalworldServerManager.SelfTest;

public static class OfflineCoordinatorTests
{
    public static Task CommandBoundary()
    {
        Check(OfflineCommand.Parse(["bootstrap", "--owner-sid", "S-1-5-21-1-2-3-4"]).Kind == "bootstrap", "Bootstrap command not recognized.");
        Check(OfflineCommand.Parse(["rotate-owner"]).IntendedSid is null, "Owner rotation accepts a caller-selected Owner.");
        Check(OfflineCommand.Parse(["rehome-owner", "--owner-sid", "S-1-5-21-1-2-3-4"]).Kind == "rehome-owner", "Re-home command not recognized.");
        foreach (var reason in new[] { "loss", "compromise" }) Check(OfflineCommand.Parse(["recover-machine", "--reason", reason]).RecoveryReason == reason, "Recovery reason changed.");
        foreach (var args in new string[][] { [], ["start"], ["rotate-owner", "--owner-sid", "someone"], ["bootstrap"], ["recover-machine", "--reason", "unknown"], ["recover-machine", "--reason", "loss", "--online"], ["bootstrap", "--config", "elsewhere"] })
        { try { OfflineCommand.Parse(args); throw new Exception("Unsupported offline command accepted."); } catch (ArgumentException) { } }
        return Task.CompletedTask;
    }
    public static async Task PublicationBarrier()
    {
        var name = "PSMAstraBarrier" + Guid.NewGuid().ToString("N");
        using var lease = HostExclusivityLock.TryAcquire(TimeSpan.Zero, name) ?? throw new Exception("Could not acquire fixture lease.");
        var attempts = 0; var reports = 0; var delays = 0; using var stop = new CancellationTokenSource();
        await OfflinePublicationBarrier.CompleteAsync(() =>
        {
            using var contender = HostExclusivityLock.TryAcquire(TimeSpan.Zero, name);
            Check(contender is null, "Publication failure/retry released the machine lease.");
            attempts++;
            if (attempts == 1) throw new IOException("Synthetic publication failure.");
            if (attempts == 2) throw new OperationCanceledException(stop.Token);
            if (attempts == 3) throw new UnauthorizedAccessException("Synthetic cleanup failure.");
            return Task.CompletedTask;
        }, () => { reports++; throw new IOException("Synthetic broken diagnostic pipe."); },
        () => { delays++; stop.Cancel(); return Task.CompletedTask; });
        Check(attempts == 4 && delays == 3 && reports == 1, "Publication barrier did not retry every failure through durable completion.");
        var committed = false; var published = false;
        try
        {
            await OfflinePublicationBarrier.CommitAndCompleteAsync(() => committed = true, () => { published = true; return Task.CompletedTask; }, () => { }, stop.Token);
            throw new Exception("Canceled selection commit was accepted.");
        }
        catch (OperationCanceledException) { }
        Check(!committed && !published, "Pre-commit cancellation selected a credential or published it.");
        using var duringCommit = new CancellationTokenSource();
        await OfflinePublicationBarrier.CommitAndCompleteAsync(() => { committed = true; duringCommit.Cancel(); },
            () => { published = true; return Task.CompletedTask; }, () => { }, duringCommit.Token);
        Check(committed && published, "Cancellation at commit abandoned the required publication.");
    }
}
