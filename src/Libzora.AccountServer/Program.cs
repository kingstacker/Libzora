using Libzora.AccountServer;

await using var application = await AccountServerHost.BuildAsync(args);
await application.RunAsync();
