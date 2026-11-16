using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices;

public sealed class SyncModule
{
    internal SyncModule(ApplicationModuleOptions options, AccountService accounts, AccountRegion dataRegion, IDeviceFeatureAccess features)
    {
        Service = options.SyncService ?? new S3SyncService(
            options.Paths,
            options.SecretProtector,
            accounts,
            dataRegion,
            features,
            options.SyncProviders ?? SyncProviderRegistry.CreateDefault());
    }

    public ISyncService Service { get; }
}
