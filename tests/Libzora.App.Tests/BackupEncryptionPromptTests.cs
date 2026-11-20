using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Theory]
    [InlineData("zh-CN", "去设置", "知道了")]
    [InlineData("en-US", "Go to settings", "Got it")]
    public Task MissingBackupEncryptionKeyDialogShowsGoToSettingsOnLeftAndOpensKeyField(
        string language, string expectedSecondary, string expectedOk) => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        ((Libzora.App)Application.Current!).ApplyLanguage(language);
        try
        {
            var promptTask = scope.Call<Task<bool>>("EnsureBackupEncryptionForUiAsync");
            await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
            await Render();

            var secondary = scope.Get<Button>("MessageSecondaryButton");
            var ok = scope.Get<Button>("MessageOkButton");
            Assert.True(secondary.IsVisible);
            Assert.Equal(0, Grid.GetColumn(secondary));
            Assert.Equal(2, Grid.GetColumn(ok));
            Assert.Equal(expectedSecondary, secondary.Content);
            Assert.Equal(expectedOk, ok.Content);
            Assert.True(secondary.TranslatePoint(default, scope.Window)!.Value.X
                        < ok.TranslatePoint(default, scope.Window)!.Value.X);

            secondary.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(await promptTask);
            await Until(() => scope.Get<Control>("S3EncryptionKeyBox").IsEffectivelyVisible);

            Assert.False(scope.Get<Control>("MessageOverlay").IsVisible);
            Assert.True(scope.Get<Control>("SettingsPage").IsVisible);
            Assert.True(scope.Get<Expander>("SettingsS3Expander").IsExpanded);
            Assert.True(scope.Get<Expander>("SettingsS3AdvancedExpander").IsExpanded);

            // A regular informational dialog without a secondary action must not leak the button.
            var infoTask = scope.Call<Task>("ShowMessageAsync", "提示", "普通消息", null, null, null);
            await Until(() => scope.Get<Control>("MessageOverlay").IsVisible);
            Assert.False(secondary.IsVisible);
            ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await infoTask;
        }
        finally
        {
            ((Libzora.App)Application.Current!).ApplyLanguage("zh-CN");
        }
    });
}
