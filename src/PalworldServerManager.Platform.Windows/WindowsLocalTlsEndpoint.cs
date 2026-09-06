using System.IO.Pipes;
using System.Security.Authentication;
using System.Security.AccessControl;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace PalworldServerManager.Platform.Windows;

// Called only by the executable composition root. Business handlers receive the resulting
// canonical OS-principal string; they never use Windows APIs or treat it as authorization.
public static class WindowsLocalTlsEndpoint
{
    public static void Configure(IWebHostBuilder builder, string pipeName, SecurityIdentifier serviceSid,
        SecurityIdentifier activationGroupSid, X509Certificate2 certificate, Func<ConnectionDelegate, ConnectionDelegate>? applicationMiddleware = null)
    {
        if (string.IsNullOrEmpty(pipeName) || pipeName.Length > 128 || pipeName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new ArgumentException("A bounded local pipe name is required.");
        if (!certificate.HasPrivateKey) throw new ArgumentException("A Host TLS credential is required.");
        builder.UseNamedPipes(options =>
        {
            options.CurrentUserOnly = false;
            var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            foreach (var sid in new[] { serviceSid, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) }.Distinct())
                acl.AddAccessRule(new PipeAccessRule(sid, PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(activationGroupSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            options.PipeSecurity = acl;
        });
        builder.ConfigureKestrel(options => options.ListenNamedPipe(pipeName, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(certificate, tls => tls.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
            if (applicationMiddleware is not null) listen.Use(applicationMiddleware);
        }));
    }
    public static string ReadNativePrincipal(HttpContext context)
    {
        if (!context.Request.IsHttps || context.Request.Protocol != "HTTP/2") throw new AuthenticationException("Authenticated local HTTP/2 is required.");
        var pipe = context.Features.Get<IConnectionNamedPipeFeature>()?.NamedPipe ?? throw new AuthenticationException("Native local transport identity is unavailable.");
        string? sid = null;
        pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(true); sid = identity?.User?.Value; });
        return sid ?? throw new AuthenticationException("Native local principal is unavailable.");
    }
}
