using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// SQLite-backed repository for local portfolio data.
/// </summary>
internal sealed class SqlitePortfolioRepository : IPortfolioRepository
{
    /// <summary>Environment variable used to override the default database path.</summary>
    public const string DatabasePathEnvVar = "LSOPENAPI_DB_PATH";
    /// <summary>
    /// Default relative directory under the user local app data folder. Matches
    /// <c>LsTokenCache</c> so portfolio.db sits next to token.db.
    /// </summary>
    public const string DefaultRelativeDirectory = "RedoxNet/LsOpenApi";
    /// <summary>Default SQLite database file name.</summary>
    public const string DefaultFileName = "portfolio.db";

    static readonly (int Version, string Sql)[] Migrations =
    [
        (1, """
            CREATE TABLE IF NOT EXISTS stocks (
                symbol      TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                market      TEXT NOT NULL,
                krx_sector  TEXT,
                updated_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS watchlist_groups (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                name        TEXT NOT NULL UNIQUE,
                description TEXT,
                sort_order  INTEGER NOT NULL DEFAULT 0,
                created_at  TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS watchlist_items (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                group_id  INTEGER NOT NULL REFERENCES watchlist_groups(id) ON DELETE CASCADE,
                symbol    TEXT NOT NULL REFERENCES stocks(symbol),
                notes     TEXT,
                added_at  TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(group_id, symbol)
            );

            CREATE TABLE IF NOT EXISTS watched_sectors (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                sector_code TEXT NOT NULL UNIQUE,
                sector_name TEXT NOT NULL,
                notes       TEXT,
                added_at    TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS accounts (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                account_no TEXT NOT NULL UNIQUE,
                nickname   TEXT NOT NULL,
                broker     TEXT NOT NULL DEFAULT 'LS',
                is_default INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );

            CREATE TABLE IF NOT EXISTS holdings (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                symbol     TEXT NOT NULL REFERENCES stocks(symbol),
                quantity   INTEGER NOT NULL CHECK(quantity >= 0),
                avg_price  REAL NOT NULL CHECK(avg_price >= 0),
                notes      TEXT,
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(account_id, symbol)
            );

            CREATE INDEX IF NOT EXISTS idx_watchlist_items_symbol ON watchlist_items(symbol);
            CREATE INDEX IF NOT EXISTS idx_holdings_symbol ON holdings(symbol);
            """),
    ];

    readonly string _databasePath;
    readonly string _connectionString;
    readonly ILogger<SqlitePortfolioRepository> _logger;
    readonly SemaphoreSlim _initLock = new(1, 1);
    bool _initialized;

    /// <summary>
    /// Creates a repository backed by the specified SQLite database path.
    /// </summary>
    public SqlitePortfolioRepository(string databasePath, ILogger<SqlitePortfolioRepository>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
        _logger = logger ?? NullLogger<SqlitePortfolioRepository>.Instance;
    }

    /// <summary>Gets the SQLite database path used by this repository.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>
    /// Resolves the database path from the environment override or the platform default.
    /// </summary>
    public static string ResolveDatabasePath()
    {
        string? overridePath = Environment.GetEnvironmentVariable(DatabasePathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath))
            return overridePath;

        string baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(baseDir, DefaultRelativeDirectory.Replace('/', Path.DirectorySeparatorChar), DefaultFileName);
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            string? dir = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken, applyWal: true).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS _schema_version (
                    version    INTEGER NOT NULL PRIMARY KEY,
                    applied_at TEXT NOT NULL DEFAULT (datetime('now'))
                );
                """,
                transaction: transaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

            int currentVersion = await connection.ExecuteScalarAsync<int?>(
                new CommandDefinition(
                    "SELECT version FROM _schema_version ORDER BY version DESC LIMIT 1;",
                    transaction: transaction,
                    cancellationToken: cancellationToken)) ?? 0;

            foreach ((int version, string sql) in Migrations.Where(m => m.Version > currentVersion))
            {
                await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO _schema_version(version) VALUES (@Version);",
                    new { Version = version }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await SeedAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
            _logger.LogDebug("Portfolio database initialized at {DatabasePath}", _databasePath);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchlistGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT g.id AS Id, g.name AS Name, g.description AS Description, g.sort_order AS SortOrder,
                   COUNT(i.id) AS ItemCount
            FROM watchlist_groups g
            LEFT JOIN watchlist_items i ON i.group_id = g.id
            GROUP BY g.id, g.name, g.description, g.sort_order
            ORDER BY g.sort_order, g.name;
            """;
        IEnumerable<WatchlistGroupSummary> rows = await connection.QueryAsync<WatchlistGroupSummary>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<WatchlistGroup> CreateGroupAsync(string name, string? description, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeName(name, nameof(name));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int nextSort = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(sort_order), -1) + 1 FROM watchlist_groups;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watchlist_groups(name, description, sort_order)
            VALUES (@Name, @Description, @SortOrder)
            ON CONFLICT(name) DO UPDATE SET description = excluded.description;
            """,
            new { Name = normalized, Description = NullIfWhiteSpace(description), SortOrder = nextSort },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await GetGroupAsync(connection, normalized, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to create watchlist group.");
    }

    /// <inheritdoc />
    public async Task<DeleteGroupResult> DeleteGroupAsync(string name, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalized = NormalizeName(name, nameof(name));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long? groupId = await GetGroupIdAsync(connection, normalized, cancellationToken).ConfigureAwait(false);
        if (groupId is null)
            return new DeleteGroupResult(false, 0);
        int itemCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM watchlist_items WHERE group_id = @GroupId;",
            new { GroupId = groupId.Value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM watchlist_groups WHERE id = @GroupId;",
            new { GroupId = groupId.Value }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new DeleteGroupResult(true, itemCount);
    }

    /// <inheritdoc />
    public async Task<WatchlistItem> AddWatchlistItemAsync(string symbol, string group, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedSymbol = NormalizeSymbol(symbol);
        string normalizedGroup = NormalizeName(group, nameof(group));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStockAsync(connection, normalizedSymbol, cancellationToken).ConfigureAwait(false);
        long groupId = await GetGroupIdAsync(connection, normalizedGroup, cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"Watchlist group '{normalizedGroup}' does not exist.");

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watchlist_items(group_id, symbol, notes)
            VALUES (@GroupId, @Symbol, @Notes)
            ON CONFLICT(group_id, symbol) DO UPDATE SET notes = excluded.notes;
            """,
            new { GroupId = groupId, Symbol = normalizedSymbol, Notes = NullIfWhiteSpace(notes) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await GetWatchlistItemAsync(connection, normalizedSymbol, normalizedGroup, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to add watchlist item.");
    }

    /// <inheritdoc />
    public async Task<bool> RemoveWatchlistItemAsync(string symbol, string group, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM watchlist_items
            WHERE symbol = @Symbol
              AND group_id = (SELECT id FROM watchlist_groups WHERE name = @Group);
            """,
            new { Symbol = NormalizeSymbol(symbol), Group = NormalizeName(group, nameof(group)) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchlistItem>> ListWatchlistAsync(string? group, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT i.id AS Id, i.group_id AS GroupId, g.name AS GroupName,
                   i.symbol AS Symbol, s.name AS Name, s.market AS Market, s.krx_sector AS KrxSector,
                   i.notes AS Notes, i.added_at AS AddedAt
            FROM watchlist_items i
            JOIN watchlist_groups g ON g.id = i.group_id
            JOIN stocks s ON s.symbol = i.symbol
            WHERE @Group IS NULL OR g.name = @Group
            ORDER BY g.sort_order, g.name, i.added_at, i.symbol;
            """;
        IEnumerable<WatchlistItem> rows = await connection.QueryAsync<WatchlistItem>(new CommandDefinition(
            sql, new { Group = NullIfWhiteSpace(group) }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<WatchedSector> WatchSectorAsync(string code, string? name, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedCode = NormalizeName(code, nameof(code)).ToUpperInvariant();
        string sectorName = NullIfWhiteSpace(name) ?? normalizedCode;
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watched_sectors(sector_code, sector_name, notes)
            VALUES (@Code, @Name, @Notes)
            ON CONFLICT(sector_code) DO UPDATE SET
                sector_name = excluded.sector_name,
                notes = excluded.notes;
            """,
            new { Code = normalizedCode, Name = sectorName, Notes = NullIfWhiteSpace(notes) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await connection.QuerySingleAsync<WatchedSector>(new CommandDefinition(
            """
            SELECT id AS Id, sector_code AS SectorCode, sector_name AS SectorName, notes AS Notes, added_at AS AddedAt
            FROM watched_sectors
            WHERE sector_code = @Code;
            """,
            new { Code = normalizedCode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UnwatchSectorAsync(string code, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM watched_sectors WHERE sector_code = @Code;",
            new { Code = NormalizeName(code, nameof(code)).ToUpperInvariant() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedSector>> ListSectorsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<WatchedSector> rows = await connection.QueryAsync<WatchedSector>(new CommandDefinition(
            """
            SELECT id AS Id, sector_code AS SectorCode, sector_name AS SectorName, notes AS Notes, added_at AS AddedAt
            FROM watched_sectors
            ORDER BY added_at, sector_code;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<Account> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetDefaultAccountAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Account> SetDefaultAccountAsync(string accountNo, string? nickname, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedAccountNo = NormalizeName(accountNo, nameof(accountNo));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Account current = await GetDefaultAccountAsync(connection, cancellationToken, transaction).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE accounts SET is_default = 0;",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE accounts
            SET account_no = @AccountNo,
                nickname = COALESCE(@Nickname, nickname),
                is_default = 1
            WHERE id = @Id;
            """,
            new { AccountNo = normalizedAccountNo, Nickname = NullIfWhiteSpace(nickname), current.Id },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        Account updated = await GetDefaultAccountAsync(connection, cancellationToken, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<Holding> UpsertHoldingAsync(long accountId, string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be non-negative.");
        if (avgPrice < 0)
            throw new ArgumentOutOfRangeException(nameof(avgPrice), "Average price must be non-negative.");

        string normalizedSymbol = NormalizeSymbol(symbol);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStockAsync(connection, normalizedSymbol, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO holdings(account_id, symbol, quantity, avg_price, notes, updated_at)
            VALUES (@AccountId, @Symbol, @Quantity, @AvgPrice, @Notes, datetime('now'))
            ON CONFLICT(account_id, symbol) DO UPDATE SET
                quantity = excluded.quantity,
                avg_price = excluded.avg_price,
                notes = excluded.notes,
                updated_at = datetime('now');
            """,
            new { AccountId = accountId, Symbol = normalizedSymbol, Quantity = quantity, AvgPrice = avgPrice, Notes = NullIfWhiteSpace(notes) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("Failed to upsert holding.");
    }

    /// <inheritdoc />
    public async Task<Holding?> GetHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetHoldingAsync(connection, accountId, NormalizeSymbol(symbol), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveHoldingAsync(long accountId, string symbol, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM holdings WHERE account_id = @AccountId AND symbol = @Symbol;",
            new { AccountId = accountId, Symbol = NormalizeSymbol(symbol) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Holding>> ListHoldingsAsync(long accountId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<Holding> rows = await connection.QueryAsync<Holding>(new CommandDefinition(
            """
            SELECT h.id AS Id, h.account_id AS AccountId, h.symbol AS Symbol,
                   s.name AS Name, s.market AS Market,
                   h.quantity AS Quantity, h.avg_price AS AvgPrice, h.notes AS Notes, h.updated_at AS UpdatedAt
            FROM holdings h
            JOIN stocks s ON s.symbol = h.symbol
            WHERE h.account_id = @AccountId
            ORDER BY h.updated_at DESC, h.symbol;
            """,
            new { AccountId = accountId }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task UpsertStockAsync(string symbol, string name, string market, string? krxSector, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertStockAsync(connection, NormalizeSymbol(symbol), NormalizeName(name, nameof(name)), NormalizeName(market, nameof(market)), NullIfWhiteSpace(krxSector), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lazily initializes the database before repository operations.
    /// </summary>
    async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a SQLite connection and applies connection-scoped pragmas.
    /// </summary>
    async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken, bool applyWal = false)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (applyWal)
            await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA busy_timeout=5000;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA synchronous=NORMAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        return connection;
    }

    /// <summary>
    /// Inserts default seed rows if they are absent.
    /// </summary>
    static async Task SeedAsync(SqliteConnection connection, System.Data.IDbTransaction transaction, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watchlist_groups(name, sort_order)
            VALUES ('default', 0)
            ON CONFLICT(name) DO NOTHING;
            """,
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        int defaultAccounts = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM accounts WHERE is_default = 1;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (defaultAccounts == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO accounts(account_no, nickname, broker, is_default)
                VALUES ('UNSET', '기본 계좌', 'LS', 1);
                """,
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Looks up a watchlist group id by name.
    /// </summary>
    static Task<long?> GetGroupIdAsync(SqliteConnection connection, string name, CancellationToken cancellationToken) =>
        connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT id FROM watchlist_groups WHERE name = @Name;",
            new { Name = name }, cancellationToken: cancellationToken));

    /// <summary>
    /// Gets a watchlist group by name.
    /// </summary>
    static Task<WatchlistGroup?> GetGroupAsync(SqliteConnection connection, string name, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<WatchlistGroup>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, description AS Description, sort_order AS SortOrder, created_at AS CreatedAt
            FROM watchlist_groups
            WHERE name = @Name;
            """,
            new { Name = name }, cancellationToken: cancellationToken));

    /// <summary>
    /// Gets a watchlist item by symbol and group.
    /// </summary>
    static Task<WatchlistItem?> GetWatchlistItemAsync(SqliteConnection connection, string symbol, string group, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<WatchlistItem>(new CommandDefinition(
            """
            SELECT i.id AS Id, i.group_id AS GroupId, g.name AS GroupName,
                   i.symbol AS Symbol, s.name AS Name, s.market AS Market, s.krx_sector AS KrxSector,
                   i.notes AS Notes, i.added_at AS AddedAt
            FROM watchlist_items i
            JOIN watchlist_groups g ON g.id = i.group_id
            JOIN stocks s ON s.symbol = i.symbol
            WHERE i.symbol = @Symbol AND g.name = @Group;
            """,
            new { Symbol = symbol, Group = group }, cancellationToken: cancellationToken));

    /// <summary>
    /// Gets the default account from an open connection.
    /// </summary>
    static Task<Account> GetDefaultAccountAsync(SqliteConnection connection, CancellationToken cancellationToken, System.Data.IDbTransaction? transaction = null) =>
        connection.QuerySingleAsync<Account>(new CommandDefinition(
            """
            SELECT id AS Id, account_no AS AccountNo, nickname AS Nickname, broker AS Broker,
                   is_default AS IsDefault, created_at AS CreatedAt
            FROM accounts
            WHERE is_default = 1
            ORDER BY id
            LIMIT 1;
            """,
            transaction: transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Gets a holding from an open connection.
    /// </summary>
    static Task<Holding?> GetHoldingAsync(SqliteConnection connection, long accountId, string symbol, CancellationToken cancellationToken) =>
        connection.QuerySingleOrDefaultAsync<Holding>(new CommandDefinition(
            """
            SELECT h.id AS Id, h.account_id AS AccountId, h.symbol AS Symbol,
                   s.name AS Name, s.market AS Market,
                   h.quantity AS Quantity, h.avg_price AS AvgPrice, h.notes AS Notes, h.updated_at AS UpdatedAt
            FROM holdings h
            JOIN stocks s ON s.symbol = h.symbol
            WHERE h.account_id = @AccountId AND h.symbol = @Symbol;
            """,
            new { AccountId = accountId, Symbol = symbol }, cancellationToken: cancellationToken));

    /// <summary>
    /// Ensures placeholder stock metadata exists for a referenced symbol.
    /// </summary>
    static Task EnsureStockAsync(SqliteConnection connection, string symbol, CancellationToken cancellationToken) =>
        UpsertStockAsync(connection, symbol, symbol, "unknown", null, cancellationToken, updateExisting: false);

    /// <summary>
    /// Inserts or updates stock metadata.
    /// </summary>
    static Task UpsertStockAsync(SqliteConnection connection, string symbol, string name, string market, string? krxSector, CancellationToken cancellationToken, bool updateExisting = true)
    {
        string conflictClause = updateExisting
            ? "DO UPDATE SET name = excluded.name, market = excluded.market, krx_sector = excluded.krx_sector, updated_at = datetime('now')"
            : "DO NOTHING";
        return connection.ExecuteAsync(new CommandDefinition(
            $$"""
            INSERT INTO stocks(symbol, name, market, krx_sector)
            VALUES (@Symbol, @Name, @Market, @KrxSector)
            ON CONFLICT(symbol) {{conflictClause}};
            """,
            new { Symbol = symbol, Name = name, Market = market, KrxSector = krxSector },
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Normalizes and validates a 6-character Korean stock/ETF code.
    /// </summary>
    /// <remarks>
    /// Most KRX codes are 6 digits, but some ETFs use a letter in one position
    /// (e.g. TIGER 코리아AI전력기기TOP3플러스 = <c>0117V0</c>). Letters are
    /// uppercased so storage stays case-insensitive.
    /// </remarks>
    static string NormalizeSymbol(string symbol)
    {
        string trimmed = NormalizeName(symbol, nameof(symbol)).ToUpperInvariant();
        if (trimmed.Length != 6 || !trimmed.All(char.IsLetterOrDigit))
            throw new ArgumentException($"Symbol '{symbol}' is not a valid 6-character stock/ETF code.", nameof(symbol));
        return trimmed;
    }

    /// <summary>
    /// Trims and validates a required string value.
    /// </summary>
    static string NormalizeName(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{paramName} must not be empty.", paramName);
        return value.Trim();
    }

    /// <summary>
    /// Converts whitespace-only strings to null and trims non-empty strings.
    /// </summary>
    static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

