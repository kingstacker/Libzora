using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.Infrastructure;

/// <summary>
/// Constants, construction and schema initialisation.
/// </summary>
public sealed partial class ReaderDataService
{
    private const int CurrentTextExtractionVersion = 2;
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    internal ReaderRecoveryContext? CachedRecoveryContext { get; private set; }
    private bool _ftsAvailable;
    private bool _ftsUsesTrigram;

    public event EventHandler<LocalDataChangedEventArgs>? DataChanged;

    public ReaderDataService(AppPaths paths)
    {
        _paths = paths;
    }

    private void NotifyDataChanged(LocalDataChangeKind kind)
    {
        try
        {
            DataChanged?.Invoke(this, new LocalDataChangedEventArgs(kind));
        }
        catch
        {
            // Persistence must remain successful even if a UI observer is
            // already shutting down or otherwise unable to accept a notice.
        }
    }

    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = _paths.Database,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private,
        DefaultTimeout = 30
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        using var processLease = await AppDataProcessLock.AcquireAsync(_paths, cancellationToken);
        await AppBackupService.RecoverPendingImportsAsync(_paths, cancellationToken);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ReaderAnnotations (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    SelectedText TEXT NOT NULL,
                    Prefix TEXT NOT NULL DEFAULT '',
                    Suffix TEXT NOT NULL DEFAULT '',
                    Color TEXT NOT NULL DEFAULT '#000000',
                    UnderlineStyle TEXT NOT NULL DEFAULT 'solid',
                    Note TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderAnnotations_BookFile
                    ON ReaderAnnotations(BookFileId, ChapterPath, StartOffset);
                CREATE INDEX IF NOT EXISTS IX_ReaderAnnotations_UpdatedAt
                    ON ReaderAnnotations(UpdatedAt DESC);

                CREATE TABLE IF NOT EXISTS ReaderBookReflections (
                    BookId TEXT PRIMARY KEY,
                    Content TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderBookReflections_UpdatedAt
                    ON ReaderBookReflections(UpdatedAt);

                CREATE TABLE IF NOT EXISTS BookContentChunks (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    ChapterIndex INTEGER NOT NULL,
                    ChunkIndex INTEGER NOT NULL,
                    ChapterTitle TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    StartOffset INTEGER NOT NULL,
                    EndOffset INTEGER NOT NULL,
                    Content TEXT NOT NULL,
                    TextExtractionVersion INTEGER NOT NULL DEFAULT 1
                );
                CREATE UNIQUE INDEX IF NOT EXISTS UX_BookContentChunks_Position
                    ON BookContentChunks(BookFileId, SourceHash, ChapterIndex, ChunkIndex);
                CREATE INDEX IF NOT EXISTS IX_BookContentChunks_Book
                    ON BookContentChunks(BookId, ChapterIndex, ChunkIndex);

                -- Progressive search indexes one chapter at a time. The state
                -- row makes a half-built index distinguishable from a complete
                -- legacy one; the chapter rows let an interrupted build resume
                -- instead of re-extracting every chapter.
                CREATE TABLE IF NOT EXISTS BookContentIndexStates (
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    TextExtractionVersion INTEGER NOT NULL,
                    TotalChapters INTEGER NOT NULL DEFAULT 0,
                    IsComplete INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (BookFileId, SourceHash, TextExtractionVersion)
                );
                CREATE TABLE IF NOT EXISTS BookContentIndexChapters (
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    TextExtractionVersion INTEGER NOT NULL,
                    ChapterIndex INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (BookFileId, SourceHash, TextExtractionVersion, ChapterIndex)
                );

                CREATE TABLE IF NOT EXISTS BookChunkEmbeddings (
                    ChunkId INTEGER PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    SourceHash TEXT NOT NULL,
                    Embedding BLOB NOT NULL,
                    EmbeddingModel TEXT NOT NULL,
                    EmbeddingDimension INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_BookChunkEmbeddings_Book
                    ON BookChunkEmbeddings(BookId, BookFileId, EmbeddingModel, EmbeddingDimension);

                CREATE TABLE IF NOT EXISTS ReaderProgress (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    ScrollPosition INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    ContentPositionJson TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderProgress_UpdatedAt
                    ON ReaderProgress(UpdatedAt DESC);

                CREATE TABLE IF NOT EXISTS ReaderBookmarks (
                    Id TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ChapterPath TEXT NOT NULL,
                    Fragment TEXT NULL,
                    ChapterIndex INTEGER NOT NULL DEFAULT 0,
                    ScrollPosition INTEGER NULL,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    ContentPositionJson TEXT NULL,
                    Title TEXT NOT NULL DEFAULT '',
                    Quote TEXT NOT NULL DEFAULT '',
                    CreatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderBookmarks_BookFile
                    ON ReaderBookmarks(BookFileId, ChapterIndex, CreatedAt);

                CREATE TABLE IF NOT EXISTS ReaderLayoutSettings (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    FontScale REAL NOT NULL DEFAULT 1.0,
                    LineHeight REAL NOT NULL DEFAULT 1.88,
                    MaxWidth REAL NOT NULL DEFAULT 800,
                    BodyPadding REAL NOT NULL DEFAULT 68,
                    FontFamily TEXT NULL,
                    FlowMode INTEGER NOT NULL DEFAULT 0,
                    VerticalWriting INTEGER NOT NULL DEFAULT 0,
                    TwoPageMode INTEGER NOT NULL DEFAULT 0,
                    ParagraphIndent INTEGER NOT NULL DEFAULT 1,
                    UseBookOverride INTEGER NOT NULL DEFAULT 0,
                    AppearanceTheme INTEGER NOT NULL DEFAULT 0,
                    AppearancePaperEnabled INTEGER NOT NULL DEFAULT 0,
                    AppearancePaperStrength REAL NOT NULL DEFAULT 0.45,
                    AppearanceFibersEnabled INTEGER NOT NULL DEFAULT 0,
                    ProfileUpdatedAt TEXT NULL,
                    LayoutUpdatedAt TEXT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ReaderLayoutSettings_UpdatedAt
                    ON ReaderLayoutSettings(UpdatedAt DESC);

                CREATE TABLE IF NOT EXISTS ReaderReadingStats (
                    BookFileId TEXT PRIMARY KEY,
                    BookId TEXT NOT NULL,
                    CumulativeSeconds INTEGER NOT NULL DEFAULT 0,
                    ListeningSeconds INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    CompletedChapters INTEGER NOT NULL DEFAULT 0,
                    TotalChapters INTEGER NOT NULL DEFAULT 0,
                    UpdatedAt TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ReaderReadingSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    BookId TEXT NOT NULL,
                    BookFileId TEXT NOT NULL,
                    ActiveSeconds INTEGER NOT NULL,
                    ListeningSeconds INTEGER NOT NULL DEFAULT 0,
                    ProgressPercent REAL NOT NULL DEFAULT 0,
                    RecordedAt TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_ReaderReadingSessions_RecordedAt
                    ON ReaderReadingSessions(RecordedAt);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            // Older builds installed these triggers for reading badges. Stop
            // tracking completions while retaining existing stored data.
            using (var retiredTriggers = connection.CreateCommand())
            {
                retiredTriggers.CommandText = """
                    DROP TRIGGER IF EXISTS ReaderCompletions_Status;
                    DROP TRIGGER IF EXISTS ReaderCompletions_Insert;
                    DROP TRIGGER IF EXISTS ReaderCompletions_FileDelete;
                    """;
                await retiredTriggers.ExecuteNonQueryAsync(cancellationToken);
            }
            await EnsureReaderLayoutTwoPageColumnAsync(connection, cancellationToken);
            await EnsureReaderLayoutParagraphIndentColumnAsync(connection, cancellationToken);
            await EnsureReaderLayoutProfileColumnsAsync(connection, cancellationToken);
            await EnsureReaderAnnotationStyleColumnAsync(connection, cancellationToken);
            await EnsureReaderBookmarkPositionColumnsAsync(connection, cancellationToken);
            await EnsureReaderContentPositionColumnsAsync(connection, cancellationToken);
            await EnsureTextExtractionVersionColumnAsync(connection, cancellationToken);
            await ReaderSyncedPositionMarkerStore.EnsureSchemaAsync(connection, null, cancellationToken);
            await ReaderAnnotationCascade.EnsureAsync(connection, cancellationToken);
            await ReaderReadingHistory.EnsureAsync(connection, cancellationToken);

            await EnsureListeningTimeColumnsAsync(connection, cancellationToken);
            await EnsureCompletionColumnAsync(connection, cancellationToken);
            await using (var syncTransaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                await ReadingTimeSyncTracker.EnsureSchemaAsync(connection, syncTransaction, cancellationToken);
                await ReaderRecoveryEpoch.EnsureAsync(connection, syncTransaction, cancellationToken);
                CachedRecoveryContext = await ReaderRecoveryEpoch.ReadAsync(connection, syncTransaction, cancellationToken);
                await syncTransaction.CommitAsync(cancellationToken);
            }

            await EnsureListeningTimeColumnsAsync(connection, cancellationToken);

            _ftsAvailable = await EnsureFullTextIndexAsync(connection, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }
}
