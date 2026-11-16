using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Layout;
using Libzora.Mobile.Views;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class ReaderLongPressSelectionTests(MobileUiSession session)
{
    [Fact]
    public Task LongPressSelectsOneCharacterAndReleaseKeepsTheToolbar() =>
        session.Session.Dispatch(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "libzora-reader-long-press-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var paths = new AppPaths(root);
            var services = new MobileServices(paths, new TestProtector(), new BookMetadataService(),
                new UnavailableBookFormatConverter(), new NoFileImportSource());
            using var modules = MobileComposition.Create(services);
            var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
            var reader = new ReaderPage(new Book { Id = Guid.NewGuid(), Title = "长按选择回归" }, modules, shell);
            reader.UseNativeFrontend();
            typeof(ReaderPage).GetField("_started", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(reader, true);

            using var host = new NativeReaderHost { Focusable = false };
            var handler = typeof(ReaderPage)
                .GetMethod("OnReaderWebMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<EventHandler<ReaderWebMessageReceivedEventArgs>>(reader);
            host.WebMessageReceived += handler;
            typeof(ReaderPage).GetField("_readerHost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(reader, host);
            reader.FindControl<ContentControl>("ReaderHostSlot")!.Content = host;
            var states = new List<NativeReaderState>();
            reader.NativeStateChanged += states.Add;
            var window = new Window { Width = 390, Height = 844, Content = reader };
            try
            {
                var chapterPath = Path.Combine(root, "chapter.xhtml");
                await File.WriteAllTextAsync(chapterPath,
                    "<html><body><p>中文长按只选一个字，English words stay whole.</p></body></html>");
                window.Show();
                await Render();

                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                host.NavigationCompleted += (_, args) => { if (args.IsSuccess) ready.TrySetResult(); };
                host.Navigate(new Uri(chapterPath));
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await Render();

                var layout = (ChapterLayout)typeof(NativeReaderHost)
                    .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var bodyText = host.BodyText!;
                var offset = bodyText.IndexOf('中');
                Assert.True(offset >= 0);
                var page = layout.GetPageIndexOfOffset(offset);
                var rect = layout.GetCharRect(page, offset);
                Assert.NotNull(rect);

                // Press just past the glyph's midpoint: the long press must
                // still select 中, not the caret boundary before 文.
                var point = host.TranslatePoint(
                    new Point(rect.Value.Right - 1, (rect.Value.Top + rect.Value.Bottom) / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                typeof(ReaderPage).GetMethod("OnLongPress", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(reader, [null, EventArgs.Empty]);
                await Render();

                var selected = states.LastOrDefault(state => state.SelectedText is not null);
                Assert.NotNull(selected);
                Assert.Equal("中", selected.SelectedText);
                Assert.NotNull(selected.SelectionBounds);
                Assert.True(selected.SelectionBounds!.X >= 0);

                window.MouseUp(point, MouseButton.Left);
                await Render();
                Assert.Equal("中", states[^1].SelectedText);

                var selectionStart = (int)typeof(NativeReaderHost)
                    .GetField("_selectionStart", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                var selectionEnd = (int)typeof(NativeReaderHost)
                    .GetField("_selectionEnd", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
                Assert.Equal((offset, offset + 1), (selectionStart, selectionEnd));

                // A tap outside the selection dismisses it and must not turn
                // the page or open the reader chrome.
                window.MouseDown(new Point(4, 4), MouseButton.Left);
                window.MouseUp(new Point(4, 4), MouseButton.Left);
                await Render();
                Assert.Null(states[^1].SelectedText);
            }
            finally
            {
                reader.NativeStateChanged -= states.Add;
                host.WebMessageReceived -= handler;
                window.Close();
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, CancellationToken.None);

    private static async Task Render()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }
}
