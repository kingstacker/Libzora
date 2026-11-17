using Libzora.Platform.Windows;
using NAudio.Wave;

namespace Libzora.Tests.Windows;

public sealed class WindowsTtsAudioPlayerTests
{
    [Fact]
    public async Task PlaybackStoppedExceptionFaultsTheCompletionTask()
    {
        if (!OperatingSystem.IsWindows()) return;
        var audioPath = CreateAudioFile();
        try
        {
            var output = new FakeWaveOutput();
            using var player = new WindowsTtsAudioPlayer(_ => output);

            var playback = player.PlayAsync(audioPath);
            var failure = new InvalidDataException("decoder failed");
            output.RaiseStopped(failure);

            var thrown = await Assert.ThrowsAsync<InvalidDataException>(() => playback);
            Assert.Same(failure, thrown);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task NaturalPlaybackStoppedCompletesSuccessfully()
    {
        if (!OperatingSystem.IsWindows()) return;
        var audioPath = CreateAudioFile();
        try
        {
            var output = new FakeWaveOutput();
            using var player = new WindowsTtsAudioPlayer(_ => output);

            var playback = player.PlayAsync(audioPath);
            output.RaiseStopped(null);

            await playback;
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task StopWinsOverTheNullStoppedEventRaisedByTheOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var audioPath = CreateAudioFile();
        try
        {
            var output = new FakeWaveOutput();
            using var player = new WindowsTtsAudioPlayer(_ => output);

            var playback = player.PlayAsync(audioPath);
            player.Stop();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
            Assert.True(output.StopCalled);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    [Fact]
    public async Task TokenCancellationWinsOverTheNullStoppedEventRaisedByTheOutput()
    {
        if (!OperatingSystem.IsWindows()) return;
        var audioPath = CreateAudioFile();
        try
        {
            var output = new FakeWaveOutput();
            using var player = new WindowsTtsAudioPlayer(_ => output);
            using var cancellation = new CancellationTokenSource();

            var playback = player.PlayAsync(audioPath, cancellation.Token);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
            Assert.True(output.StopCalled);
        }
        finally
        {
            File.Delete(audioPath);
        }
    }

    private static string CreateAudioFile()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "LibzoraTests",
            Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private sealed class FakeWaveOutput : WindowsTtsAudioPlayer.ITtsWaveOutput
    {
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public bool StopCalled { get; private set; }

        public void Init()
        {
        }

        public void Play()
        {
        }

        public void Pause()
        {
        }

        public void Stop()
        {
            StopCalled = true;
            // WaveOut raises PlaybackStopped synchronously with a null error
            // when Stop() is called, which must not turn cancellation into
            // success.
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(null));
        }

        public void Dispose()
        {
        }

        public void RaiseStopped(Exception? exception) =>
            PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
    }
}
