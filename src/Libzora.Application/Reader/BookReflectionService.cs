using Libzora.Core;
using Libzora.Infrastructure;

namespace Libzora.ApplicationServices;

/// <summary>
/// Orchestrates book-level reflection persistence and annotation citation loading.
/// </summary>
public sealed class BookReflectionService
{
    private readonly ReaderDataService _data;
    private readonly IBookLibraryService _library;

    public BookReflectionService(ReaderDataService data, IBookLibraryService library)
    {
        _data = data;
        _library = library;
    }

    public Task<ReaderBookReflection?> GetAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        _data.GetBookReflectionAsync(bookId, cancellationToken);

    public Task<IReadOnlyList<ReaderBookReflection>> GetAllAsync(
        CancellationToken cancellationToken = default,
        int limit = int.MaxValue) =>
        _data.GetAllBookReflectionsAsync(cancellationToken, limit);

    public async Task<ReaderBookReflection> SaveAsync(
        Guid bookId,
        string content,
        CancellationToken cancellationToken = default)
    {
        var existing = await _data.GetBookReflectionAsync(bookId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var reflection = new ReaderBookReflection
        {
            BookId = bookId,
            Content = (content ?? string.Empty).Trim(),
            CreatedAt = existing?.CreatedAt ?? now,
            UpdatedAt = now
        };
        await _data.SaveBookReflectionAsync(reflection, cancellationToken);
        return reflection;
    }

    public Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default) =>
        _data.DeleteBookReflectionAsync(bookId, cancellationToken);

    public async Task<IReadOnlyList<BookReflectionCitationRecord>> LoadCitationsAsync(
        string? currentBookTitle = null,
        CancellationToken cancellationToken = default)
    {
        var annotations = await _data.GetAllAnnotationsAsync(cancellationToken);
        if (annotations.Count == 0)
            return [];

        var bookIds = annotations.Select(annotation => annotation.BookId).Distinct().ToArray();
        var displayInfos = await _library.GetBookDisplayInfosAsync(bookIds, cancellationToken);
        var fallbackTitle = UiText.Get("已删除的本地书籍");
        var records = annotations.Select(annotation => new BookReflectionCitationRecord(
            annotation.Id,
            annotation.BookId,
            displayInfos.GetValueOrDefault(annotation.BookId)?.Title ?? fallbackTitle,
            BookReflectionMarkdown.ResolveCitationChapterLabel(annotation.ChapterPath),
            annotation.SelectedText,
            annotation.Note,
            annotation.UpdatedAt));
        return BookReflectionMarkdown.FilterCitations(records, query: null, currentBookTitle: currentBookTitle);
    }
}
