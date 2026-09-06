using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; stop.Cancel(); };
return await PalworldServerManager.Host.Cli.OfflineHostCli.RunAsync(args, ct: stop.Token);
