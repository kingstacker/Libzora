using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Libzora.ApplicationServices;
using Libzora.Core;
using Libzora.Infrastructure;
using Libzora.Mobile.Views;
using Libzora.Mobile.Views.Sheets;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Libzora.Mobile.Tests;

[Collection("Mobile UI")]
public sealed class BookReflectionMobileTests(MobileUiSession session)
{
    [Fact]
    public Task BookDetailDisplaysSavesFormatsCitesAndDeletesReflection() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-reflection-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        var services = new MobileServices(paths, protector, new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource());
        using var modules = MobileComposition.Create(services);
        var book = await SeedBookAsync(modules, "测试书", "chapter1.xhtml", "值得记住的原话", "随手批注");
        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths))
        {
            NativeConfirmation = (_, _, _) => Task.FromResult(true)
        };
        var window = new Window { Width = 411, Height = 880, Content = shell };

        try
        {
            window.Show();
            shell.OpenBook(book);
            await Until(() => shell.CurrentPage is BookDetailPage);
            var detail = Assert.IsType<BookDetailPage>(shell.CurrentPage);
            await Until(() => detail.FindControl<TextBlock>("ReflectionPreviewText")?.Text == "还没有写下读后思考。");

            Click(detail.FindControl<Button>("EditReflectionButton")!);
            await Until(() => shell.CurrentPage is BookReflectionPage);
            var editor = Assert.IsType<BookReflectionPage>(shell.CurrentPage);
            await Until(() => editor.FindControl<Button>("SaveButton")!.IsEnabled);

            var box = editor.FindControl<TextBox>("EditorBox")!;
            box.Text = "第一段感想";
            box.CaretIndex = box.Text.Length;

            // Format current line as H1 and insert citation from existing annotation.
            await Until(() => editor.GetVisualDescendants().OfType<Button>().Any(button => button.Tag as string == "h1"));
            var h1 = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Tag as string == "h1");
            Click(h1);
            Assert.Equal("# 第一段感想", box.Text);

            var cite = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == "引用批注");
            Click(cite);
            await Until(() => shell.CurrentSheet is BookReflectionCitationSheet);
            var sheet = Assert.IsType<BookReflectionCitationSheet>(shell.CurrentSheet);
            await Until(() => sheet.GetVisualDescendants().OfType<Button>().Any());
            var citationButton = sheet.GetVisualDescendants().OfType<Button>().First();
            Click(citationButton);
            await Until(() => shell.CurrentSheet is null);
            Assert.Contains("> 值得记住的原话", box.Text, StringComparison.Ordinal);
            Assert.Contains("--《测试书》", box.Text, StringComparison.Ordinal);

            // Toggle preview and back, then save.
            Click(editor.FindControl<Button>("PreviewToggleButton")!);
            Assert.True(editor.FindControl<ScrollViewer>("PreviewScroller")!.IsVisible);
            Click(editor.FindControl<Button>("PreviewToggleButton")!);
            Click(editor.FindControl<Button>("SaveButton")!);

            await Until(() => shell.CurrentPage is BookDetailPage);
            await Until(() => detail.FindControl<TextBlock>("ReflectionPreviewText")!.Text!.Contains("第一段感想", StringComparison.Ordinal));
            var persisted = await modules.Reader.Reflections.GetAsync(book.Id);
            Assert.NotNull(persisted);
            Assert.Contains("# 第一段感想", persisted!.Content, StringComparison.Ordinal);

            // Reopen editor and delete reflection.
            Click(detail.FindControl<Button>("EditReflectionButton")!);
            await Until(() => shell.CurrentPage is BookReflectionPage);
            editor = Assert.IsType<BookReflectionPage>(shell.CurrentPage);
            await Until(() => editor.FindControl<Button>("SaveButton")!.IsEnabled);
            var deleteItem = editor.FindControl<Button>("MoreButton")!.Flyout is MenuFlyout flyout
                ? flyout.Items.OfType<MenuItem>().Single()
                : throw new InvalidOperationException("Missing flyout");
            deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

            await Until(() => shell.CurrentPage is BookDetailPage);
            await Until(() => detail.FindControl<TextBlock>("ReflectionPreviewText")?.Text == "还没有写下读后思考。");
            Assert.Null(await modules.Reader.Reflections.GetAsync(book.Id));
        }
        finally
        {
            window.Close();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task MaterialsPageShowsFiltersSortsAndExportsBookReflections() => session.Session.Dispatch(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "libzora-mobile-materials-reflection-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var protector = new TestProtector();
        var share = new CaptureShare();
        var services = new MobileServices(paths, protector, new BookMetadataService(),
            new UnavailableBookFormatConverter(), new NoFileImportSource(), FileShare: share);
        using var modules = MobileComposition.Create(services);
        var book = await SeedBookAsync(modules, "思考之书", "ch1.xhtml", "原文划线", "");
        await modules.Reader.Reflections.SaveAsync(book.Id, "# 总结\n\n**关键启发**\n\n![图](data:image/png;base64,AAA=)");

        var shell = new MobileShell(modules, services, new MobilePreferencesStore(paths));
        var window = new Window { Width = 411, Height = 880, Content = shell };
        try
        {
            window.Show();
            shell.ShowMaterials();
            await Until(() => shell.CurrentPage is MaterialsPage);
            var page = Assert.IsType<MaterialsPage>(shell.CurrentPage);
            var list = page.FindControl<ItemsControl>("MaterialsList")!;
            await Until(() => list.ItemsSource is IReadOnlyList<MaterialBookGroup> groups && groups.Count == 1);

            var groups = Assert.IsAssignableFrom<IReadOnlyList<MaterialBookGroup>>(list.ItemsSource);
            var entries = groups[0].Entries;
            Assert.Equal(2, entries.Count);
            Assert.Equal(MaterialKind.Reflection, entries[0].Kind);
            Assert.Equal(MaterialKind.Highlight, entries[1].Kind);
            Assert.DoesNotContain("data:image", entries[0].DisplayNote, StringComparison.Ordinal);

            // Filter by reflections only.
            Click(page.FindControl<Button>("FilterReflections")!);
            groups = Assert.IsAssignableFrom<IReadOnlyList<MaterialBookGroup>>(list.ItemsSource);
            Assert.Single(groups[0].Entries);
            Assert.Equal(MaterialKind.Reflection, groups[0].Entries[0].Kind);

            // Export filtered reflection to Markdown.
            Click(page.FindControl<Button>("ExportButton")!);
            await Until(() => shell.CurrentSheet is ChoiceSheet);
            var exportSheet = Assert.IsType<ChoiceSheet>(shell.CurrentSheet);
            await Until(() => exportSheet.GetVisualDescendants().OfType<Button>().Any());
            var mdButton = exportSheet.GetVisualDescendants().OfType<Button>().First();
            Click(mdButton);
            await Until(() => share.Captured is not null);
            Assert.Contains("### 读后思考", share.Captured, StringComparison.Ordinal);
            Assert.Contains("# 总结", share.Captured, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public void SharedMarkdownHelpersNormalizeStripAndCiteUniformly()
    {
        var wrapped = "\"# 标题\\n\\n**重点**\\n\\n![img](data:image/png;base64,abc =80x80)\"";
        Assert.Equal("# 标题\n\n**重点**\n\n![img](data:image/png;base64,abc =80x80)",
            BookReflectionMarkdown.NormalizeMarkdown(wrapped));
        Assert.Equal("# 标题\n\n**重点**", BookReflectionMarkdown.RemoveImagesForPreview(wrapped));
        Assert.Equal("标题", BookReflectionMarkdown.GetFirstPreviewLine(wrapped));
        Assert.Equal(4, BookReflectionMarkdown.CountCharacters(wrapped));
        Assert.Equal("> 摘录\n>\n> --《测试书》",
            BookReflectionMarkdown.FormatCitationQuoteBlock("摘录", "测试书"));
        Assert.Equal("第 3 页", BookReflectionMarkdown.ResolveCitationChapterLabel("pdf:page:3"));
    }

    private static async Task<Book> SeedBookAsync(
        ApplicationModules modules,
        string title,
        string chapterPath,
        string selectedText,
        string note)
    {
        await modules.Library.Books.InitializeAsync();
        await modules.Reader.Data.InitializeAsync();
        var sourcePath = Path.Combine(modules.Paths.Data, $"{Guid.NewGuid():N}.txt");
        Directory.CreateDirectory(modules.Paths.Data);
        await File.WriteAllTextAsync(sourcePath, "第一章\n测试正文。");
        var import = await modules.Library.Books.ImportAsync([sourcePath]);
        var bookId = import.Items.Single().BookId!.Value;
        await modules.Library.Books.RenameBookAsync(bookId, title);
        var book = (await modules.Library.Books.GetBookAsync(bookId))!;
        var fileId = book.Files.Single().Id;
        await modules.Reader.Data.SaveAnnotationAsync(new ReaderAnnotation
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            BookFileId = fileId,
            ChapterPath = chapterPath,
            StartOffset = 10,
            EndOffset = 20,
            SelectedText = selectedText,
            Note = note,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return book;
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    private sealed class CaptureShare : IMobileFileShare
    {
        public string? Captured { get; private set; }
        public Task<bool> ShareAsync(string fileName, byte[] content)
        {
            Captured = Encoding.UTF8.GetString(content);
            return Task.FromResult(true);
        }
        public Task<bool> ShareFileAsync(string path) => throw new NotSupportedException();
    }
}
