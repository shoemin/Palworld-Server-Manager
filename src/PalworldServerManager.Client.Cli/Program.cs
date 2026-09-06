using PalworldServerManager.Client.Security;

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; stop.Cancel(); };
return await LocalSecurityCommands.RunAsync(args, WindowsClientSecurity.Create, Console.In, Console.Out, Console.Error, stop.Token);
