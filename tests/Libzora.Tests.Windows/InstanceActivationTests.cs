using Libzora.Platform.Windows;

namespace Libzora.Tests.Windows;

public sealed class InstanceActivationTests
{
    [Fact]
    public async Task SecondInstanceFilesReachThePrimaryActivationHandler()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"Libzora.Tests.Activation.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = WindowsInstanceActivationServer.TryStart(
            pipeName,
            files => received.TrySetResult(files));
        Assert.NotNull(server);

        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        try
        {
            Assert.True(WindowsInstanceActivationServer.TryRequestActivation(
                pipeName,
                [first, second]));

            var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { first, second }, files);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public async Task ActivationWithoutFilesStillReachesThePrimary()
    {
        if (!OperatingSystem.IsWindows()) return;

        var pipeName = $"Libzora.Tests.Activation.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<IReadOnlyList<string>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = WindowsInstanceActivationServer.TryStart(
            pipeName,
            files => received.TrySetResult(files));
        Assert.NotNull(server);

        Assert.True(WindowsInstanceActivationServer.TryRequestActivation(pipeName, []));

        Assert.Empty(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ActivationRequestFailsCleanlyWhenNoPrimaryIsListening()
    {
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(WindowsInstanceActivationServer.TryRequestActivation(
            $"Libzora.Tests.Activation.{Guid.NewGuid():N}",
            []));
    }
}
