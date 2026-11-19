using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Libzora.Core;

namespace Libzora.Infrastructure;

/// <summary>
/// Importing files and merging duplicates.
/// </summary>
public sealed partial class SqliteBookLibraryService
{
    public async Task<ImportBatchResult> ImportAsync(
        IEnumerable<string> paths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<ImportBookConflict, Task<ImportConflictResolution>>? conflictResolver = null,
        IReadOnlyDictionary<string, TxtImportRules>? txtImportRules = null)
    {
        DataRootMigrationState.ThrowIfActive(_paths);
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        var files = ExpandInputFiles(paths).ToList();
        var result = new ImportBatchResult();
        result.BookDetailsAvailable = files.Count <= DetailedImportResultLimit;
        var totalBytes = files.Sum(GetFileLengthSafe);
        long completedBytes = 0;
        var changed = false;
        // The uncollected collection id is stable for the whole import; query
        // it once instead of per imported book.
        Guid? defaultCollectionId = null;
        var defaultCollectionResolved = false;

        try
        {
            foreach (var sourcePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? createdFilePath = null;
                string? createdCoverPath = null;
                string? conversionDirectory = null;
                var committed = false;
                try
                {
                    var file = new FileInfo(sourcePath);
                    if (!file.Exists)
                        throw new FileNotFoundException("文件不存在", sourcePath);

                var importPath = sourcePath;
                if (Path.GetExtension(sourcePath).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    conversionDirectory = Path.Combine(Path.GetTempPath(), "Libzora-txt-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(conversionDirectory);
                    importPath = Path.Combine(conversionDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".epub");
                    progress?.Report(new TransferProgress(completedBytes, totalBytes, $"正在识别 TXT 章节：{file.Name}"));
                    var rules = txtImportRules is not null
                                && txtImportRules.TryGetValue(sourcePath, out var selectedRules)
                        ? selectedRules
                        : TxtImportRules.Default;
                    await TxtToEpubService.ConvertWithRulesAsync(sourcePath, importPath, rules, cancellationToken);
                }
                var hash = await Hashing.Sha256Async(importPath, cancellationToken);
                await using var connection = await OpenConnectionAsync(cancellationToken);
                var duplicate = await FindBookByHashAsync(connection, hash, cancellationToken);
                if (duplicate is not null)
                {
                    result.Items.Add(new ImportItemResult(
                        sourcePath,
                        true,
                        UiText.Get("《{0}》已在书库中，已跳过重复文件。", duplicate.Title),
                        result.BookDetailsAvailable ? duplicate : null,
                        BookId: duplicate.Id));
                    completedBytes += file.Length;
                    progress?.Report(new TransferProgress(completedBytes, totalBytes, UiText.Get("已跳过重复文件：{0}", file.Name)));
                    continue;
                }

                var metadata = await _metadata.ReadMetadataAsync(importPath, cancellationToken);
                var title = string.IsNullOrWhiteSpace(metadata.Title) ? Path.GetFileNameWithoutExtension(sourcePath) : metadata.Title.Trim();
                var authors = string.IsNullOrWhiteSpace(metadata.Authors) ? "未知作者" : metadata.Authors.Trim();
                Book? book = await FindBookByTitleAuthorsAsync(connection, title, authors, cancellationToken);
                if (book is not null && conflictResolver is not null)
                {
                    book.Files = await ReadFilesAsync(connection, book.Id, cancellationToken);
                    var resolution = await conflictResolver(new ImportBookConflict(
                        sourcePath,
                        book,
                        title,
                        authors,
                        Path.GetExtension(importPath).TrimStart('.').ToLowerInvariant()));
                    if (resolution == ImportConflictResolution.Skip)
                    {
                        result.Items.Add(new ImportItemResult(
                            sourcePath,
                            true,
                            "已跳过同名版本",
                            result.BookDetailsAvailable ? book : null,
                            Added: false,
                            BookId: book.Id));
                        completedBytes += file.Length;
                        progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已跳过 {file.Name}"));
                        continue;
                    }

                    if (resolution == ImportConflictResolution.KeepSeparate)
                        book = null;
                }
                var newBook = book is null;
                book ??= new Book
                {
                    Id = Guid.NewGuid(),
                    Title = title,
                    Authors = authors,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                book.Series ??= metadata.Series;
                book.SeriesIndex ??= metadata.SeriesIndex;
                book.Description ??= metadata.Description;
                book.UpdatedAt = DateTimeOffset.UtcNow;

                var bookDirectory = Path.Combine(_paths.Library, book.Id.ToString("N"));
                Directory.CreateDirectory(bookDirectory);
                var targetName = GetUniqueFileName(bookDirectory, Path.GetFileName(importPath));
                var targetPath = Path.Combine(bookDirectory, targetName);
                var temporaryPath = targetPath + $".{Guid.NewGuid():N}.part";
                try
                {
                    await CopyFileAsync(importPath, temporaryPath, new FileInfo(importPath).Length, completedBytes, totalBytes, conversionDirectory is null ? progress : null, cancellationToken);
                    File.Move(temporaryPath, targetPath, overwrite: false);
                    createdFilePath = targetPath;
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }

                // Resolve the uncollected collection before the write
                // transaction starts: a command executed on a connection with
                // a pending local transaction must carry that transaction.
                if (newBook && !defaultCollectionResolved)
                {
                    defaultCollectionId = await GetCollectionIdByNameAsync(
                        connection,
                        BookLibraryDefaults.UncollectedCollectionName,
                        cancellationToken);
                    defaultCollectionResolved = true;
                }

                // Publish the file, book row and collection membership together.
                // A failed insert must not leave an empty book in the library.
                using var transaction = connection.BeginTransaction();
                if (newBook)
                {
                    await InsertBookAsync(connection, book, cancellationToken, transaction);
                }
                else
                {
                    await UpdateBookRowAsync(connection, book, cancellationToken, transaction);
                }

                if (newBook)
                {
                    if (defaultCollectionId is not null)
                    {
                        var membership = connection.CreateCommand();
                        membership.Transaction = transaction;
                        membership.CommandText = """
                            INSERT OR IGNORE INTO BookCollectionItems (CollectionId, BookId, AddedAt)
                            VALUES ($collectionId, $bookId, $addedAt);
                            """;
                        membership.Parameters.AddWithValue("$collectionId", defaultCollectionId.Value.ToString());
                        membership.Parameters.AddWithValue("$bookId", book.Id.ToString());
                        membership.Parameters.AddWithValue("$addedAt", DateTimeOffset.UtcNow.ToString("O"));
                        await membership.ExecuteNonQueryAsync(cancellationToken);
                        book.CollectionIds.Add(defaultCollectionId.Value);
                    }
                }

                var relativePath = Path.GetRelativePath(_paths.Data, targetPath);
                var bookFile = new BookFile
                {
                    Id = Guid.NewGuid(),
                    BookId = book.Id,
                    Format = Path.GetExtension(importPath).TrimStart('.').ToLowerInvariant(),
                    RelativePath = relativePath,
                    Size = new FileInfo(targetPath).Length,
                    Sha256 = hash
                };
                await InsertFileAsync(connection, bookFile, cancellationToken, transaction);
                book.Files.Add(bookFile);

                if (metadata.CoverBytes is { Length: > 0 } && string.IsNullOrWhiteSpace(book.CoverPath))
                {
                    var coverName = $"{book.Id:N}{NormalizeCoverExtension(metadata.CoverExtension)}";
                    var coverPath = Path.Combine(_paths.Covers, coverName);
                    if (!File.Exists(coverPath)) createdCoverPath = coverPath;
                    await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                    book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                    await UpdateBookRowAsync(connection, book, cancellationToken, transaction);
                }

                transaction.Commit();
                committed = true;
                changed = true;
                result.Items.Add(new ImportItemResult(
                    sourcePath,
                    true,
                    newBook ? "已导入" : "已添加新格式",
                    result.BookDetailsAvailable ? book : null,
                    Added: true,
                    BookId: book.Id));
                completedBytes += file.Length;
                progress?.Report(new TransferProgress(completedBytes, totalBytes, $"已导入 {file.Name}"));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Items.Add(new ImportItemResult(sourcePath, false, ex.Message, null));
                }
                finally
                {
                    if (conversionDirectory is not null)
                        TryDeleteFile(Path.Combine(conversionDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".epub"));
                    if (conversionDirectory is not null && Directory.Exists(conversionDirectory))
                    {
                        try { Directory.Delete(conversionDirectory); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                    if (!committed)
                    {
                        if (createdFilePath is not null) TryDeleteFile(createdFilePath);
                        if (createdCoverPath is not null) TryDeleteFile(createdCoverPath);
                    }
                }
            }

            return result;
        }
        finally
        {
            // A large import must not wake the window/S3 debounce pipeline once
            // per file. Partial imports still publish one final notification.
            if (changed) NotifyDataChanged();
        }
    }
}
