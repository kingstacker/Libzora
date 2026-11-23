using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Adding files, renaming, metadata and deletion.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public async Task<BookFile> AddFileToBookAsync(
        Guid bookId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("待添加的书籍文件不存在。", source);

        var format = BookFormatConversionPolicy.Normalize(Path.GetExtension(source));
        if (!BookFormatConversionPolicy.IsConvertibleFormat(format))
            throw new NotSupportedException("书库只允许添加 EPUB、AZW3、PDF 或 MOBI 格式。");

        var fileInfo = new FileInfo(source);
        var hash = await Hashing.Sha256Async(source, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            if (!await BookExistsAsync(connection, bookId, cancellationToken))
                throw new InvalidOperationException("目标书籍已不存在，请刷新书库后重试。 ");

            if (await FindFileByHashAsync(connection, hash, cancellationToken) is not null)
                throw new InvalidOperationException("相同文件已经在书库中。 ");

            var bookDirectory = Path.Combine(_paths.Library, bookId.ToString("N"));
            Directory.CreateDirectory(bookDirectory);
            var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(source));
            var targetPath = Path.Combine(bookDirectory, targetName);
            var temporaryPath = targetPath + ".part";
            var targetCreated = false;
            var fileRowCreated = false;
            try
            {
                await CopyFileAsync(
                    source,
                    temporaryPath,
                    fileInfo.Length,
                    completed: 0,
                    total: fileInfo.Length,
                    progress: null,
                    cancellationToken);
                File.Move(temporaryPath, targetPath, true);
                targetCreated = true;

                var touch = connection.CreateCommand();
                touch.CommandText = "UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;";
                touch.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                touch.Parameters.AddWithValue("$bookId", bookId.ToString());
                await touch.ExecuteNonQueryAsync(cancellationToken);

                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = bookId,
                    Format = format,
                    RelativePath = Path.GetRelativePath(_paths.Data, targetPath),
                    Size = fileInfo.Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken);
                fileRowCreated = true;
                NotifyDataChanged();
                return bookFile;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    TryDeleteFile(temporaryPath);
                if (targetCreated && !fileRowCreated)
                    TryDeleteFile(targetPath);
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    /// <summary>
    /// Renames one file inside a book. Generated editions use this after the
    /// preceding version is retired, so their canonical file name (for
    /// example a trailing "_pinyin") is restored without a temporary suffix.
    /// </summary>
    public async Task RenameFileAsync(
        Guid bookId,
        Guid bookFileId,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken)
                ?? throw new InvalidOperationException("目标书籍已不存在，请刷新书库后重试。");
            var file = book.Files.FirstOrDefault(item => item.Id == bookFileId)
                ?? throw new FileNotFoundException("书籍文件不存在。");
            var currentPath = ResolveDataPath(file.RelativePath);
            var directory = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("书籍文件目录无效。");
            var safeName = SanitizeFileName(fileName);
            var targetPath = Path.Combine(directory, safeName);
            if (string.Equals(
                    Path.GetFullPath(currentPath),
                    Path.GetFullPath(targetPath),
                    StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(targetPath))
                throw new IOException($"同名文件已存在：{safeName}");

            File.Move(currentPath, targetPath);
            try
            {
                var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE BookFiles SET RelativePath = $relativePath WHERE Id = $id;
                    UPDATE Books SET UpdatedAt = $updatedAt WHERE Id = $bookId;
                    """;
                update.Parameters.AddWithValue("$relativePath", Path.GetRelativePath(_paths.Data, targetPath));
                update.Parameters.AddWithValue("$id", bookFileId.ToString());
                update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$bookId", bookId.ToString());
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            catch
            {
                TryMoveFileBack(targetPath, currentPath);
                throw;
            }
            NotifyDataChanged();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static void TryMoveFileBack(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async Task<string?> EnsurePdfCoverAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _pdfCoverGate.WaitAsync(cancellationToken);
        try
        {
            var book = await GetBookAsync(bookId, cancellationToken);
            if (book is null) return null;
            if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(ResolveDataPath(book.CoverPath))) return book.CoverPath;
            var file = book.Files.FirstOrDefault(item => string.Equals(item.Format, "pdf", StringComparison.OrdinalIgnoreCase));
            if (file is null || !File.Exists(GetAbsoluteFilePath(file))) return null;
            var metadata = await _metadata.ReadMetadataAsync(GetAbsoluteFilePath(file), cancellationToken);
            if (metadata.CoverBytes is not { Length: > 0 }) return null;
            var path = Path.Combine(_paths.Covers, $"{book.Id:N}-pdf.png");
            var temporary = path + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(temporary, metadata.CoverBytes, cancellationToken);
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            var relative = Path.GetRelativePath(_paths.Data, path);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var update = connection.CreateCommand();
            // Do not overwrite a cover chosen while the first page rendered,
            // or rewrite other metadata from an old UI snapshot.
            update.CommandText = "UPDATE Books SET CoverPath=$cover, UpdatedAt=$updatedAt WHERE Id=$id AND CoverPath IS $previous;";
            update.Parameters.AddWithValue("$cover", relative);
            update.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", bookId.ToString());
            update.Parameters.AddWithValue("$previous", (object?)book.CoverPath ?? DBNull.Value);
            return await update.ExecuteNonQueryAsync(cancellationToken) > 0 ? relative : null;
        }
        finally { _pdfCoverGate.Release(); }
    }

    public async Task RenameBookAsync(
        Guid bookId,
        string title,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length == 0)
            throw new ArgumentException("书名不能为空。", nameof(title));
        if (normalizedTitle.Length > 240)
            throw new ArgumentException("书名不能超过 240 个字符。", nameof(title));

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken)
                ?? throw new FileNotFoundException("指定书籍不存在。", bookId.ToString());
            var plan = BuildBookRenamePlan(book, normalizedTitle);
            var titleChanged = !string.Equals(book.Title, normalizedTitle, StringComparison.Ordinal);
            if (!titleChanged && !plan.Any(item => !string.Equals(
                    item.SourcePath,
                    item.TargetPath,
                    StringComparison.Ordinal)))
            {
                return;
            }

            var moves = new List<FileRenameState>();
            try
            {
                MoveFilesToTemporaryPaths(plan, moves, cancellationToken);
                CompleteFileMoves(moves, cancellationToken);

                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
                var updatedAt = DateTimeOffset.UtcNow;
                using (var updateBook = connection.CreateCommand())
                {
                    updateBook.Transaction = transaction;
                    updateBook.CommandText = "UPDATE Books SET Title = $title, UpdatedAt = $updatedAt WHERE Id = $bookId;";
                    updateBook.Parameters.AddWithValue("$title", normalizedTitle);
                    updateBook.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
                    updateBook.Parameters.AddWithValue("$bookId", bookId.ToString());
                    if (await updateBook.ExecuteNonQueryAsync(cancellationToken) == 0)
                        throw new InvalidOperationException("书籍已不存在，请刷新书库后重试。");
                }

                foreach (var item in plan.Where(item => !string.Equals(
                             item.File.RelativePath,
                             item.TargetRelativePath,
                             StringComparison.Ordinal)))
                {
                    using var updateFile = connection.CreateCommand();
                    updateFile.Transaction = transaction;
                    updateFile.CommandText = """
                        UPDATE BookFiles
                        SET RelativePath = $relativePath
                        WHERE Id = $fileId AND BookId = $bookId;
                        """;
                    updateFile.Parameters.AddWithValue("$relativePath", item.TargetRelativePath);
                    updateFile.Parameters.AddWithValue("$fileId", item.File.Id.ToString());
                    updateFile.Parameters.AddWithValue("$bookId", bookId.ToString());
                    if (await updateFile.ExecuteNonQueryAsync(cancellationToken) == 0)
                        throw new InvalidOperationException("书籍文件记录已不存在，请刷新书库后重试。");
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                RestoreFileMoves(moves);
                throw;
            }

            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task UpdateMetadataAsync(Book book, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        book.UpdatedAt = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await UpdateBookRowAsync(connection, book, cancellationToken);
        NotifyDataChanged();
    }

    public async Task DeleteFileAsync(
        Guid bookId,
        Guid bookFileId,
        CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken)
                ?? throw new FileNotFoundException("指定书籍不存在。");
            var file = book.Files.FirstOrDefault(item => item.Id == bookFileId)
                ?? throw new FileNotFoundException("指定书籍格式不存在。");

            if (book.Files.Count <= 1)
                await MoveBookToTrashAsync(connection, book, cancellationToken);
            else
                await MoveFileToTrashAsync(connection, book, file, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task DeleteAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var book = await ReadBookByIdAsync(connection, bookId, cancellationToken);
            if (book is null)
                return;

            await MoveBookToTrashAsync(connection, book, cancellationToken);
            NotifyDataChanged();
        }
        finally { _databaseGate.Release(); }
    }

    public async Task<BookBatchDeleteResult> DeleteBatchAsync(
        IReadOnlyList<Guid> bookIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bookIds);
        if (bookIds.Count == 0)
            return new BookBatchDeleteResult([], []);

        var uniqueIds = bookIds.Distinct().ToArray();
        var deletedIds = new List<Guid>(uniqueIds.Length);
        var failures = new List<BookBatchDeleteFailure>();

        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            try
            {
                foreach (var bookId in uniqueIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var book = await ReadBookByIdAsync(connection, bookId, cancellationToken);
                    if (book is null)
                        continue;

                    try
                    {
                        await MoveBookToTrashAsync(connection, book, cancellationToken);
                        deletedIds.Add(book.Id);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new BookBatchDeleteFailure(book.Id, book.Title, exception.Message));
                    }
                }
            }
            finally
            {
                if (deletedIds.Count > 0)
                    NotifyDataChanged();
            }
        }
        finally
        {
            _databaseGate.Release();
        }

        return new BookBatchDeleteResult(deletedIds, failures);
    }

    public async Task RemoveFromLibraryKeepFilesAsync(Guid bookId, CancellationToken cancellationToken = default)
    {
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var deleted = false;
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                if (ShouldDeleteAnnotationsWhenDeletingBook()
                    && await TableExistsAsync(connection, transaction, "ReaderAnnotations", cancellationToken))
                {
                    var annotations = connection.CreateCommand();
                    annotations.Transaction = transaction;
                    annotations.CommandText = "DELETE FROM ReaderAnnotations WHERE BookId = $id;";
                    annotations.Parameters.AddWithValue("$id", bookId.ToString());
                    deleted |= await annotations.ExecuteNonQueryAsync(cancellationToken) > 0;
                }

                var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM Books WHERE Id = $id;";
                delete.Parameters.AddWithValue("$id", bookId.ToString());
                deleted |= await delete.ExecuteNonQueryAsync(cancellationToken) > 0;
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
            if (deleted)
                NotifyDataChanged();
            // The library directory and saved cover are deliberately left on disk.
        }
        finally { _databaseGate.Release(); }
    }
}
