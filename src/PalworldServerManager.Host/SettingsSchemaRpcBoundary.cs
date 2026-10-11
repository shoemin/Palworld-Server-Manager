using System.Security.Authentication;
using Grpc.Core;

namespace PalworldServerManager.Host;

internal static class SettingsSchemaRpcBoundary
{
    internal static async Task<T> Invoke<T>(Func<Task<T>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw Error(StatusCode.Cancelled); }
        catch (AuthenticationException) { throw Error(StatusCode.Unauthenticated); }
        catch (UnauthorizedAccessException) { throw Error(StatusCode.PermissionDenied); }
        catch (ArgumentException) { throw Error(StatusCode.InvalidArgument); }
        catch (InvalidOperationException) { throw Error(StatusCode.FailedPrecondition); }
        catch (RpcException ex)
        {
            throw Error(ex.StatusCode is StatusCode.PermissionDenied or StatusCode.Unauthenticated or StatusCode.Cancelled or
                StatusCode.FailedPrecondition or StatusCode.InvalidArgument or StatusCode.Aborted ? ex.StatusCode : StatusCode.Unavailable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { throw Error(StatusCode.Internal); }
    }
    private static RpcException Error(StatusCode status) => new(new(status, "Settings schema request refused or did not complete."));
}
