using System.Security.Cryptography;
using System.Text;
using Libzora.Core;
using Microsoft.Data.Sqlite;

namespace Libzora.AccountServer;

public sealed class AccountDatabase
{
    private const int PasswordIterations = 600_000;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly byte[] _dummySalt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _dummyHash = RandomNumberGenerator.GetBytes(32);
    public AccountRegion Region { get; }

    public AccountDatabase(string directory, TimeProvider clock, AccountRegion region = AccountRegion.China)
    {
        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(directory, "accounts.db"), ForeignKeys = true, DefaultTimeout = 30 }.ToString();
        _clock = clock;
        Region = region;
    }

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync(CancellationToken.None);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Accounts (
                Id TEXT PRIMARY KEY, Username TEXT NOT NULL, NormalizedUsername TEXT NOT NULL UNIQUE,
                Salt BLOB NOT NULL, PasswordHash BLOB NOT NULL, CreatedAt INTEGER NOT NULL,
                Email TEXT, NormalizedEmail TEXT);
            CREATE TABLE IF NOT EXISTS AccountSessions (
                TokenHash BLOB PRIMARY KEY, AccountId TEXT NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_AccountSessions_Expiry ON AccountSessions(ExpiresAt);
            CREATE TABLE IF NOT EXISTS Feedback (
                Id TEXT PRIMARY KEY, Number INTEGER NOT NULL UNIQUE,
                UserId TEXT NOT NULL REFERENCES Accounts(Id) ON DELETE CASCADE,
                RequestId TEXT NOT NULL, Type TEXT NOT NULL, Content TEXT NOT NULL, Contact TEXT,
                AppVersion TEXT NOT NULL, Platform TEXT NOT NULL, PlatformVersion TEXT NOT NULL,
                DeviceModel TEXT NOT NULL, AttachmentUrls TEXT NOT NULL, Status TEXT NOT NULL,
                AdminReply TEXT, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
                AdminRepliedAt INTEGER, UNIQUE(UserId, RequestId));
            CREATE INDEX IF NOT EXISTS IX_Feedback_User ON Feedback(UserId, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Feedback_Status ON Feedback(Status, CreatedAt DESC);
            CREATE INDEX IF NOT EXISTS IX_Feedback_Type ON Feedback(Type, CreatedAt DESC);
            """;
        await command.ExecuteNonQueryAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        command.CommandText = "PRAGMA table_info(Accounts);";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        foreach (var name in new[] { "Email", "NormalizedEmail" })
        {
            if (columns.Contains(name)) continue;
            command.CommandText = $"ALTER TABLE Accounts ADD COLUMN {name} TEXT;";
            await command.ExecuteNonQueryAsync();
        }
        command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS IX_Accounts_Email ON Accounts(NormalizedEmail) WHERE NormalizedEmail IS NOT NULL;";
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AccountSessionResponse> RegisterAsync(AccountCredentials credentials, CancellationToken token)
    {
        var username = Validate(credentials, register: true);
        var email = credentials.Email!.Trim();
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = await Task.Run(() => PasswordHash(credentials.Password, salt), token);
        var user = new AccountUser(Guid.NewGuid(), username, email);
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Accounts (Id, Username, NormalizedUsername, Salt, PasswordHash, CreatedAt, Email, NormalizedEmail) VALUES ($id, $username, $normalized, $salt, $hash, $created, $email, $normalizedEmail);";
        command.Parameters.AddWithValue("$id", user.Id.ToString());
        command.Parameters.AddWithValue("$username", username);
        command.Parameters.AddWithValue("$normalized", username.ToLowerInvariant());
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$created", _clock.GetUtcNow().ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$email", email);
        command.Parameters.AddWithValue("$normalizedEmail", email.ToLowerInvariant());
        try { await command.ExecuteNonQueryAsync(token); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        { throw new AccountRequestException(StatusCodes.Status409Conflict, exception.Message.Contains("NormalizedEmail", StringComparison.Ordinal)
            ? "该邮箱已被其他账号使用。" : "该用户名已注册。"); }
        var session = await CreateSessionAsync(connection, transaction, user, token);
        await transaction.CommitAsync(token);
        return session;
    }

    public async Task<AccountSessionResponse> SignInAsync(AccountCredentials credentials, CancellationToken token)
    {
        var username = Validate(credentials, register: false);
        await using var connection = await OpenAsync(token);
        AccountUser? user = null;
        var salt = _dummySalt;
        var expected = _dummyHash;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Id, Username, Salt, PasswordHash, Email FROM Accounts WHERE NormalizedUsername = $username OR NormalizedEmail = $username;";
            command.Parameters.AddWithValue("$username", username.ToLowerInvariant());
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                user = new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(4) ? null : reader.GetString(4));
                salt = (byte[])reader[2]; expected = (byte[])reader[3];
            }
        }
        var actual = await Task.Run(() => PasswordHash(credentials.Password, salt), token);
        var valid = CryptographicOperations.FixedTimeEquals(actual, expected);
        CryptographicOperations.ZeroMemory(actual);
        if (!valid || user is null)
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "用户名、邮箱或密码错误。");
        return await CreateSessionAsync(connection, null, user, token);
    }

    public async Task<AccountSessionInfo> GetSessionAsync(string bearer, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.Id, a.Username, s.ExpiresAt, a.Email FROM AccountSessions s
            JOIN Accounts a ON a.Id = s.AccountId WHERE s.TokenHash = $hash AND s.ExpiresAt > $now;
            """;
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeSeconds());
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new AccountRequestException(StatusCodes.Status401Unauthorized, "会话已失效，请重新登录。");
        return new(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(3) ? null : reader.GetString(3)), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)), Region);
    }

    public async Task RevokeAsync(string bearer, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM AccountSessions WHERE TokenHash = $hash;";
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<FeedbackReceipt?> FindFeedbackAsync(Guid userId, Guid requestId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Number, Status, CreatedAt FROM Feedback WHERE UserId=$user AND RequestId=$request;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$request", requestId.ToString());
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)))
            : null;
    }

    public async Task<FeedbackReceipt> CreateFeedbackAsync(Guid id, AccountUser user, FeedbackDraft draft,
        IReadOnlyList<string> attachmentKeys, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token);
        if (await FindFeedbackAsync(connection, transaction, user.Id, draft.RequestId, token) is { } existing)
        {
            await transaction.CommitAsync(token);
            return existing;
        }
        var now = _clock.GetUtcNow();        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = """
                SELECT
                    SUM(CASE WHEN CreatedAt > $window THEN 1 ELSE 0 END),
                    SUM(CASE WHEN CreatedAt > $day THEN 1 ELSE 0 END)
                FROM Feedback WHERE UserId=$user;
                """;
            check.Parameters.AddWithValue("$user", user.Id.ToString());
            check.Parameters.AddWithValue("$window", now.AddMinutes(-FeedbackLimits.PerUserWindowMinutes).ToUnixTimeSeconds());
            check.Parameters.AddWithValue("$day", now.AddDays(-1).ToUnixTimeSeconds());
            await using var reader = await check.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                var recent = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
                var daily = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                if (recent >= FeedbackLimits.PerUserWindowLimit || daily >= FeedbackLimits.PerUserDayLimit)
                    throw new AccountRequestException(StatusCodes.Status429TooManyRequests, "提交过于频繁，请稍后再试。");
            }
        }
        long number;
        using (var next = connection.CreateCommand())
        {
            next.Transaction = transaction;
            next.CommandText = "SELECT COALESCE(MAX(Number), 0) + 1 FROM Feedback;";
            number = (long)(await next.ExecuteScalarAsync(token) ?? 1L);
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO Feedback (Id, Number, UserId, RequestId, Type, Content, Contact, AppVersion,
                    Platform, PlatformVersion, DeviceModel, AttachmentUrls, Status, AdminReply,
                    CreatedAt, UpdatedAt, AdminRepliedAt)
                VALUES ($id, $number, $user, $request, $type, $content, $contact, $appVersion,
                    $platform, $platformVersion, $deviceModel, $attachments, $status, NULL,
                    $created, $created, NULL);
                """;
            insert.Parameters.AddWithValue("$id", id.ToString());
            insert.Parameters.AddWithValue("$number", number);
            insert.Parameters.AddWithValue("$user", user.Id.ToString());
            insert.Parameters.AddWithValue("$request", draft.RequestId.ToString());
            insert.Parameters.AddWithValue("$type", draft.Type);
            insert.Parameters.AddWithValue("$content", draft.Content.Trim());
            insert.Parameters.AddWithValue("$contact", string.IsNullOrWhiteSpace(draft.Contact) ? DBNull.Value : draft.Contact.Trim());
            insert.Parameters.AddWithValue("$appVersion", draft.AppVersion.Trim());
            insert.Parameters.AddWithValue("$platform", draft.Platform.Trim());
            insert.Parameters.AddWithValue("$platformVersion", draft.PlatformVersion.Trim());
            insert.Parameters.AddWithValue("$deviceModel", draft.DeviceModel.Trim());
            insert.Parameters.AddWithValue("$attachments", FeedbackJson.SerializeKeys(attachmentKeys));
            insert.Parameters.AddWithValue("$status", FeedbackStatuses.Pending);
            insert.Parameters.AddWithValue("$created", now.ToUnixTimeSeconds());
            try { await insert.ExecuteNonQueryAsync(token); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                if (await FindFeedbackAsync(connection, transaction, user.Id, draft.RequestId, token) is { } duplicate)
                {
                    await transaction.CommitAsync(token);
                    return duplicate;
                }
                throw;
            }
        }
        await transaction.CommitAsync(token);
        return new(id, number, FeedbackStatuses.Pending, DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()));
    }

    public async Task<FeedbackItem[]> ListFeedbackAsync(Guid userId, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = FeedbackSelect + " WHERE UserId=$user ORDER BY CreatedAt DESC, Number DESC LIMIT 100;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        return await ReadFeedbackAsync(command, token);
    }

    public async Task<FeedbackItem> GetFeedbackAsync(Guid userId, Guid id, CancellationToken token)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = FeedbackSelect + " WHERE UserId=$user AND Id=$id;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$id", id.ToString());
        return (await ReadFeedbackAsync(command, token)).SingleOrDefault()
            ?? throw new AccountRequestException(StatusCodes.Status404NotFound, "反馈不存在或已被删除。");
    }

    private const string FeedbackSelect = """
        SELECT Id, Number, Type, Content, Contact, AppVersion, Platform, PlatformVersion, DeviceModel,
            Status, AdminReply, CreatedAt, UpdatedAt, AttachmentUrls
        FROM Feedback
        """;

    private static async Task<FeedbackItem[]> ReadFeedbackAsync(SqliteCommand command, CancellationToken token)
    {
        var items = new List<FeedbackItem>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var attachments = FeedbackJson.DeserializeKeys(reader.GetString(13));
            var hasSoftwareLog = attachments.Any(key =>
                key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            items.Add(new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(11)),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)),
                attachments.Count(key => !key.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)),
                hasSoftwareLog));
        }
        return items.ToArray();
    }

    private static async Task<FeedbackReceipt?> FindFeedbackAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid userId, Guid requestId, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, Number, Status, CreatedAt FROM Feedback WHERE UserId=$user AND RequestId=$request;";
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$request", requestId.ToString());
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token)
            ? new(Guid.Parse(reader.GetString(0)), reader.GetInt64(1), reader.GetString(2),
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3)))
            : null;
    }

    private async Task<AccountSessionResponse> CreateSessionAsync(SqliteConnection connection, SqliteTransaction? transaction,
        AccountUser user, CancellationToken token)
    {
        var bearer = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(_clock.GetUtcNow().AddDays(30).ToUnixTimeSeconds());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM AccountSessions WHERE ExpiresAt <= $now;
            INSERT INTO AccountSessions VALUES ($hash, $account, $expires);
            """;
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$hash", TokenHash(bearer));
        command.Parameters.AddWithValue("$account", user.Id.ToString());
        command.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(token);
        return new() { User = user, Token = bearer, ExpiresAt = expiresAt, Region = Region };
    }

    private string Validate(AccountCredentials credentials, bool register)
    {
        if (credentials.Region != Region) throw new AccountRequestException(StatusCodes.Status403Forbidden, "账号服务区域不匹配。");
        var username = credentials.Username?.Trim() ?? "";
        if (!(register ? AccountValidation.IsUsername(username) : AccountValidation.IsLogin(username)) || !AccountValidation.IsPassword(credentials.Password))
            throw new AccountRequestException(StatusCodes.Status400BadRequest, "用户名或密码格式无效。");
        if (register && !AccountValidation.IsEmail(credentials.Email?.Trim()))
            throw new AccountRequestException(StatusCodes.Status400BadRequest, "注册时必须填写有效的邮箱。");
        if (register && !AccountValidation.IsPassword(credentials.ConfirmPassword))
            throw new AccountRequestException(StatusCodes.Status400BadRequest, "请输入确认登录密码。");
        if (register && credentials.Password != credentials.ConfirmPassword)
            throw new AccountRequestException(StatusCodes.Status400BadRequest, "两次输入的密码不一致。");
        return username;
    }

    private static byte[] PasswordHash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);
    private static byte[] TokenHash(string bearer) => SHA256.HashData(Encoding.ASCII.GetBytes(bearer));

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(_connectionString);
        try { await connection.OpenAsync(token); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
}
