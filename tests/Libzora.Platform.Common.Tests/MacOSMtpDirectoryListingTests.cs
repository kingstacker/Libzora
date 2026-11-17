using Libzora.Platform.MacOS.Mtp;
using Xunit;

namespace Libzora.Platform.Common.Tests;

public sealed class MacOSMtpDirectoryListingTests
{
    [Fact]
    public void EmptyListingWithoutNativeErrorsIsAGenuineEmptyDirectory()
    {
        Assert.Empty(MtpDirectoryListing.ResolveEmpty([], "documents"));
    }

    [Fact]
    public void NativeErrorWithNullListingIsNotAnEmptyDirectory()
    {
        var exception = Assert.Throws<IOException>(() =>
            MtpDirectoryListing.ResolveEmpty(["[2] I/O error"], "documents"));

        Assert.Contains("documents", exception.Message);
        Assert.Contains("I/O error", exception.Message);
    }

    [Fact]
    public void FailedListingIsNotCachedAndIsRetried()
    {
        var cache = new MtpDirectoryListingCache();
        var attempts = 0;

        Assert.Throws<IOException>(() => cache.GetOrLoad((1u, 1u), () =>
        {
            attempts++;
            throw new IOException("native failure");
        }));
        Assert.Throws<IOException>(() => cache.GetOrLoad((1u, 1u), () =>
        {
            attempts++;
            throw new IOException("native failure");
        }));

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void SuccessfulEmptyListingIsCached()
    {
        var cache = new MtpDirectoryListingCache();
        var loads = 0;

        var first = cache.GetOrLoad((1u, 1u), () =>
        {
            loads++;
            return Array.Empty<MtpEntry>();
        });
        var second = cache.GetOrLoad((1u, 1u), () =>
        {
            loads++;
            return Array.Empty<MtpEntry>();
        });

        Assert.Empty(first);
        Assert.Same(first, second);
        Assert.Equal(1, loads);
    }
}
