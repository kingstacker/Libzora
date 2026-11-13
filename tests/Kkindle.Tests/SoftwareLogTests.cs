using System.Text;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class SoftwareLogTests
{
    [Fact]
    public void SanitizesAccountsPathsTitlesTokensAndUrls()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            SoftwareLog.Configure(paths, "test-version", "macos");
            var text = string.Join('\n',
                $"Cannot open {Path.Combine(paths.Root, "data", "library", "三体.epub")}",
                "reader@example.invalid",
                "https://api.example.com/v1/chat?key=very-secret-value",
                "550e8400-e29b-41d4-a716-446655440000",
                "Bearer abcdefghijklmnopqrstuvwxyz0123456789",
                "C:\\Users\\reader\\Books\\三体.epub",
                "《三体》转换失败",
                "10.23.45.67");

            var sanitized = SoftwareLog.Sanitize(text);

            Assert.DoesNotContain("reader@example.invalid", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("very-secret-value", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("550e8400", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("三体", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("reader\\Books", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain("10.23.45.67", sanitized, StringComparison.Ordinal);
            Assert.Contains("<app>", sanitized, StringComparison.Ordinal);
            Assert.Contains("<email>", sanitized, StringComparison.Ordinal);
            Assert.Contains("https://api.example.com", sanitized, StringComparison.Ordinal);
            Assert.Contains("<id>", sanitized, StringComparison.Ordinal);
            Assert.Contains("<title>", sanitized, StringComparison.Ordinal);
            Assert.DoesNotContain(SoftwareLog.Sanitize("bad\0log"), "\0", StringComparison.Ordinal);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void RecordsExplicitFailuresAndCapturesBoundedFeedbackLog()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            SoftwareLog.Configure(paths, "test-version", "macos");
            SoftwareLog.Error("sync.run", new InvalidOperationException("reader@example.invalid"),
                ("silent", false));
            SoftwareLog.ErrorWithoutMessage("ai.request",
                new InvalidOperationException("private prompt text"));
            SoftwareLog.Warning("plugin.script", ("reason", "timeout"));

            var captured = SoftwareLog.CaptureFeedbackLog();

            Assert.NotNull(captured);
            Assert.Contains("session.start", captured, StringComparison.Ordinal);
            Assert.Contains("sync.run", captured, StringComparison.Ordinal);
            Assert.Contains("ai.request", captured, StringComparison.Ordinal);
            Assert.Contains("plugin.script", captured, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", captured, StringComparison.Ordinal);
            Assert.Contains("<email>", captured, StringComparison.Ordinal);
            Assert.DoesNotContain("reader@example.invalid", captured, StringComparison.Ordinal);
            Assert.DoesNotContain("private prompt text", captured, StringComparison.Ordinal);
            Assert.True(Encoding.UTF8.GetByteCount(captured) <= SoftwareLog.MaximumFeedbackBytes);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void RotatesBoundedLogsAndKeepsFeedbackCaptureUnderTheLimit()
    {
        var root = TestHelpers.CreateTempDirectory();
        try
        {
            var paths = new AppPaths(root);
            paths.EnsureDirectories();
            SoftwareLog.Configure(paths, "test-version", "linux");
            for (var index = 0; index < 5000; index++)
                SoftwareLog.ErrorWithoutMessage("sync.run", new InvalidOperationException(), ("attempt", index));

            Assert.True(File.Exists(Path.Combine(paths.Logs, "software.1.log")));
            var current = new FileInfo(Path.Combine(paths.Logs, "software.log"));
            Assert.True(current.Length <= SoftwareLog.MaximumFileBytes);

            var captured = SoftwareLog.CaptureFeedbackLog();
            Assert.NotNull(captured);
            Assert.True(Encoding.UTF8.GetByteCount(captured) <= SoftwareLog.MaximumFeedbackBytes);
        }
        finally
        {
            TestHelpers.TryDelete(root);
        }
    }

    [Fact]
    public void RejectsOversizedOrControlRichSoftwareLogs()
    {
        Assert.Null(FeedbackValidation.ValidateSoftwareLog(null));
        Assert.Null(FeedbackValidation.ValidateSoftwareLog(""));
        Assert.Null(FeedbackValidation.ValidateSoftwareLog("ERROR sync.run\r\n  exception=System.TimeoutException"));
        Assert.NotNull(FeedbackValidation.ValidateSoftwareLog("bad\0log"));
        Assert.NotNull(FeedbackValidation.ValidateSoftwareLog(
            new string('x', FeedbackLimits.MaxSoftwareLogBytes + 1)));
    }
}
