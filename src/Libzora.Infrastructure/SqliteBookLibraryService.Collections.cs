using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Collection create, delete, dissolve and membership.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public async Task<IReadOnlyList<BookCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, CreatedAt, UpdatedAt FROM BookCollections ORDER BY Name COLLATE NOCASE;";
        var collections = new List<BookCollection>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            collections.Add(new BookCollection
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(2)),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(3))
            });
        }
        return collections;
    }

    public async Task<BookCollection> CreateCollectionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("收藏夹名称不能为空。", nameof(name));
        if (normalizedName.Length > 60)
            throw new ArgumentException("收藏夹名称不能超过 60 个字符。", nameof(name));

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existing = connection.CreateCommand();
            existing.CommandText = "SELECT EXISTS(SELECT 1 FROM BookCollections WHERE Name = $name COLLATE NOCASE);";
            existing.Parameters.AddWithValue("$name", normalizedName);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken)) != 0)
                throw new InvalidOperationException("同名收藏夹已经存在。");

            var collection = new BookCollection
            {
                Id = Guid.NewGuid(),
                Name = normalizedName,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO BookCollections (Id, Name, CreatedAt, UpdatedAt) VALUES ($id, $name, $createdAt, $updatedAt);";
            insert.Parameters.AddWithValue("$id", collection.Id.ToString());
            insert.Parameters.AddWithValue("$name", collection.Name);
            insert.Parameters.AddWithValue("$createdAt", collection.CreatedAt.ToString("O"));
            insert.Parameters.AddWithValue("$updatedAt", collection.UpdatedAt.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            NotifyDataChanged();
            return collection;
        }
        finally { _databaseGate.Release(); }
    }

    public Task DeleteCollectionAsync(
        Guid collectionId,
        CancellationToken cancellationToken = default) =>
        DissolveCollectionAsync(collectionId, cancellationToken);

    public async Task RenameCollectionAsync(
        Guid collectionId,
        string name,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        var normalizedName = (name ?? string.Empty).Trim();
        if (normalizedName.Length == 0)
            throw new ArgumentException("收藏夹名称不能为空。", nameof(name));
        if (normalizedName.Length > 60)
            throw new ArgumentException("收藏夹名称不能超过 60 个字符。", nameof(name));

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var defaultCollectionId = await GetCollectionIdByNameAsync(
                    connection,
                    BookLibraryDefaults.UncollectedCollectionName,
                    cancellationToken,
                    transaction);
                if (defaultCollectionId == collectionId)
                    throw new InvalidOperationException("未收藏收藏夹不能重命名。");

                var duplicate = connection.CreateCommand();
                duplicate.Transaction = transaction;
                duplicate.CommandText = """
                    SELECT EXISTS(
                        SELECT 1
                        FROM BookCollections
                        WHERE Name = $name COLLATE NOCASE
                          AND Id <> $collectionId
                    );
                    """;
                duplicate.Parameters.AddWithValue("$name", normalizedName);
                duplicate.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                if (Convert.ToInt64(await duplicate.ExecuteScalarAsync(cancellationToken)) != 0)
                    throw new InvalidOperationException("同名收藏夹已经存在。");

                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE BookCollections SET Name = $name, UpdatedAt = $updatedAt WHERE Id = $collectionId;";
                update.Parameters.AddWithValue("$name", normalizedName);
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                    throw new InvalidOperationException("收藏夹已不存在。");

                await transaction.CommitAsync(cancellationToken);
                NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }

    public async Task ClearCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var defaultCollectionId = await GetCollectionIdByNameAsync(
                    connection,
                    BookLibraryDefaults.UncollectedCollectionName,
                    cancellationToken,
                    transaction);
                if (defaultCollectionId == collectionId)
                    throw new InvalidOperationException("未收藏收藏夹不能清空。");

                var collectionExists = connection.CreateCommand();
                collectionExists.Transaction = transaction;
                collectionExists.CommandText = "SELECT EXISTS(SELECT 1 FROM BookCollections WHERE Id = $collectionId);";
                collectionExists.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                if (Convert.ToInt64(await collectionExists.ExecuteScalarAsync(cancellationToken)) == 0)
                    throw new InvalidOperationException("收藏夹已不存在。");

                var affectedBookIds = new List<Guid>();
                var affectedBooks = connection.CreateCommand();
                affectedBooks.Transaction = transaction;
                affectedBooks.CommandText = "SELECT BookId FROM BookCollectionItems WHERE CollectionId = $collectionId;";
                affectedBooks.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                await using (var reader = await affectedBooks.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (Guid.TryParse(reader.GetString(0), out var bookId))
                            affectedBookIds.Add(bookId);
                    }
                }

                var deleteItems = connection.CreateCommand();
                deleteItems.Transaction = transaction;
                deleteItems.CommandText = "DELETE FROM BookCollectionItems WHERE CollectionId = $collectionId;";
                deleteItems.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                var deletedItems = await deleteItems.ExecuteNonQueryAsync(cancellationToken);

                if (defaultCollectionId is { } uncollectedId)
                {
                    foreach (var bookId in affectedBookIds.Distinct())
                        await NormalizeBookCollectionMembershipAsync(
                            connection,
                            transaction,
                            uncollectedId,
                            bookId,
                            cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                if (deletedItems > 0)
                    NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }

    public async Task DissolveCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var defaultCollectionId = await GetCollectionIdByNameAsync(
                    connection,
                    BookLibraryDefaults.UncollectedCollectionName,
                    cancellationToken,
                    transaction);
                if (defaultCollectionId == collectionId)
                    throw new InvalidOperationException("未收藏收藏夹不能解散。");

                var affectedBookIds = new List<Guid>();
                var affectedBooks = connection.CreateCommand();
                affectedBooks.Transaction = transaction;
                affectedBooks.CommandText = "SELECT BookId FROM BookCollectionItems WHERE CollectionId = $id;";
                affectedBooks.Parameters.AddWithValue("$id", collectionId.ToString());
                await using (var reader = await affectedBooks.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (Guid.TryParse(reader.GetString(0), out var bookId))
                            affectedBookIds.Add(bookId);
                    }
                }

                var deleteItems = connection.CreateCommand();
                deleteItems.Transaction = transaction;
                deleteItems.CommandText = "DELETE FROM BookCollectionItems WHERE CollectionId = $id;";
                deleteItems.Parameters.AddWithValue("$id", collectionId.ToString());
                var deletedItems = await deleteItems.ExecuteNonQueryAsync(cancellationToken);

                var deleteCollection = connection.CreateCommand();
                deleteCollection.Transaction = transaction;
                deleteCollection.CommandText = "DELETE FROM BookCollections WHERE Id = $id;";
                deleteCollection.Parameters.AddWithValue("$id", collectionId.ToString());
                var deletedCollections = await deleteCollection.ExecuteNonQueryAsync(cancellationToken);

                if (deletedCollections > 0 && defaultCollectionId is { } uncollectedId)
                {
                    foreach (var bookId in affectedBookIds.Distinct())
                        await NormalizeBookCollectionMembershipAsync(
                            connection,
                            transaction,
                            uncollectedId,
                            bookId,
                            cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                if (deletedItems > 0 || deletedCollections > 0)
                    NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }

    public async Task MergeCollectionsAsync(
        Guid sourceCollectionId,
        Guid targetCollectionId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        if (sourceCollectionId == targetCollectionId)
            throw new InvalidOperationException("收藏夹不能合并到自身。");

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var defaultCollectionId = await GetCollectionIdByNameAsync(
                    connection,
                    BookLibraryDefaults.UncollectedCollectionName,
                    cancellationToken,
                    transaction);
                if (defaultCollectionId is null
                    || sourceCollectionId == defaultCollectionId
                    || targetCollectionId == defaultCollectionId)
                    throw new InvalidOperationException("未收藏收藏夹不能参与合并。");

                var existingCollections = connection.CreateCommand();
                existingCollections.Transaction = transaction;
                existingCollections.CommandText = """
                    SELECT COUNT(*)
                    FROM BookCollections
                    WHERE Id IN ($sourceCollectionId, $targetCollectionId);
                    """;
                existingCollections.Parameters.AddWithValue("$sourceCollectionId", sourceCollectionId.ToString());
                existingCollections.Parameters.AddWithValue("$targetCollectionId", targetCollectionId.ToString());
                if (Convert.ToInt64(await existingCollections.ExecuteScalarAsync(cancellationToken)) != 2)
                    throw new InvalidOperationException("要合并的收藏夹已不存在。");

                var sourceBookIds = new List<Guid>();
                var affectedBooks = connection.CreateCommand();
                affectedBooks.Transaction = transaction;
                affectedBooks.CommandText = "SELECT BookId FROM BookCollectionItems WHERE CollectionId = $sourceCollectionId;";
                affectedBooks.Parameters.AddWithValue("$sourceCollectionId", sourceCollectionId.ToString());
                await using (var reader = await affectedBooks.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        if (Guid.TryParse(reader.GetString(0), out var bookId))
                            sourceBookIds.Add(bookId);
                    }
                }

                var copyItems = connection.CreateCommand();
                copyItems.Transaction = transaction;
                copyItems.CommandText = """
                    INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                    SELECT $targetCollectionId, BookId, AddedAt
                    FROM BookCollectionItems
                    WHERE CollectionId = $sourceCollectionId;
                    """;
                copyItems.Parameters.AddWithValue("$targetCollectionId", targetCollectionId.ToString());
                copyItems.Parameters.AddWithValue("$sourceCollectionId", sourceCollectionId.ToString());
                var copiedItems = await copyItems.ExecuteNonQueryAsync(cancellationToken);

                var deleteItems = connection.CreateCommand();
                deleteItems.Transaction = transaction;
                deleteItems.CommandText = "DELETE FROM BookCollectionItems WHERE CollectionId = $sourceCollectionId;";
                deleteItems.Parameters.AddWithValue("$sourceCollectionId", sourceCollectionId.ToString());
                var deletedItems = await deleteItems.ExecuteNonQueryAsync(cancellationToken);

                var deleteCollection = connection.CreateCommand();
                deleteCollection.Transaction = transaction;
                deleteCollection.CommandText = "DELETE FROM BookCollections WHERE Id = $sourceCollectionId;";
                deleteCollection.Parameters.AddWithValue("$sourceCollectionId", sourceCollectionId.ToString());
                var deletedCollections = await deleteCollection.ExecuteNonQueryAsync(cancellationToken);

                foreach (var bookId in sourceBookIds.Distinct())
                    await NormalizeBookCollectionMembershipAsync(
                        connection,
                        transaction,
                        defaultCollectionId.Value,
                        bookId,
                        cancellationToken);

                await transaction.CommitAsync(cancellationToken);
                if (copiedItems > 0 || deletedItems > 0 || deletedCollections > 0)
                    NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }

    public Task AddBookToCollectionAsync(
        Guid bookId,
        Guid collectionId,
        CancellationToken cancellationToken = default) =>
        SetBookCollectionMembershipAsync(bookId, collectionId, add: true, cancellationToken);

    public Task RemoveBookFromCollectionAsync(
        Guid bookId,
        Guid collectionId,
        CancellationToken cancellationToken = default) =>
        SetBookCollectionMembershipAsync(bookId, collectionId, add: false, cancellationToken);

    private async Task SetBookCollectionMembershipAsync(
        Guid bookId,
        Guid collectionId,
        bool add,
        CancellationToken cancellationToken)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var defaultCollectionId = await GetCollectionIdByNameAsync(
                    connection,
                    BookLibraryDefaults.UncollectedCollectionName,
                    cancellationToken,
                    transaction)
                    ?? throw new InvalidOperationException("未收藏收藏夹不存在。");

                var exists = connection.CreateCommand();
                exists.Transaction = transaction;
                exists.CommandText = """
                    SELECT EXISTS(SELECT 1 FROM BookCollections WHERE Id = $collectionId)
                       AND EXISTS(SELECT 1 FROM Books WHERE Id = $bookId);
                    """;
                exists.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                exists.Parameters.AddWithValue("$bookId", bookId.ToString());
                if (add && Convert.ToInt64(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                    throw new InvalidOperationException("书籍或收藏夹已不存在。");

                var changed = 0;
                if (add && collectionId == defaultCollectionId)
                {
                    var clearCustom = connection.CreateCommand();
                    clearCustom.Transaction = transaction;
                    clearCustom.CommandText = """
                        DELETE FROM BookCollectionItems
                        WHERE BookId = $bookId AND CollectionId <> $defaultCollectionId;
                        """;
                    clearCustom.Parameters.AddWithValue("$bookId", bookId.ToString());
                    clearCustom.Parameters.AddWithValue("$defaultCollectionId", defaultCollectionId.ToString());
                    changed += await clearCustom.ExecuteNonQueryAsync(cancellationToken);
                }
                else if (add)
                {
                    var addMembership = connection.CreateCommand();
                    addMembership.Transaction = transaction;
                    addMembership.CommandText = """
                        INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                        VALUES ($collectionId, $bookId, $addedAt);
                        """;
                    addMembership.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                    addMembership.Parameters.AddWithValue("$bookId", bookId.ToString());
                    addMembership.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
                    changed += await addMembership.ExecuteNonQueryAsync(cancellationToken);
                }
                else if (collectionId != defaultCollectionId)
                {
                    var removeMembership = connection.CreateCommand();
                    removeMembership.Transaction = transaction;
                    removeMembership.CommandText = "DELETE FROM BookCollectionItems WHERE CollectionId = $collectionId AND BookId = $bookId;";
                    removeMembership.Parameters.AddWithValue("$collectionId", collectionId.ToString());
                    removeMembership.Parameters.AddWithValue("$bookId", bookId.ToString());
                    changed += await removeMembership.ExecuteNonQueryAsync(cancellationToken);
                }

                changed += await NormalizeBookCollectionMembershipAsync(
                    connection,
                    transaction,
                    defaultCollectionId,
                    bookId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (changed > 0)
                    NotifyDataChanged();
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally { _databaseGate.Release(); }
    }
}
