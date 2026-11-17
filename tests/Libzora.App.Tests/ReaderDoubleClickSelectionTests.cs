using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Xunit;

namespace Libzora.Ui.Tests;

[Collection("Settings UI")]
public sealed class ReaderDoubleClickSelectionTests(SettingsUiSession session)
{
    [Theory]
    [InlineData(1, false, false, false, "English")]
    [InlineData(0, false, false, false, "English")]
    [InlineData(1, false, true, false, "English")]
    [InlineData(1, false, true, true, "English")]
    [InlineData(1, true, false, false, "English")]
    [InlineData(1, false, false, false, "don't")]
    [InlineData(1, false, false, false, "well-known")]
    public Task DoubleClickSelectsTheWholeWordAndOpensTheFloatingMenu(
        int flowMode, bool vertical, bool spread, bool secondPage, string word) => WithReader(
        new ReaderLayoutSettings(FlowMode: flowMode, VerticalWriting: vertical, TwoPageMode: spread),
        async (scope, host, messages) =>
        {
            var layout = Layout(host);
            var page = layout.Pages[secondPage ? 1 : 0];
            var start = host.BodyText!.IndexOf(word, page.TextStartOffset, StringComparison.Ordinal);
            Assert.InRange(start, page.TextStartOffset, page.TextEndOffset - word.Length);
            var point = WordPoint(scope, host, start, word.Length);

            Click(scope.Window, point);
            Assert.False(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            Click(scope.Window, point);
            await ReaderTests.Render();

            Assert.Equal(word, scope.Field<string?>("_readerPendingSelection"));
            Assert.Equal(start, scope.Field<int>("_readerPendingSelectionStartOffset"));
            Assert.Equal(start + word.Length, scope.Field<int>("_readerPendingSelectionEndOffset"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            Assert.True(scope.Get<Border>("ReaderSelectionPopupBar").IsEffectivelyVisible);
            var selection = messages.Last(message => MessageType(message) == "selection");
            Assert.Equal(word, selection.GetProperty("text").GetString());
            Assert.True(selection.GetProperty("bottom").GetDouble() > selection.GetProperty("y").GetDouble());

            await WaitForDoubleClickWindow(host);
            if (host.IsPaginated)
                Assert.DoesNotContain(messages, message => MessageType(message) == "pageClick");
            Assert.Equal(0, host.CurrentPage);
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
        });

    [Theory]
    [InlineData("don't", 3)]
    [InlineData("well-known", 4)]
    public Task DoubleClickOnAnInternalConnectorSelectsTheWholeWord(string word, int characterIndex) =>
        WithReader(new ReaderLayoutSettings(), async (scope, host, _) =>
        {
            var start = host.BodyText!.IndexOf(word, StringComparison.Ordinal);
            var point = WordPoint(scope, host, start, characterIndex + 1);
            Click(scope.Window, point);
            Click(scope.Window, point);
            await ReaderTests.Render();

            Assert.Equal(word, scope.Field<string?>("_readerPendingSelection"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
        });

    [Theory]
    [InlineData("<p>first</p><p>second</p>", "first")]
    [InlineData("<p>first</p><p>second</p>", "second")]
    [InlineData("<p>first<br/>second</p>", "first")]
    [InlineData("<p>first<br/>second</p>", "second")]
    [InlineData("<p>'first'</p><p>second</p>", "first")]
    [InlineData("<p>Eng<em>li</em>sh, reader.</p>", "English")]
    public Task WordBoundariesRespectParagraphsAndLineBreaksButNotInlineEmphasis(string body, string word) =>
        WithReader(new ReaderLayoutSettings(), async (scope, host, _) =>
        {
            var start = host.BodyText!.IndexOf(word, StringComparison.Ordinal);
            var point = WordPoint(scope, host, start, word.Length);
            Click(scope.Window, point);
            Click(scope.Window, point);
            await ReaderTests.Render();

            Assert.Equal(word, scope.Field<string?>("_readerPendingSelection"));
            Assert.Equal(start, scope.Field<int>("_readerPendingSelectionStartOffset"));
            Assert.Equal(start + word.Length, scope.Field<int>("_readerPendingSelectionEndOffset"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
        }, body);

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    public Task DoubleClickInAPageTurnZoneDoesNotTurnThePage(string side) => WithReader(
        new ReaderLayoutSettings(), async (scope, host, messages) =>
        {
            Assert.True(host.TurnPage(1));
            await ReaderTests.Render();
            var page = host.CurrentPage;
            var point = WordPointInSide(scope, host, side);

            Click(scope.Window, point);
            Click(scope.Window, point);
            await WaitForDoubleClickWindow(host);

            Assert.Equal(page, host.CurrentPage);
            Assert.Equal("English", scope.Field<string?>("_readerPendingSelection"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            Assert.DoesNotContain(messages, message => MessageType(message) == "pageClick");
        });

    [Fact]
    public Task SingleClickOnTextStillTurnsAfterTheDoubleClickWindow() => WithReader(
        new ReaderLayoutSettings(), async (scope, host, messages) =>
        {
            var page = host.CurrentPage;
            Click(scope.Window, WordPointInSide(scope, host, "right"));

            Assert.Equal(page, host.CurrentPage);
            Assert.DoesNotContain(messages, message => MessageType(message) == "pageClick");
            await WaitForDoubleClickWindow(host);

            Assert.Equal(page + 1, host.CurrentPage);
            Assert.Single(messages, message => MessageType(message) == "pageClick");
            Assert.False(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
        });

    [Fact]
    public Task SingleClickOnBlankSpaceStillTurnsImmediately() => WithReader(
        new ReaderLayoutSettings(), async (scope, host, messages) =>
        {
            Assert.True(host.TurnPage(1));
            var point = host.TranslatePoint(new Point(4, 4), scope.Window)!.Value;
            Click(scope.Window, point);

            Assert.Single(messages, message => MessageType(message) == "pageClick");
            await ReaderTests.Render();
            Assert.Equal(0, host.CurrentPage);
        });

    [Fact]
    public Task DragOnTheSecondClickKeepsCharacterRangeSelection() => WithReader(
        new ReaderLayoutSettings(), async (scope, host, messages) =>
        {
            var start = host.BodyText!.IndexOf("English", StringComparison.Ordinal);
            var first = Layout(host).GetCharRect(0, start + 1)!.Value;
            var last = Layout(host).GetCharRect(0, start + 4)!.Value;
            var from = host.TranslatePoint(new Point(first.Left + 1, first.MidY), scope.Window)!.Value;
            var to = host.TranslatePoint(new Point(last.Right - 1, last.MidY), scope.Window)!.Value;

            Click(scope.Window, from);
            scope.Window.MouseDown(from, MouseButton.Left);
            scope.Window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            scope.Window.MouseUp(to, MouseButton.Left);
            await WaitForDoubleClickWindow(host);

            Assert.Equal("ngli", scope.Field<string?>("_readerPendingSelection"));
            Assert.True(scope.Get<Popup>("ReaderSelectionHostPopup").IsOpen);
            Assert.DoesNotContain(messages, message => MessageType(message) == "pageClick");
        });

    [Fact]
    public Task StoppingTheReaderCancelsADeferredTextClick() => WithReader(
        new ReaderLayoutSettings(), async (scope, host, messages) =>
        {
            Click(scope.Window, WordPointInSide(scope, host, "right"));
            host.Stop();
            await WaitForDoubleClickWindow(host);

            Assert.Equal(0, host.CurrentPage);
            Assert.DoesNotContain(messages, message => MessageType(message) == "pageClick");
        });

    private Task WithReader(ReaderLayoutSettings settings,
        Func<ReaderTestWindow, NativeReaderHost, List<JsonElement>, Task> action, string? body = null) =>
        session.Session.Dispatch(async () =>
        {
            await using var scope = await ReaderTestWindow.Create();
            using var host = new NativeReaderHost();
            var path = Path.Combine(scope.Paths.ReaderCache, "double-click.xhtml");
            var paragraph = "English, readers don't skip well-known! "
                + string.Join(" ", Enumerable.Repeat("English", 30));
            await File.WriteAllTextAsync(path, "<html><body>"
                + (body ?? string.Concat(Enumerable.Repeat("<p>" + paragraph + "</p>", 30)))
                + "</body></html>");
            scope.Set("_readerLayout", settings);
            scope.Set("_readerPageAnimation", 0);
            scope.Set("_readerDocument", new EpubReaderDocument(scope.Paths.ReaderCache, [path], [], []));
            scope.Set("_readerActiveHost", host);
            scope.Get<ContentControl>("ReaderActiveHostSlot").Content = host;
            scope.Get<Control>("LibraryRoot").IsVisible = false;
            scope.Get<Control>("ReaderRoot").IsVisible = true;
            scope.Call("ApplyReaderPanelLayout");
            await ReaderTests.Render();

            var messages = new List<JsonElement>();
            EventHandler<ReaderWebMessageReceivedEventArgs> handler = (_, args) =>
            {
                using var message = JsonDocument.Parse(args.Body!);
                messages.Add(message.RootElement.Clone());
                scope.Call("ReaderHost_WebMessageReceived", host, args);
            };
            host.WebMessageReceived += handler;
            try
            {
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(path), settings, false);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await ReaderTests.Render();
                messages.Clear();
                await action(scope, host, messages);
            }
            finally
            {
                host.WebMessageReceived -= handler;
                scope.Call("HideReaderSelectionPopup");
                scope.Get<ContentControl>("ReaderActiveHostSlot").Content = null;
                scope.Set("_readerActiveHost", null);
            }
            return true;
        }, CancellationToken.None);

    private static Point WordPoint(ReaderTestWindow scope, NativeReaderHost host, int start, int length)
    {
        var layout = Layout(host);
        var offset = start + length - 1;
        var page = layout.GetPageIndexOfOffset(offset);
        var rect = layout.GetCharRect(page, offset)!.Value;
        var origin = (Point)typeof(NativeReaderHost)
            .GetMethod("PageOrigin", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, [page])!;
        // The trailing half of the final glyph must select this word, not the
        // punctuation or space after the caret boundary.
        var local = host.Vertical
            ? new Point(rect.MidX, rect.Bottom - 1)
            : new Point(rect.Right - 1, rect.MidY);
        return host.TranslatePoint(origin + new Vector(local.X, local.Y), scope.Window)!.Value;
    }

    private static Point WordPointInSide(ReaderTestWindow scope, NativeReaderHost host, string side)
    {
        var page = Layout(host).Pages[host.CurrentPage];
        var text = host.BodyText!;
        for (var offset = text.IndexOf("English", page.TextStartOffset, StringComparison.Ordinal);
             offset >= 0 && offset + 7 <= page.TextEndOffset;
             offset = text.IndexOf("English", offset + 7, StringComparison.Ordinal))
        {
            var point = WordPoint(scope, host, offset, 7);
            var local = scope.Window.TranslatePoint(point, host)!.Value;
            if (side == "left" ? local.X < host.Bounds.Width / 3 : local.X > host.Bounds.Width * 2 / 3)
                return point;
        }
        throw new InvalidOperationException($"No English word in the {side} page-turn zone.");
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static async Task WaitForDoubleClickWindow(NativeReaderHost host)
    {
        var interval = host.GetPlatformSettings()?.GetDoubleTapTime(PointerType.Mouse)
            ?? TimeSpan.FromMilliseconds(400);
        await Task.Delay(interval + TimeSpan.FromMilliseconds(200));
        await ReaderTests.Render();
    }

    private static ChapterLayout Layout(NativeReaderHost host) => (ChapterLayout)typeof(NativeReaderHost)
        .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static string? MessageType(JsonElement message) => message.GetProperty("type").GetString();
}
