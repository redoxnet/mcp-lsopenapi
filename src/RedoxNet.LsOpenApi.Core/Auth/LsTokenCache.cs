using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// SQLite-backed <see cref="ITokenStore"/> using WAL journal mode.
/// </summary>
/// <remarks>
/// The default database path is platform-specific:
/// <list type="bullet">
///   <item><description>Windows: <c>%LOCALAPPDATA%\RedoxNet\LsOpenApi\token.db</c></description></item>
///   <item><description>Linux/macOS: <c>~/.local/share/redoxnet/lsopenapi/token.db</c></description></item>
/// </list>
/// On POSIX systems the database file and its WAL/SHM siblings are chmod-ed
/// to <c>0600</c> after every write so other local users cannot read cached
/// access tokens.
/// </remarks>
public sealed class LsTokenCache : ITokenStore
{
    /// <summary>Default subfolder under the user's local data directory.</summary>
    public const string DefaultRelativeDirectory = "RedoxNet/LsOpenApi";

    /// <summary>Default database file name.</summary>
    public const string DefaultFileName = "token.db";

    readonly string _databasePath;
    readonly string _connectionString;
    readonly ILogger<LsTokenCache> _logger;
    readonly object _initLock = new();
    bool _initialized;

    /// <summary>
    /// Resolves the default database path for the current OS.
    /// </summary>
    /// <returns>The fully-qualified default database path.</returns>
    public static string GetDefaultDatabasePath()
    {
        string baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, DefaultRelativeDirectory.Replace('/', Path.DirectorySeparatorChar), DefaultFileName);
    }

    /// <summary>
    /// Creates a token cache backed by the default platform-specific path.
    /// </summary>
    /// <param name="logger">Optional logger. <see cref="NullLogger"/> when omitted.</param>
    public LsTokenCache(ILogger<LsTokenCache>? logger = null)
        : this(GetDefaultDatabasePath(), logger)
    {
    }

    /// <summary>
    /// Creates a token cache backed by the specified path. Used by tests.
    /// </summary>
    /// <param name="databasePath">Absolute path to the SQLite database file.</param>
    /// <param name="logger">Optional logger.</param>
    public LsTokenCache(string databasePath, ILogger<LsTokenCache>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must not be empty.", nameof(databasePath));

        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger ?? NullLogger<LsTokenCache>.Instance;
    }

    /// <summary>
    /// Gets the fully-qualified database file path in use.
    /// </summary>
    public string DatabasePath => _databasePath;

    /// <inheritdoc />
    public async Task<LsAccessToken?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureInitialized();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT access_token, token_type, issued_at_utc, expires_at_utc, scope
            FROM tokens
            WHERE key = $key
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;

        string accessToken = reader.GetString(0);
        string tokenType = reader.GetString(1);
        DateTimeOffset issuedAt = DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture);
        DateTimeOffset expiresAt = DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture);
        string? scope = reader.IsDBNull(4) ? null : reader.GetString(4);

        return new LsAccessToken(accessToken, tokenType, issuedAt, expiresAt, scope);
    }

    /// <inheritdoc />
    public async Task SaveAsync(string key, LsAccessToken token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(token);
        EnsureInitialized();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO tokens (key, access_token, token_type, issued_at_utc, expires_at_utc, scope)
            VALUES ($key, $access_token, $token_type, $issued_at_utc, $expires_at_utc, $scope)
            ON CONFLICT(key) DO UPDATE SET
                access_token   = excluded.access_token,
                token_type     = excluded.token_type,
                issued_at_utc  = excluded.issued_at_utc,
                expires_at_utc = excluded.expires_at_utc,
                scope          = excluded.scope;
            """;
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$access_token", token.AccessToken);
        cmd.Parameters.AddWithValue("$token_type", token.TokenType);
        cmd.Parameters.AddWithValue("$issued_at_utc", token.IssuedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$expires_at_utc", token.ExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$scope", (object?)token.Scope ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        ApplyUserOnlyPermissions();
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureInitialized();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM tokens WHERE key = $key;";
        cmd.Parameters.AddWithValue("$key", key);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (_initLock)
        {
            if (_initialized)
                return;

            string? dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }

            using (var create = connection.CreateCommand())
            {
                create.CommandText = """
                    CREATE TABLE IF NOT EXISTS tokens (
                        key            TEXT PRIMARY KEY,
                        access_token   TEXT NOT NULL,
                        token_type     TEXT NOT NULL,
                        issued_at_utc  TEXT NOT NULL,
                        expires_at_utc TEXT NOT NULL,
                        scope          TEXT
                    );
                    """;
                create.ExecuteNonQuery();
            }

            ApplyUserOnlyPermissions();
            _initialized = true;
            _logger.LogDebug("LsTokenCache initialized at {DatabasePath}", _databasePath);
        }
    }

    void ApplyUserOnlyPermissions()
    {
        if (!File.Exists(_databasePath))
            return;

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                const UnixFileMode userRw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.SetUnixFileMode(_databasePath, userRw);
                foreach (string suffix in new[] { "-wal", "-shm" })
                {
                    string sibling = _databasePath + suffix;
                    if (File.Exists(sibling))
                        File.SetUnixFileMode(sibling, userRw);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply 0600 permissions to token cache files.");
            }
        }
        // On Windows the file inherits the user's profile ACL, which is
        // sufficient for the default %LOCALAPPDATA% location.
    }
}
