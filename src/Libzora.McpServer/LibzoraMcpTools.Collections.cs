using System.ComponentModel;
using Libzora.Core;
using Libzora.Infrastructure;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Libzora.McpServer;

/// <summary>
/// Collection queries and mutations.
/// </summary>
public sealed partial class LibzoraMcpTools
{
    /// <summary>Lists the library's real collection records and book counts.</summary>
    [McpServerTool(
        Name = "list_collections",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("List local Libzora collections with their IDs, creation times, and book counts.")]
    public async Task<CollectionListResult> ListCollectionsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await _library.GetCollectionSummariesAsync(cancellationToken);
        return new CollectionListResult(
            summaries.Select(summary => new CollectionInfo(
                summary.Collection.Id,
                summary.Collection.Name,
                summary.Collection.CreatedAt,
                summary.BookCount)).ToArray());
    }

    /// <summary>Creates a new library collection (收藏夹).</summary>
    [McpServerTool(
        Name = "create_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Create a new Libzora library collection (收藏夹).")]
    public async Task<CollectionOperationResult> CreateCollectionAsync(
        [Description("Name of the collection to create; must not be blank and at most 200 characters.")] string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = ValidateCollectionName(name);
        var collection = await RunLibraryMutationAsync(
            ct => _library.CreateCollectionAsync(normalizedName, ct),
            "Create collection",
            cancellationToken);
        return new CollectionOperationResult(
            collection.Id,
            collection.Name,
            true,
            [$"Created collection '{collection.Name}'."]);
    }

    /// <summary>Renames an existing library collection (收藏夹).</summary>
    [McpServerTool(
        Name = "rename_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Rename a Libzora library collection (收藏夹).")]
    public async Task<CollectionOperationResult> RenameCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        [Description("New collection name; must not be blank and at most 200 characters.")] string name,
        CancellationToken cancellationToken = default)
    {
        var (_, currentName) = await RequireCollectionAsync(collectionId, cancellationToken);
        var normalizedName = ValidateCollectionName(name);
        await RunLibraryMutationAsync(
            ct => _library.RenameCollectionAsync(collectionId, normalizedName, ct),
            "Rename collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            normalizedName,
            true,
            [$"Renamed collection '{currentName}' to '{normalizedName}'."]);
    }

    /// <summary>Deletes a library collection (收藏夹). The books inside are not deleted.</summary>
    [McpServerTool(
        Name = "delete_collection",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Delete a Libzora library collection (收藏夹); the books inside are kept and become uncollected.")]
    public async Task<CollectionOperationResult> DeleteCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var (_, name) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.DeleteCollectionAsync(collectionId, ct),
            "Delete collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            name,
            true,
            [$"Deleted collection '{name}'."]);
    }

    /// <summary>Empties a library collection without deleting the books themselves.</summary>
    [McpServerTool(
        Name = "clear_collection",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Remove every book from a Libzora collection; the collection itself and the books are kept.")]
    public async Task<CollectionOperationResult> ClearCollectionAsync(
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var (_, name) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.ClearCollectionAsync(collectionId, ct),
            "Clear collection",
            cancellationToken);
        return new CollectionOperationResult(
            collectionId,
            name,
            true,
            [$"Cleared collection '{name}'."]);
    }

    /// <summary>Moves every book of the source collection into the target one, then deletes the source.</summary>
    [McpServerTool(
        Name = "merge_collections",
        ReadOnly = false,
        Destructive = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Merge source collection into target collection (books move to the target) and delete the source.")]
    public async Task<CollectionOperationResult> MergeCollectionsAsync(
        [Description("Source collection GUID whose books are moved into the target; it is deleted afterwards.")] Guid sourceCollectionId,
        [Description("Target collection GUID that receives the books.")] Guid targetCollectionId,
        CancellationToken cancellationToken = default)
    {
        if (sourceCollectionId == targetCollectionId)
            throw new McpException("sourceCollectionId and targetCollectionId must be different.");
        var (_, sourceName) = await RequireCollectionAsync(sourceCollectionId, cancellationToken);
        var (_, targetName) = await RequireCollectionAsync(targetCollectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.MergeCollectionsAsync(sourceCollectionId, targetCollectionId, ct),
            "Merge collections",
            cancellationToken);
        return new CollectionOperationResult(
            targetCollectionId,
            targetName,
            true,
            [$"Merged collection '{sourceName}' into '{targetName}'."]);
    }

    /// <summary>Adds one library book to a collection (收藏夹).</summary>
    [McpServerTool(
        Name = "add_book_to_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Add one library book to a Libzora collection (收藏夹).")]
    public async Task<CollectionBookOperationResult> AddBookToCollectionAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var (_, collectionName) = await RequireCollectionAsync(collectionId, cancellationToken);
        if (collectionName.Equals(BookLibraryDefaults.UncollectedCollectionName, StringComparison.OrdinalIgnoreCase))
            throw new McpException("‘未收藏’是系统视图，不能直接添加。请移除这本书的所有自定义收藏夹成员关系。");
        await RunLibraryMutationAsync(
            ct => _library.AddBookToCollectionAsync(bookId, collectionId, ct),
            "Add book to collection",
            cancellationToken);
        return new CollectionBookOperationResult(
            bookId,
            collectionId,
            collectionName,
            true,
            [$"Added '{book.Title}' to collection '{collectionName}'."]);
    }

    /// <summary>Removes one library book from a collection (收藏夹).</summary>
    [McpServerTool(
        Name = "remove_book_from_collection",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Remove one library book from a Libzora collection (收藏夹); the book itself is kept.")]
    public async Task<CollectionBookOperationResult> RemoveBookFromCollectionAsync(
        [Description("Book GUID returned by list_library or search_books.")] Guid bookId,
        [Description("Collection GUID returned by list_collections.")] Guid collectionId,
        CancellationToken cancellationToken = default)
    {
        var book = await GetBookOrThrowAsync(bookId, cancellationToken);
        var (_, collectionName) = await RequireCollectionAsync(collectionId, cancellationToken);
        await RunLibraryMutationAsync(
            ct => _library.RemoveBookFromCollectionAsync(bookId, collectionId, ct),
            "Remove book from collection",
            cancellationToken);
        return new CollectionBookOperationResult(
            bookId,
            collectionId,
            collectionName,
            true,
            [$"Removed '{book.Title}' from collection '{collectionName}'."]);
    }
}
