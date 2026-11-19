using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Library trash: move, restore, purge and staging recovery.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public async Task<IReadOnlyList<LibraryTrashItem>> GetTrashItemsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Kind, BookId, Title, Format, Size, DeletedAt
            FROM LibraryTrash
            ORDER BY DeletedAt DESC;
            """;
        var items = new List<LibraryTrashItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id)
                || !Guid.TryParse(reader.GetString(2), out var bookId)
                || !Enum.IsDefined(typeof(LibraryTrashItemKind), reader.GetInt32(1)))
                continue;

            items.Add(new LibraryTrashItem(
                id,
                (LibraryTrashItemKind)reader.GetInt32(1),
                bookId,
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5),
                DateTimeOffset.Parse(reader.GetString(6))));
        }
        return items;
    }

    public async Task RestoreTrashItemAsync(
        Guid trashItemId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var entry = await ReadTrashEntryAsync(connection, trashItemId, cancellationToken)
                ?? throw new FileNotFoundException("回收站项目不存在，可能已被其他设备处理。");

            if (entry.Kind == LibraryTrashItemKind.Book)
                await RestoreBookFromTrashAsync(connection, entry, cancellationToken);
            else
                await RestoreFileFromTrashAsync(connection, entry, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task PurgeTrashItemAsync(
        Guid trashItemId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var entry = await ReadTrashEntryAsync(connection, trashItemId, cancellationToken)
                ?? throw new FileNotFoundException("回收站项目不存在，可能已被其他设备处理。");

            var stagingDirectory = GetPurgeStagingDirectory(entry.Id);
            if (Directory.Exists(stagingDirectory))
            {
                await RecoverPurgeStagingDirectoryAsync(connection, stagingDirectory, cancellationToken);
                if (Directory.Exists(stagingDirectory))
                    throw new IOException("上一次永久清理的暂存内容无法自动恢复。");
            }

            var bodySource = ResolveDataPath(entry.TrashPath);
            var coverSource = string.IsNullOrWhiteSpace(entry.TrashCoverPath)
                ? null
                : ResolveDataPath(entry.TrashCoverPath);
            var manifest = new LibraryTrashPurgeManifest
            {
                TrashItemId = entry.Id,
                TrashPath = entry.TrashPath,
                TrashCoverPath = entry.TrashCoverPath,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var stagedBody = GetPurgeStagedBodyPath(stagingDirectory);
            var stagedCover = coverSource is null ? null : GetPurgeStagedCoverPath(stagingDirectory, coverSource);
            var bodyMoved = false;
            var coverMoved = false;

            try
            {
                Directory.CreateDirectory(stagingDirectory);
                await WritePurgeManifestAsync(stagingDirectory, manifest, cancellationToken);
                bodyMoved = MovePath(bodySource, stagedBody);
                if (coverSource is not null && stagedCover is not null)
                    coverMoved = MovePath(coverSource, stagedCover);
            }
            catch
            {
                RestoreStagedPurgePath(stagedCover, coverSource, "封面", coverMoved);
                RestoreStagedPurgePath(stagedBody, bodySource, "书籍内容", bodyMoved);
                TryDeletePurgeStagingDirectory(stagingDirectory, "准备永久清理失败后的回滚");
                throw;
            }

            try
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    if (entry.Kind == LibraryTrashItemKind.Book)
                    {
                        if (ShouldDeleteAnnotationsWhenDeletingBook()
                            && await TableExistsAsync(connection, transaction, "ReaderAnnotations", cancellationToken))
                            await DeletePurgeReaderRowsAsync(
                                connection,
                                transaction,
                                "DELETE FROM ReaderAnnotations WHERE BookId = $bookId;",
                                "$bookId",
                                entry.BookId,
                                cancellationToken);
                        if (await TableExistsAsync(connection, transaction, "ReaderBookReflections", cancellationToken))
                            await DeletePurgeReaderRowsAsync(
                                connection,
                                transaction,
                                "DELETE FROM ReaderBookReflections WHERE BookId = $bookId;",
                                "$bookId",
                                entry.BookId,
                                cancellationToken);
                    }
                    else if (entry.BookFileId is { } purgedFileId
                        && await TableExistsAsync(connection, transaction, "ReaderAnnotations", cancellationToken))
                    {
                        await DeletePurgeReaderRowsAsync(
                            connection,
                            transaction,
                            "DELETE FROM ReaderAnnotations WHERE BookFileId = $bookFileId;",
                            "$bookFileId",
                            purgedFileId,
                            cancellationToken);
                    }

                    var delete = connection.CreateCommand();
                    delete.Transaction = transaction;
                    delete.CommandText = "DELETE FROM LibraryTrash WHERE Id = $id;";
                    delete.Parameters.AddWithValue("$id", trashItemId.ToString());
                    await delete.ExecuteNonQueryAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    try
                    {
                        await transaction.RollbackAsync(CancellationToken.None);
                    }
                    catch (Exception rollbackException)
                    {
                        LogLibraryTrashRecovery("永久清理数据库回滚失败。", rollbackException);
                    }
                    throw;
                }
            }
            catch
            {
                var coverRestored = RestoreStagedPurgePath(stagedCover, coverSource, "封面", coverMoved);
                var bodyRestored = RestoreStagedPurgePath(stagedBody, bodySource, "书籍内容", bodyMoved);
                if (coverRestored && bodyRestored)
                    TryDeletePurgeStagingDirectory(stagingDirectory, "永久清理失败后的回滚");
                throw;
            }

            TryDeletePurgeStagingDirectory(stagingDirectory, "永久清理完成后的清理");
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    private async Task MoveBookToTrashAsync(
        SqliteConnection connection,
        Book book,
        CancellationToken cancellationToken)
    {
        var trashId = Guid.NewGuid();
        var retainedCoverPath = PreserveReadingMaterialCover(book.Id, book.CoverPath);
        var trashDirectory = Path.Combine(_paths.Trash, "books", trashId.ToString("N"));
        var originalDirectory = Path.Combine(_paths.Library, book.Id.ToString("N"));
        var trashPath = Path.GetRelativePath(_paths.Data, trashDirectory);
        var originalPath = Path.GetRelativePath(_paths.Data, originalDirectory);
        var trashCoverPath = string.IsNullOrWhiteSpace(book.CoverPath)
            ? null
            : Path.GetRelativePath(
                _paths.Data,
                Path.Combine(_paths.Trash, "covers", trashId.ToString("N"), "cover" + Path.GetExtension(book.CoverPath)));
        var originalCoverPath = book.CoverPath;

        try
        {
            MovePath(originalDirectory, trashDirectory);
            if (!string.IsNullOrWhiteSpace(originalCoverPath))
            {
                var originalCover = ResolveDataPath(originalCoverPath);
                var movedCover = ResolveDataPath(trashCoverPath!);
                MovePath(originalCover, movedCover);
            }
        }
        catch
        {
            MovePathBack(trashDirectory, originalDirectory);
            if (!string.IsNullOrWhiteSpace(trashCoverPath))
                MovePathBack(ResolveDataPath(trashCoverPath), ResolveDataPath(originalCoverPath!));
            throw;
        }

        var entry = new TrashEntry
        {
            Id = trashId,
            Kind = LibraryTrashItemKind.Book,
            BookId = book.Id,
            Title = book.Title,
            Format = null,
            Size = book.Files.Sum(file => Math.Max(0, file.Size)),
            DeletedAt = DateTimeOffset.UtcNow,
            BookJson = JsonSerializer.Serialize(book, TrashJsonOptions),
            TrashPath = trashPath,
            OriginalPath = originalPath,
            TrashCoverPath = trashCoverPath,
            OriginalCoverPath = originalCoverPath
        };

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertTrashEntryAsync(connection, transaction, entry, cancellationToken);
                if (ShouldDeleteAnnotationsWhenDeletingBook()
                    && await TableExistsAsync(connection, transaction, "ReaderAnnotations", cancellationToken))
                {
                    var annotations = connection.CreateCommand();
                    annotations.Transaction = transaction;
                    annotations.CommandText = "DELETE FROM ReaderAnnotations WHERE BookId = $id;";
                    annotations.Parameters.AddWithValue("$id", book.Id.ToString());
                    await annotations.ExecuteNonQueryAsync(cancellationToken);
                }

                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Books WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", book.Id.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken);
                using var retainedCover = connection.CreateCommand();
                retainedCover.Transaction = transaction;
                retainedCover.CommandText = "UPDATE ReadingMaterialBooks SET CoverPath=$cover WHERE Id=$id;";
                retainedCover.Parameters.AddWithValue("$id", book.Id.ToString());
                retainedCover.Parameters.AddWithValue("$cover", (object?)retainedCoverPath ?? DBNull.Value);
                await retainedCover.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(trashDirectory, originalDirectory);
            if (!string.IsNullOrWhiteSpace(trashCoverPath))
                MovePathBack(ResolveDataPath(trashCoverPath), ResolveDataPath(originalCoverPath!));
            throw;
        }
    }

    private async Task MoveFileToTrashAsync(
        SqliteConnection connection,
        Book book,
        BookFile file,
        CancellationToken cancellationToken)
    {
        var trashId = Guid.NewGuid();
        var originalPath = file.RelativePath;
        var sourcePath = ResolveDataPath(originalPath);
        var trashDirectory = Path.Combine(_paths.Trash, "files", trashId.ToString("N"));
        var trashFilePath = Path.Combine(trashDirectory, SanitizeFileName(Path.GetFileName(originalPath)));
        var trashPath = Path.GetRelativePath(_paths.Data, trashFilePath);
        MovePath(sourcePath, trashFilePath);

        var entry = new TrashEntry
        {
            Id = trashId,
            Kind = LibraryTrashItemKind.File,
            BookId = book.Id,
            BookFileId = file.Id,
            Title = book.Title,
            Format = file.Format,
            Size = file.Size,
            DeletedAt = DateTimeOffset.UtcNow,
            FileJson = JsonSerializer.Serialize(file, TrashJsonOptions),
            TrashPath = trashPath,
            OriginalPath = originalPath
        };

        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertTrashEntryAsync(connection, transaction, entry, cancellationToken);
                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM BookFiles WHERE Id = $id AND BookId = $bookId;";
                delete.Parameters.AddWithValue("$id", file.Id.ToString());
                delete.Parameters.AddWithValue("$bookId", book.Id.ToString());
                await delete.ExecuteNonQueryAsync(cancellationToken);

                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$bookId", book.Id.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch
        {
            MovePathBack(trashFilePath, sourcePath);
            throw;
        }
    }

    private async Task RestoreBookFromTrashAsync(
        SqliteConnection connection,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var book = JsonSerializer.Deserialize<Book>(entry.BookJson ?? string.Empty, TrashJsonOptions)
            ?? throw new InvalidDataException("回收站中的书籍记录无法读取。");
        book.Files ??= [];
        book.CollectionIds ??= [];
        if (await BookExistsAsync(connection, book.Id, cancellationToken))
            throw new InvalidOperationException("同 ID 的书籍已经存在，无法恢复。");
        foreach (var file in book.Files)
        {
            if (await FindFileByHashAsync(connection, file.Sha256, cancellationToken) is not null)
                throw new InvalidOperationException($"恢复失败：文件 {file.Format.ToUpperInvariant()} 已存在于书库中。");
        }

        var sourceDirectory = ResolveDataPath(entry.TrashPath);
        var targetDirectory = ResolveDataPath(entry.OriginalPath);
        var targetCover = string.IsNullOrWhiteSpace(entry.OriginalCoverPath)
            ? null
            : ResolveDataPath(entry.OriginalCoverPath);
        var sourceCover = string.IsNullOrWhiteSpace(entry.TrashCoverPath)
            ? null
            : ResolveDataPath(entry.TrashCoverPath);
        EnsureMoveTargetAvailable(targetDirectory);
        if (targetCover is not null) EnsureMoveTargetAvailable(targetCover);

        var directoryMoved = false;
        var coverMoved = false;
        try
        {
            directoryMoved = MovePath(sourceDirectory, targetDirectory);
            if (sourceCover is not null && targetCover is not null)
                coverMoved = MovePath(sourceCover, targetCover);
            book.UpdatedAt = DateTimeOffset.UtcNow;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertBookAsync(connection, book, cancellationToken, transaction);
                foreach (var file in book.Files)
                    await InsertFileAsync(connection, file, cancellationToken, transaction);
                foreach (var collectionId in book.CollectionIds)
                    await RestoreCollectionMembershipAsync(connection, transaction, collectionId, book.Id, cancellationToken);
                if (await GetCollectionIdByNameAsync(
                        connection,
                        BookLibraryDefaults.UncollectedCollectionName,
                        cancellationToken,
                        transaction) is { } uncollectedId)
                {
                    await NormalizeBookCollectionMembershipAsync(
                        connection,
                        transaction,
                        uncollectedId,
                        book.Id,
                        cancellationToken);
                }
                await DeleteTrashEntryAsync(connection, transaction, entry.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    LogLibraryTrashRecovery("恢复书籍的数据库回滚失败。", rollbackException);
                }
                throw;
            }
        }
        catch
        {
            RollbackMovedPath(targetCover, sourceCover, coverMoved, "恢复书籍封面");
            RollbackMovedPath(targetDirectory, sourceDirectory, directoryMoved, "恢复书籍内容");
            throw;
        }
    }

    private async Task RestoreFileFromTrashAsync(
        SqliteConnection connection,
        TrashEntry entry,
        CancellationToken cancellationToken)
    {
        var file = JsonSerializer.Deserialize<BookFile>(entry.FileJson ?? string.Empty, TrashJsonOptions)
            ?? throw new InvalidDataException("回收站中的文件记录无法读取。");
        if (!await BookExistsAsync(connection, file.BookId, cancellationToken))
            throw new InvalidOperationException("原书籍已经不存在，无法恢复此格式。");
        if (await FindFileByHashAsync(connection, file.Sha256, cancellationToken) is not null)
            throw new InvalidOperationException("相同文件已经在书库中，无法重复恢复。");

        var sourcePath = ResolveDataPath(entry.TrashPath);
        var targetPath = ResolveDataPath(entry.OriginalPath);
        EnsureMoveTargetAvailable(targetPath);
        var fileMoved = false;

        try
        {
            fileMoved = MovePath(sourcePath, targetPath);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await InsertFileAsync(connection, file, cancellationToken, transaction);
                var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$bookId", file.BookId.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
                await DeleteTrashEntryAsync(connection, transaction, entry.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    LogLibraryTrashRecovery("恢复格式文件的数据库回滚失败。", rollbackException);
                }
                throw;
            }
        }
        catch
        {
            RollbackMovedPath(targetPath, sourcePath, fileMoved, "恢复格式文件");
            throw;
        }
    }

    private static async Task RestoreCollectionMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid collectionId,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT $collectionId, $bookId, $addedAt
            WHERE EXISTS(SELECT 1 FROM BookCollections WHERE Id = $collectionId);
            """;
        command.Parameters.AddWithValue("$collectionId", collectionId.ToString());
        command.Parameters.AddWithValue("$bookId", bookId.ToString());
        command.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> NormalizeBookCollectionMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid uncollectedCollectionId,
        Guid bookId,
        CancellationToken cancellationToken)
    {
        var changed = 0;
        var removeStaleUncollected = connection.CreateCommand();
        removeStaleUncollected.Transaction = transaction;
        removeStaleUncollected.CommandText = """
            DELETE FROM BookCollectionItems
            WHERE CollectionId = $uncollectedCollectionId
              AND BookId = $bookId
              AND EXISTS (
                  SELECT 1
                  FROM BookCollectionItems custom
                  WHERE custom.BookId = $bookId
                    AND custom.CollectionId <> $uncollectedCollectionId
              );
            """;
        removeStaleUncollected.Parameters.AddWithValue("$uncollectedCollectionId", uncollectedCollectionId.ToString());
        removeStaleUncollected.Parameters.AddWithValue("$bookId", bookId.ToString());
        changed += await removeStaleUncollected.ExecuteNonQueryAsync(cancellationToken);

        var addUncollected = connection.CreateCommand();
        addUncollected.Transaction = transaction;
        addUncollected.CommandText = """
            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
            SELECT $uncollectedCollectionId, $bookId, $addedAt
            WHERE EXISTS(SELECT 1 FROM Books WHERE Id = $bookId)
              AND NOT EXISTS (
                  SELECT 1
                  FROM BookCollectionItems custom
                  WHERE custom.BookId = $bookId
                    AND custom.CollectionId <> $uncollectedCollectionId
              );
            """;
        addUncollected.Parameters.AddWithValue("$uncollectedCollectionId", uncollectedCollectionId.ToString());
        addUncollected.Parameters.AddWithValue("$bookId", bookId.ToString());
        addUncollected.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
        changed += await addUncollected.ExecuteNonQueryAsync(cancellationToken);
        return changed;
    }
}
