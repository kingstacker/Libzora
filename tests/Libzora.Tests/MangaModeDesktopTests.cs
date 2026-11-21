using Libzora.Core;
using Xunit;

namespace Libzora.Tests;

public sealed class MangaModeDesktopTests
{
    [Fact]
    public void AppSettingsStoresAndNormalizesPerBookMangaSettingsWithoutLeakingAcrossBooks()
    {
        var mangaId = Guid.NewGuid();
        var novelId = Guid.NewGuid();
        var settings = new AppSettings().WithMangaSettings(mangaId, new MangaBookSettings
        {
            Enabled = true,
            Direction = MangaReadingDirection.RightToLeft,
            AutoCrop = true,
            BlackBackground = true,
            TwoPageSpread = true
        });

        var normalized = AppSettings.Normalize(settings);
        Assert.True(normalized.TryGetMangaSettings(mangaId, out var saved));
        Assert.True(saved.Enabled);
        Assert.Equal(MangaReadingDirection.RightToLeft, saved.Direction);
        Assert.Equal(MangaReadingDirection.RightToLeft, saved.LastPaginatedDirection);
        Assert.True(saved.AutoCrop);
        Assert.True(saved.BlackBackground);
        Assert.True(saved.TwoPageSpread);
        Assert.False(normalized.TryGetMangaSettings(novelId, out _));
    }

    [Fact]
    public void ReaderLayoutDefaultsAllowsScrollAndSpreadWhenMangaModeOverridesVerticalWriting()
    {
        var scrollManga = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            BodyPadding: 48,
            FlowMode: 0,
            VerticalWriting: true,
            TwoPageMode: false)
        {
            MangaMode = true,
            MangaAutoCrop = true
        });
        Assert.Equal(0, scrollManga.BodyPadding);
        Assert.Equal(0, scrollManga.FlowMode);
        Assert.False(scrollManga.VerticalWriting);
        Assert.False(scrollManga.TwoPageMode);

        var spreadManga = ReaderLayoutDefaults.Normalize(new ReaderLayoutSettings(
            BodyPadding: 48,
            FlowMode: 1,
            VerticalWriting: true,
            TwoPageMode: true)
        {
            MangaMode = true,
            MangaRightToLeft = true
        });
        Assert.Equal(0, spreadManga.BodyPadding);
        Assert.Equal(1, spreadManga.FlowMode);
        Assert.False(spreadManga.VerticalWriting);
        Assert.True(spreadManga.TwoPageMode);
        Assert.True(spreadManga.MangaRightToLeft);
    }
}
