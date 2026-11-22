using Avalonia.Controls;
using Avalonia.Input;
using Xunit;

namespace Libzora.Ui.Tests;

public sealed partial class SettingsTests
{
    [Fact]
    public Task LibraryShortcutsRespectDialogsPagesAndFocusedButtons() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);
        scope.Call("SelectBook", scope.Window.ViewModel.Books[0]);
        foreach (var name in new[] { "ConfirmationOverlay", "MessageOverlay", "OnboardingOverlay" })
        {
            var overlay = scope.Get<Control>(name);
            overlay.IsVisible = true;
            foreach (var key in new[] { Key.Enter, Key.Delete, Key.Escape, Key.F1 })
            {
                var args = new KeyEventArgs { Key = key };
                scope.Call("MainWindow_GlobalKeyDown", scope.Window, args);
                Assert.False(args.Handled);
            }
            overlay.IsVisible = false;
        }
        var buttonKey = new KeyEventArgs { Key = Key.Enter, Source = scope.Get<Button>("AllBooksButton") };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, buttonKey);
        Assert.False(buttonKey.Handled);
        scope.Call("ShowStage3Page", scope.Get<Grid>("SettingsPage"), null);
        var select = new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, select);
        Assert.False(select.Handled);
        scope.Call("ShowLibraryPage");
        select = new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Control };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, select);
        Assert.True(select.Handled);
        await Render();
    });

    [Fact]
    public Task MacLibraryAndReaderShortcutsFollowPlatformConventions() => Run(async () =>
    {
        await using var scope = await TestWindow.Create();
        await SeedLayoutLibrary(scope);

        // Cmd+1 / Cmd+2 / Cmd+3 switch between Grid, List, and Collections views.
        var listMode = new KeyEventArgs { Key = Key.D2, KeyModifiers = KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, listMode);
        Assert.True(listMode.Handled);
        Assert.True(scope.Get<Control>("BookList").IsVisible);

        var collectionsMode = new KeyEventArgs { Key = Key.D3, KeyModifiers = KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, collectionsMode);
        Assert.True(collectionsMode.Handled);
        Assert.True(scope.Get<Control>("CollectionList").IsVisible);

        var gridMode = new KeyEventArgs { Key = Key.D1, KeyModifiers = KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, gridMode);
        Assert.True(gridMode.Handled);
        Assert.True(scope.Get<Control>("BookGrid").IsVisible);

        // Option+Cmd+F toggles the filter panel.
        var filterToggle = new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, filterToggle);
        Assert.True(filterToggle.Handled);
        Assert.True(scope.Get<Control>("FilterPanel").IsVisible);
        filterToggle = new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, filterToggle);
        Assert.False(scope.Get<Control>("FilterPanel").IsVisible);

        // Option+Cmd+I toggles the book detail inspector pane.
        scope.Call("SelectBook", scope.Window.ViewModel.Books[0]);
        Assert.True(scope.Get<Control>("LibraryDetailPane").IsVisible);
        var inspectorToggle = new KeyEventArgs { Key = Key.I, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, inspectorToggle);
        Assert.True(inspectorToggle.Handled);
        await Until(() => !scope.Get<Control>("LibraryDetailPane").IsVisible);

        // Shift+Cmd+N opens the new-collection prompt.
        var newCollection = new KeyEventArgs { Key = Key.N, KeyModifiers = KeyModifiers.Shift | KeyModifiers.Meta };
        scope.Call("MainWindow_GlobalKeyDown", scope.Window, newCollection);
        Assert.True(newCollection.Handled);
        Assert.True(scope.Get<Control>("CollectionNameOverlay").IsVisible);
        scope.Call("CompleteCollectionName", (string?)null);

        if (OperatingSystem.IsMacOS())
        {
            // Cmd+Backspace opens the delete confirmation dialog on macOS.
            scope.Field<HashSet<Guid>>("_selectedBookIds").Add(scope.Window.ViewModel.Books[0].Book.Id);
            var macDelete = new KeyEventArgs { Key = Key.Back, KeyModifiers = KeyModifiers.Meta };
            scope.Call("MainWindow_GlobalKeyDown", scope.Window, macDelete);
            Assert.True(macDelete.Handled);
            await Until(() => scope.Get<Control>("ConfirmationOverlay").IsVisible);
            scope.Call("CompleteConfirmation", false);

            // Cmd+, opens Settings on macOS.
            var macSettings = new KeyEventArgs { Key = Key.OemComma, KeyModifiers = KeyModifiers.Meta };
            scope.Call("MainWindow_GlobalKeyDown", scope.Window, macSettings);
            Assert.True(macSettings.Handled);
            await Until(() => scope.Get<Control>("SettingsPage").IsVisible);

            // Cmd+/ opens the keyboard shortcuts dialog on macOS.
            var macHelp = new KeyEventArgs { Key = Key.OemQuestion, KeyModifiers = KeyModifiers.Meta };
            scope.Call("MainWindow_GlobalKeyDown", scope.Window, macHelp);
            Assert.True(macHelp.Handled);
            Assert.True(scope.Get<Control>("MessageOverlay").IsVisible);
            scope.Get<Control>("MessageOverlay").IsVisible = false;
            scope.Call("ShowLibraryPage");
        }

        // Reader shortcuts: Shift+Cmd+F (whole-book search), Shift+Cmd+T (TOC),
        // Option+Cmd+B (bookmarks), Shift+Cmd+A (assistant), Cmd+Plus/Minus/0 (zoom),
        // and Ctrl+Cmd+Z (zen mode).
        await scope.Call<Task>("OpenBookAsync", scope.Window.ViewModel.Books[0], null, true);
        await Until(() => scope.Get<Control>("ReaderRoot").IsVisible);

        var wholeBookSearch = new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Shift | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, wholeBookSearch);
        Assert.True(wholeBookSearch.Handled);
        Assert.True(scope.Field<bool>("_readerSearchVisible"));
        wholeBookSearch = new KeyEventArgs { Key = Key.F, KeyModifiers = KeyModifiers.Shift | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, wholeBookSearch);
        Assert.False(scope.Field<bool>("_readerSearchVisible"));

        var bookmarksPanel = new KeyEventArgs { Key = Key.B, KeyModifiers = KeyModifiers.Alt | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, bookmarksPanel);
        Assert.True(bookmarksPanel.Handled);
        Assert.True(scope.Get<Control>("ReaderBookmarkPane").IsVisible);

        var tocToggle = new KeyEventArgs { Key = Key.T, KeyModifiers = KeyModifiers.Shift | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, tocToggle);
        Assert.True(tocToggle.Handled);
        Assert.False(scope.Field<bool>("_readerTocExpanded"));

        var assistantToggle = new KeyEventArgs { Key = Key.A, KeyModifiers = KeyModifiers.Shift | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, assistantToggle);
        Assert.True(assistantToggle.Handled);
        Assert.True(scope.Get<Control>("ReaderAssistantPanel").IsVisible);

        var initialScale = scope.Field<Libzora.Core.ReaderLayoutSettings>("_readerLayout").FontScale;
        var zoomIn = new KeyEventArgs { Key = Key.OemPlus, KeyModifiers = KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, zoomIn);
        Assert.True(zoomIn.Handled);
        await Until(() => scope.Field<Libzora.Core.ReaderLayoutSettings>("_readerLayout").FontScale > initialScale);

        var zoomReset = new KeyEventArgs { Key = Key.D0, KeyModifiers = KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, zoomReset);
        Assert.True(zoomReset.Handled);
        await Until(() => Math.Abs(scope.Field<Libzora.Core.ReaderLayoutSettings>("_readerLayout").FontScale - Libzora.Core.ReaderLayoutDefaults.DefaultFontScale) < 0.001);

        var zenToggle = new KeyEventArgs { Key = Key.Z, KeyModifiers = KeyModifiers.Control | KeyModifiers.Meta };
        scope.Call("MainWindow_KeyDown", scope.Window, zenToggle);
        Assert.True(zenToggle.Handled);
        Assert.True(scope.Field<bool>("_readerZenMode"));
        scope.Call("ExitReaderZenMode");
    });
}
