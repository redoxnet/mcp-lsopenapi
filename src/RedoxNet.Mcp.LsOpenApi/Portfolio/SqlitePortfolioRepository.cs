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
        (2, """
            -- v0.5: enforce unique nickname so account identifiers stay deterministic.
            -- The seeded 'UNSET' placeholder from v1 is removed unless it actually
            -- owns holdings; v0.5 allows the zero-account empty state and surfaces
            -- it via RequiresAccount errors at the service layer.
            DELETE FROM accounts
            WHERE account_no = 'UNSET'
              AND nickname = '기본 계좌'
              AND id NOT IN (SELECT account_id FROM holdings);

            CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_nickname ON accounts(nickname);
            """),
        (3, """
            -- v0.6: v0.5 mis-named LS theme membership as 'sector'. Rename to align
            -- with the v0.6 industry (KRX 표준 산업분류) vs theme (LS curated group)
            -- split. Rows (e.g. tmcode 0012, 0064) are preserved as-is.
            ALTER TABLE watched_sectors RENAME TO watched_themes;
            ALTER TABLE watched_themes RENAME COLUMN sector_code TO theme_code;
            ALTER TABLE watched_themes RENAME COLUMN sector_name TO theme_name;

            -- v0.6: stock_themes caches per-stock t1532 theme memberships so
            -- holdings × theme filters in ls_holdings_list can resolve without
            -- a live TR fetch. Populated by fire-and-forget enrichment on writes.
            CREATE TABLE stock_themes (
                symbol      TEXT NOT NULL REFERENCES stocks(symbol),
                theme_code  TEXT NOT NULL,
                theme_name  TEXT NOT NULL,
                updated_at  TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (symbol, theme_code)
            );
            CREATE INDEX idx_stock_themes_theme_code ON stock_themes(theme_code);
            CREATE INDEX idx_stock_themes_theme_name ON stock_themes(theme_name);
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
    public async Task<RenameGroupResult> RenameGroupAsync(string oldName, string newName, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedOld = NormalizeName(oldName, nameof(oldName));
        string normalizedNew = NormalizeName(newName, nameof(newName));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long? groupId = await GetGroupIdAsync(connection, normalizedOld, cancellationToken).ConfigureAwait(false);
        if (groupId is null)
            throw new InvalidOperationException($"Watchlist group '{normalizedOld}' does not exist.");
        if (string.Equals(normalizedOld, normalizedNew, StringComparison.Ordinal))
            return new RenameGroupResult(normalizedOld, normalizedNew);

        long? collision = await GetGroupIdAsync(connection, normalizedNew, cancellationToken).ConfigureAwait(false);
        if (collision is not null)
            throw new InvalidOperationException($"Watchlist group '{normalizedNew}' already exists.");

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE watchlist_groups SET name = @NewName WHERE id = @Id;",
            new { NewName = normalizedNew, Id = groupId.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RenameGroupResult(normalizedOld, normalizedNew);
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
    public async Task<WatchedTheme> WatchThemeAsync(string code, string? name, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedCode = NormalizeName(code, nameof(code)).ToUpperInvariant();
        string themeName = NullIfWhiteSpace(name) ?? normalizedCode;
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watched_themes(theme_code, theme_name, notes)
            VALUES (@Code, @Name, @Notes)
            ON CONFLICT(theme_code) DO UPDATE SET
                theme_name = excluded.theme_name,
                notes = excluded.notes;
            """,
            new { Code = normalizedCode, Name = themeName, Notes = NullIfWhiteSpace(notes) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return await connection.QuerySingleAsync<WatchedTheme>(new CommandDefinition(
            """
            SELECT id AS Id, theme_code AS ThemeCode, theme_name AS ThemeName, notes AS Notes, added_at AS AddedAt
            FROM watched_themes
            WHERE theme_code = @Code;
            """,
            new { Code = normalizedCode }, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> UnwatchThemeAsync(string code, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM watched_themes WHERE theme_code = @Code;",
            new { Code = NormalizeName(code, nameof(code)).ToUpperInvariant() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return affected > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WatchedTheme>> ListThemesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<WatchedTheme> rows = await connection.QueryAsync<WatchedTheme>(new CommandDefinition(
            """
            SELECT id AS Id, theme_code AS ThemeCode, theme_name AS ThemeName, notes AS Notes, added_at AS AddedAt
            FROM watched_themes
            ORDER BY added_at, theme_code;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountSummary>> ListAccountSummariesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        const string sql = """
            SELECT a.account_no AS AccountNumber, a.nickname AS Nickname, a.broker AS Broker,
                   a.is_default AS IsDefault,
                   (SELECT COUNT(*) FROM holdings h WHERE h.account_id = a.id) AS HoldingsCount
            FROM accounts a
            ORDER BY a.is_default DESC, a.id ASC;
            """;
        IEnumerable<AccountSummary> rows = await connection.QueryAsync<AccountSummary>(
            new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<Account> rows = await connection.QueryAsync<Account>(
            new CommandDefinition(AccountSelectSql + " ORDER BY a.is_default DESC, a.id ASC;",
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<Account?> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetDefaultAccountAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Account?> GetAccountByIdentifierAsync(string identifier, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string trimmed = NormalizeName(identifier, nameof(identifier));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetAccountByIdentifierAsync(connection, trimmed, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Account> UpsertAccountAsync(string accountNumber, string nickname, string? broker, bool setDefault, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedAccountNumber = NormalizeName(accountNumber, nameof(accountNumber));
        string normalizedNickname = NormalizeName(nickname, nameof(nickname));
        string normalizedBroker = NullIfWhiteSpace(broker) ?? "LS";

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO accounts(account_no, nickname, broker, is_default)
            VALUES (@AccountNumber, @Nickname, @Broker, 0)
            ON CONFLICT(account_no) DO UPDATE SET
                nickname = excluded.nickname,
                broker = excluded.broker;
            """,
            new { AccountNumber = normalizedAccountNumber, Nickname = normalizedNickname, Broker = normalizedBroker },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Promote to default if requested OR if no default currently exists.
        int existingDefaults = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM accounts WHERE is_default = 1;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        bool promote = setDefault || existingDefaults == 0;
        if (promote)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE accounts SET is_default = CASE WHEN account_no = @AccountNumber THEN 1 ELSE 0 END;",
                new { AccountNumber = normalizedAccountNumber },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        Account result = await connection.QuerySingleAsync<Account>(new CommandDefinition(
            AccountSelectSql + " WHERE a.account_no = @AccountNumber;",
            new { AccountNumber = normalizedAccountNumber }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<RemoveAccountResult> RemoveAccountAsync(Account account, bool confirm, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        int holdingCount = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM holdings WHERE account_id = @Id;",
            new { account.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (holdingCount > 0 && !confirm)
        {
            // Caller must escalate. Service translates this into RequiresConfirmationException.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new RemoveAccountResult(false, holdingCount, null);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM accounts WHERE id = @Id;",
            new { account.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Default succession: if removed account was default and others remain, promote id ASC.
        AccountInfo? newDefault = null;
        if (account.IsDefault)
        {
            Account? heir = await connection.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
                AccountSelectSql + " ORDER BY a.id ASC LIMIT 1;",
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            if (heir is not null)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE accounts SET is_default = CASE WHEN id = @Id THEN 1 ELSE 0 END;",
                    new { heir.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                newDefault = new AccountInfo(heir.AccountNo, heir.Nickname, heir.Broker, IsDefault: true);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RemoveAccountResult(true, holdingCount, newDefault);
    }

    /// <inheritdoc />
    public async Task<Account> SetDefaultAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE accounts SET is_default = CASE WHEN id = @Id THEN 1 ELSE 0 END;",
            new { account.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        Account updated = await connection.QuerySingleAsync<Account>(new CommandDefinition(
            AccountSelectSql + " WHERE a.id = @Id;",
            new { account.Id }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc />
    public async Task<RenameBrokerResult> RenameBrokerAsync(string fromBroker, string toBroker, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedFrom = NormalizeName(fromBroker, nameof(fromBroker));
        string normalizedTo = NormalizeName(toBroker, nameof(toBroker));
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        int affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE accounts SET broker = @To WHERE broker = @From;",
            new { From = normalizedFrom, To = normalizedTo },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return new RenameBrokerResult(normalizedFrom, normalizedTo, affected);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Account>> FindAccountsHoldingAsync(string symbol, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedSymbol = NormalizeSymbol(symbol);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<Account> rows = await connection.QueryAsync<Account>(new CommandDefinition(
            AccountSelectSql + """
             JOIN holdings h ON h.account_id = a.id
             WHERE h.symbol = @Symbol
             ORDER BY a.is_default DESC, a.id ASC;
            """,
            new { Symbol = normalizedSymbol }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<Holding> SetHoldingAsync(long accountId, string symbol, int quantity, double avgPrice, string? notes, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive; use RemoveHoldingAsync to delete a holding.");
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
    public async Task<Holding> BuyHoldingAsync(long accountId, string symbol, int quantity, double price, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Buy quantity must be positive.");
        if (price < 0)
            throw new ArgumentOutOfRangeException(nameof(price), "Buy price must be non-negative.");

        string normalizedSymbol = NormalizeSymbol(symbol);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStockAsync(connection, normalizedSymbol, cancellationToken).ConfigureAwait(false);

        Holding? existing = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false);
        int newQty;
        double newAvg;
        if (existing is null)
        {
            newQty = quantity;
            newAvg = price;
        }
        else
        {
            newQty = existing.Quantity + quantity;
            double existingCost = existing.Quantity * existing.AvgPrice;
            double buyCost = quantity * price;
            newAvg = newQty == 0 ? 0 : (existingCost + buyCost) / newQty;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO holdings(account_id, symbol, quantity, avg_price, updated_at)
            VALUES (@AccountId, @Symbol, @Quantity, @AvgPrice, datetime('now'))
            ON CONFLICT(account_id, symbol) DO UPDATE SET
                quantity = excluded.quantity,
                avg_price = excluded.avg_price,
                updated_at = datetime('now');
            """,
            new { AccountId = accountId, Symbol = normalizedSymbol, Quantity = newQty, AvgPrice = newAvg },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        Holding result = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Failed to record buy.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task<Holding?> SellHoldingAsync(long accountId, string symbol, int quantity, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "Sell quantity must be positive.");

        string normalizedSymbol = NormalizeSymbol(symbol);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Holding? existing = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false);
        if (existing is null)
            throw new InvalidOperationException($"Symbol '{normalizedSymbol}' is not held in this account.");
        if (quantity > existing.Quantity)
            throw new ArgumentOutOfRangeException(nameof(quantity),
                $"Cannot sell {quantity} share(s); current quantity is {existing.Quantity}.");

        int newQty = existing.Quantity - quantity;
        if (newQty == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM holdings WHERE account_id = @AccountId AND symbol = @Symbol;",
                new { AccountId = accountId, Symbol = normalizedSymbol },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE holdings
            SET quantity = @Quantity, updated_at = datetime('now')
            WHERE account_id = @AccountId AND symbol = @Symbol;
            """,
            new { AccountId = accountId, Symbol = normalizedSymbol, Quantity = newQty },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        Holding result = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Failed to record sell.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
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
    public async Task<IReadOnlyList<Holding>> ListAllHoldingsAsync(CancellationToken cancellationToken = default)
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
            ORDER BY h.account_id, h.updated_at DESC, h.symbol;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <inheritdoc />
    public async Task<Holding?> ApplyCorporateActionAsync(long accountId, string symbol, double qtyMultiplier, double priceMultiplier, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (qtyMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(qtyMultiplier), "Quantity multiplier must be positive.");
        if (priceMultiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(priceMultiplier), "Price multiplier must be positive.");

        string normalizedSymbol = NormalizeSymbol(symbol);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        Holding? existing = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        double scaledQty = existing.Quantity * qtyMultiplier;
        int newQty = (int)Math.Round(scaledQty, MidpointRounding.AwayFromZero);
        if (Math.Abs(scaledQty - newQty) > 1e-9)
            throw new ArgumentOutOfRangeException(nameof(qtyMultiplier),
                $"Corporate action produces non-integer share count ({scaledQty:G}). Reverse-split ratios must evenly divide the current quantity.");
        if (newQty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qtyMultiplier),
                "Corporate action would zero or invert the position.");

        double newAvg = existing.AvgPrice * priceMultiplier;

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE holdings
            SET quantity = @Quantity, avg_price = @AvgPrice, updated_at = datetime('now')
            WHERE account_id = @AccountId AND symbol = @Symbol;
            """,
            new { AccountId = accountId, Symbol = normalizedSymbol, Quantity = newQty, AvgPrice = newAvg },
            transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        Holding result = await GetHoldingAsync(connection, accountId, normalizedSymbol, cancellationToken, transaction).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Corporate action failed.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc />
    public async Task UpsertStockAsync(string symbol, string name, string market, string? krxSector, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertStockAsync(connection, NormalizeSymbol(symbol), NormalizeName(name, nameof(name)), NormalizeName(market, nameof(market)), NullIfWhiteSpace(krxSector), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ReplaceStockThemesAsync(string symbol, IReadOnlyList<ThemeCatalogRow> themes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(themes);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        string normalizedSymbol = NormalizeSymbol(symbol);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Ensure the FK target row exists before inserting stock_themes rows.
        await UpsertStockAsync(connection, normalizedSymbol, normalizedSymbol, "unknown", null, cancellationToken, updateExisting: false).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM stock_themes WHERE symbol = @Symbol;",
            new { Symbol = normalizedSymbol }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (ThemeCatalogRow theme in themes)
        {
            if (string.IsNullOrWhiteSpace(theme.Code))
                continue;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO stock_themes(symbol, theme_code, theme_name, updated_at)
                VALUES (@Symbol, @ThemeCode, @ThemeName, datetime('now'))
                ON CONFLICT(symbol, theme_code) DO UPDATE SET
                    theme_name = excluded.theme_name,
                    updated_at = datetime('now');
                """,
                new
                {
                    Symbol = normalizedSymbol,
                    ThemeCode = theme.Code.Trim().ToUpperInvariant(),
                    ThemeName = (theme.Name ?? "").Trim(),
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PortfolioExportDto> ExportSnapshotAsync(string exporterVersion, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        IEnumerable<Account> accounts = await connection.QueryAsync<Account>(new CommandDefinition(
            AccountSelectSql + " ORDER BY a.id;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var accountList = accounts.ToList();

        IEnumerable<Holding> holdings = await connection.QueryAsync<Holding>(new CommandDefinition(
            """
            SELECT h.id AS Id, h.account_id AS AccountId, h.symbol AS Symbol,
                   s.name AS Name, s.market AS Market,
                   h.quantity AS Quantity, h.avg_price AS AvgPrice, h.notes AS Notes, h.updated_at AS UpdatedAt
            FROM holdings h
            JOIN stocks s ON s.symbol = h.symbol
            ORDER BY h.account_id, h.id;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var holdingsByAccount = holdings.GroupBy(h => h.AccountId).ToDictionary(g => g.Key, g => g.ToList());

        IEnumerable<WatchlistGroup> groups = await connection.QueryAsync<WatchlistGroup>(new CommandDefinition(
            """
            SELECT id AS Id, name AS Name, description AS Description, sort_order AS SortOrder, created_at AS CreatedAt
            FROM watchlist_groups
            ORDER BY sort_order, id;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var groupList = groups.ToList();

        IEnumerable<WatchlistItemRow> items = await connection.QueryAsync<WatchlistItemRow>(new CommandDefinition(
            """
            SELECT i.group_id AS GroupId, i.symbol AS Symbol, i.notes AS Notes, i.added_at AS AddedAt
            FROM watchlist_items i
            ORDER BY i.group_id, i.id;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var itemsByGroup = items.GroupBy(t => t.GroupId).ToDictionary(g => g.Key, g => g.ToList());

        IEnumerable<WatchedTheme> themes = await connection.QueryAsync<WatchedTheme>(new CommandDefinition(
            """
            SELECT id AS Id, theme_code AS ThemeCode, theme_name AS ThemeName, notes AS Notes, added_at AS AddedAt
            FROM watched_themes
            ORDER BY added_at, theme_code;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var dto = new PortfolioExportDto
        {
            SchemaVersion = 1,
            ExportedAt = DateTimeOffset.Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ExporterVersion = exporterVersion,
            Accounts = accountList.Select(a => new AccountExportDto
            {
                AccountNumber = a.AccountNo,
                Nickname = a.Nickname,
                Broker = a.Broker,
                IsDefault = a.IsDefault,
                CreatedAt = a.CreatedAt,
                Holdings = holdingsByAccount.TryGetValue(a.Id, out List<Holding>? hs)
                    ? hs.Select(h => new HoldingExportDto
                    {
                        Shcode = h.Symbol,
                        Quantity = h.Quantity,
                        AvgPrice = h.AvgPrice,
                        Notes = h.Notes,
                        UpdatedAt = h.UpdatedAt,
                    }).ToList()
                    : new(),
            }).ToList(),
            WatchlistGroups = groupList.Select(g => new WatchlistGroupExportDto
            {
                Name = g.Name,
                Description = g.Description,
                SortOrder = g.SortOrder,
                CreatedAt = g.CreatedAt,
                Items = itemsByGroup.TryGetValue(g.Id, out var its)
                    ? its.Select(t => new WatchlistItemExportDto
                    {
                        Shcode = t.Symbol,
                        Notes = t.Notes,
                        AddedAt = t.AddedAt,
                    }).ToList()
                    : new(),
            }).ToList(),
            WatchedThemes = themes.Select(t => new WatchedThemeExportDto
            {
                ThemeCode = t.ThemeCode,
                ThemeName = t.ThemeName,
                Notes = t.Notes,
                AddedAt = t.AddedAt,
            }).ToList(),
        };
        return dto;
    }

    /// <inheritdoc />
    public async Task<ApplyImportResult> ApplyImportAsync(PortfolioExportDto dto, string mode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        ArgumentException.ThrowIfNullOrWhiteSpace(mode);
        string normalizedMode = mode.Trim().ToLowerInvariant();
        if (normalizedMode is not ("merge" or "replace"))
            throw new ArgumentException($"mode must be 'merge' or 'replace', got '{mode}'.", nameof(mode));

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        var accountSkips = new List<AccountSkip>();
        var holdingSkips = new List<HoldingSkip>();
        var groupSkips = new List<WatchlistGroupSkip>();
        var itemSkips = new List<WatchlistItemSkip>();
        var themeSkips = new List<WatchedThemeSkip>();
        int importedAccounts = 0, importedHoldings = 0, importedGroups = 0, importedItems = 0, importedThemes = 0;

        if (normalizedMode == "replace")
        {
            // Wipe export-covered domains. stocks/stock_themes caches are
            // intentionally preserved (spec §4.3.2) so quote/theme enrichment
            // doesn't need a full re-fetch after import.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM holdings;
                DELETE FROM watchlist_items;
                DELETE FROM watchlist_groups WHERE name != 'default';
                DELETE FROM watched_themes;
                DELETE FROM accounts;
                """,
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        // Track existing keys for merge-mode skip detection. For replace mode
        // these lookups still happen but every set is empty post-wipe, so the
        // duplicate paths short-circuit naturally.
        HashSet<string> existingAccountNumbers = new(StringComparer.Ordinal);
        HashSet<string> existingNicknames = new(StringComparer.Ordinal);
        Dictionary<string, long> accountIdByNumber = new(StringComparer.Ordinal);

        IEnumerable<AccountIdentityRow> accountRows = await connection.QueryAsync<AccountIdentityRow>(new CommandDefinition(
            "SELECT account_no AS AccountNo, nickname AS Nickname, id AS Id FROM accounts;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (AccountIdentityRow row in accountRows)
        {
            existingAccountNumbers.Add(row.AccountNo);
            existingNicknames.Add(row.Nickname);
            accountIdByNumber[row.AccountNo] = row.Id;
        }

        // ----- Accounts + nested holdings -----
        foreach (AccountExportDto acc in dto.Accounts)
        {
            if (string.IsNullOrWhiteSpace(acc.AccountNumber) || string.IsNullOrWhiteSpace(acc.Nickname))
            {
                accountSkips.Add(new AccountSkip(acc.AccountNumber, acc.Nickname, "invalid_account_row"));
                continue;
            }

            if (existingAccountNumbers.Contains(acc.AccountNumber))
            {
                accountSkips.Add(new AccountSkip(acc.AccountNumber, acc.Nickname, "duplicate_account_number"));
                // Even when the account row is skipped, nested holdings still
                // need a skip note so the model can explain "we kept your old
                // account; the file's holdings were not merged into it".
                foreach (HoldingExportDto h in acc.Holdings)
                    holdingSkips.Add(new HoldingSkip(acc.AccountNumber, h.Shcode, "skipped_account_holdings_kept"));
                continue;
            }
            if (existingNicknames.Contains(acc.Nickname))
            {
                accountSkips.Add(new AccountSkip(acc.AccountNumber, acc.Nickname, "duplicate_nickname"));
                foreach (HoldingExportDto h in acc.Holdings)
                    holdingSkips.Add(new HoldingSkip(acc.AccountNumber, h.Shcode, "skipped_account_holdings_kept"));
                continue;
            }

            string createdAt = string.IsNullOrWhiteSpace(acc.CreatedAt) ? CurrentSqliteTimestamp() : acc.CreatedAt;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO accounts(account_no, nickname, broker, is_default, created_at)
                VALUES (@AccountNumber, @Nickname, @Broker, @IsDefault, @CreatedAt);
                """,
                new
                {
                    acc.AccountNumber,
                    acc.Nickname,
                    Broker = string.IsNullOrWhiteSpace(acc.Broker) ? "LS" : acc.Broker,
                    IsDefault = acc.IsDefault ? 1 : 0,
                    CreatedAt = createdAt,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            importedAccounts++;
            existingAccountNumbers.Add(acc.AccountNumber);
            existingNicknames.Add(acc.Nickname);

            long accountId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT id FROM accounts WHERE account_no = @AccountNumber;",
                new { acc.AccountNumber }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            accountIdByNumber[acc.AccountNumber] = accountId;

            foreach (HoldingExportDto h in acc.Holdings)
            {
                if (string.IsNullOrWhiteSpace(h.Shcode) || h.Quantity <= 0 || h.AvgPrice < 0)
                {
                    holdingSkips.Add(new HoldingSkip(acc.AccountNumber, h.Shcode, "invalid_holding_row"));
                    continue;
                }
                string normalizedSymbol = h.Shcode.Trim().ToUpperInvariant();
                await UpsertStockAsync(connection, normalizedSymbol, normalizedSymbol, "unknown", null, cancellationToken, updateExisting: false).ConfigureAwait(false);
                string updatedAt = string.IsNullOrWhiteSpace(h.UpdatedAt) ? CurrentSqliteTimestamp() : h.UpdatedAt;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO holdings(account_id, symbol, quantity, avg_price, notes, updated_at)
                    VALUES (@AccountId, @Symbol, @Quantity, @AvgPrice, @Notes, @UpdatedAt);
                    """,
                    new
                    {
                        AccountId = accountId,
                        Symbol = normalizedSymbol,
                        h.Quantity,
                        h.AvgPrice,
                        h.Notes,
                        UpdatedAt = updatedAt,
                    },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                importedHoldings++;
            }
        }

        // ----- Watchlist groups + nested items -----
        HashSet<string> existingGroupNames = new(StringComparer.Ordinal);
        Dictionary<string, long> groupIdByName = new(StringComparer.Ordinal);
        IEnumerable<GroupIdentityRow> groupRows = await connection.QueryAsync<GroupIdentityRow>(new CommandDefinition(
            "SELECT name AS Name, id AS Id FROM watchlist_groups;",
            transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (GroupIdentityRow g in groupRows)
        {
            existingGroupNames.Add(g.Name);
            groupIdByName[g.Name] = g.Id;
        }

        foreach (WatchlistGroupExportDto g in dto.WatchlistGroups)
        {
            if (string.IsNullOrWhiteSpace(g.Name))
            {
                groupSkips.Add(new WatchlistGroupSkip(g.Name, "invalid_group_row"));
                continue;
            }
            long groupId;
            if (existingGroupNames.Contains(g.Name))
            {
                // The seeded 'default' group survives replace-mode wipes, so a
                // file that re-declares 'default' will merge into it rather
                // than fail. Items under it get the dup-or-add logic below.
                groupSkips.Add(new WatchlistGroupSkip(g.Name, "duplicate_group_name"));
                groupId = groupIdByName[g.Name];
            }
            else
            {
                string createdAt = string.IsNullOrWhiteSpace(g.CreatedAt) ? CurrentSqliteTimestamp() : g.CreatedAt;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO watchlist_groups(name, description, sort_order, created_at)
                    VALUES (@Name, @Description, @SortOrder, @CreatedAt);
                    """,
                    new { g.Name, g.Description, g.SortOrder, CreatedAt = createdAt },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                importedGroups++;
                groupId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT id FROM watchlist_groups WHERE name = @Name;",
                    new { g.Name }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                existingGroupNames.Add(g.Name);
                groupIdByName[g.Name] = groupId;
            }

            HashSet<string> existingItemSymbols = new(
                (await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT symbol FROM watchlist_items WHERE group_id = @GroupId;",
                    new { GroupId = groupId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
                .ToList(),
                StringComparer.Ordinal);

            foreach (WatchlistItemExportDto item in g.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Shcode))
                {
                    itemSkips.Add(new WatchlistItemSkip(g.Name, item.Shcode, "invalid_item_row"));
                    continue;
                }
                string normalizedSymbol = item.Shcode.Trim().ToUpperInvariant();
                if (existingItemSymbols.Contains(normalizedSymbol))
                {
                    itemSkips.Add(new WatchlistItemSkip(g.Name, normalizedSymbol, "duplicate_group_symbol"));
                    continue;
                }
                await UpsertStockAsync(connection, normalizedSymbol, normalizedSymbol, "unknown", null, cancellationToken, updateExisting: false).ConfigureAwait(false);
                string addedAt = string.IsNullOrWhiteSpace(item.AddedAt) ? CurrentSqliteTimestamp() : item.AddedAt;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO watchlist_items(group_id, symbol, notes, added_at)
                    VALUES (@GroupId, @Symbol, @Notes, @AddedAt);
                    """,
                    new { GroupId = groupId, Symbol = normalizedSymbol, item.Notes, AddedAt = addedAt },
                    transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                importedItems++;
                existingItemSymbols.Add(normalizedSymbol);
            }
        }

        // ----- Watched themes -----
        HashSet<string> existingThemeCodes = new(
            (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT theme_code FROM watched_themes;",
                transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false))
            .ToList(),
            StringComparer.Ordinal);

        foreach (WatchedThemeExportDto t in dto.WatchedThemes)
        {
            if (string.IsNullOrWhiteSpace(t.ThemeCode))
            {
                themeSkips.Add(new WatchedThemeSkip(t.ThemeCode, "invalid_theme_row"));
                continue;
            }
            string normalizedCode = t.ThemeCode.Trim().ToUpperInvariant();
            if (existingThemeCodes.Contains(normalizedCode))
            {
                themeSkips.Add(new WatchedThemeSkip(normalizedCode, "duplicate_theme_code"));
                continue;
            }
            string addedAt = string.IsNullOrWhiteSpace(t.AddedAt) ? CurrentSqliteTimestamp() : t.AddedAt;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO watched_themes(theme_code, theme_name, notes, added_at)
                VALUES (@Code, @Name, @Notes, @AddedAt);
                """,
                new
                {
                    Code = normalizedCode,
                    Name = string.IsNullOrWhiteSpace(t.ThemeName) ? normalizedCode : t.ThemeName,
                    t.Notes,
                    AddedAt = addedAt,
                },
                transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            importedThemes++;
            existingThemeCodes.Add(normalizedCode);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ApplyImportResult(
            new PortfolioIoCounts(importedAccounts, importedHoldings, importedGroups, importedItems, importedThemes),
            new PortfolioImportSkipped(accountSkips, holdingSkips, groupSkips, itemSkips, themeSkips));
    }

    static string CurrentSqliteTimestamp() =>
        DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    sealed class WatchlistItemRow
    {
        public long GroupId { get; init; }
        public string Symbol { get; init; } = "";
        public string? Notes { get; init; }
        public string AddedAt { get; init; } = "";
    }

    sealed class AccountIdentityRow
    {
        public string AccountNo { get; init; } = "";
        public string Nickname { get; init; } = "";
        public long Id { get; init; }
    }

    sealed class GroupIdentityRow
    {
        public string Name { get; init; } = "";
        public long Id { get; init; }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StockTheme>>> GetStockThemesBatchAsync(
        IReadOnlyCollection<string> symbols,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        if (symbols.Count == 0)
            return new Dictionary<string, IReadOnlyList<StockTheme>>(0, StringComparer.Ordinal);

        string[] normalized = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length == 0)
            return new Dictionary<string, IReadOnlyList<StockTheme>>(0, StringComparer.Ordinal);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<StockTheme> rows = await connection.QueryAsync<StockTheme>(new CommandDefinition(
            """
            SELECT symbol AS Symbol, theme_code AS ThemeCode, theme_name AS ThemeName, updated_at AS UpdatedAt
            FROM stock_themes
            WHERE symbol IN @Symbols
            ORDER BY symbol, theme_code;
            """,
            new { Symbols = normalized }, cancellationToken: cancellationToken)).ConfigureAwait(false);

        var result = new Dictionary<string, IReadOnlyList<StockTheme>>(StringComparer.Ordinal);
        foreach (IGrouping<string, StockTheme> group in rows.GroupBy(r => r.Symbol, StringComparer.Ordinal))
        {
            result[group.Key] = group.ToList();
        }
        return result;
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
    /// Inserts default seed rows if they are absent. The default watchlist group is preserved
    /// because group-less items would be awkward; accounts are no longer auto-seeded in v0.5 —
    /// the zero-account empty state is a valid first-run state.
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
    /// Shared SELECT prefix for account queries. Append the WHERE / ORDER BY clause inline.
    /// </summary>
    const string AccountSelectSql = """
        SELECT a.id AS Id, a.account_no AS AccountNo, a.nickname AS Nickname, a.broker AS Broker,
               a.is_default AS IsDefault, a.created_at AS CreatedAt
        FROM accounts a
        """;

    /// <summary>
    /// Gets the default account from an open connection. Returns null when no accounts exist.
    /// </summary>
    static Task<Account?> GetDefaultAccountAsync(SqliteConnection connection, CancellationToken cancellationToken, System.Data.IDbTransaction? transaction = null) =>
        connection.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            AccountSelectSql + " WHERE a.is_default = 1 ORDER BY a.id LIMIT 1;",
            transaction: transaction,
            cancellationToken: cancellationToken));

    /// <summary>
    /// Resolves an account by account_number, falling back to nickname when no number matches.
    /// </summary>
    static async Task<Account?> GetAccountByIdentifierAsync(SqliteConnection connection, string identifier, CancellationToken cancellationToken, System.Data.IDbTransaction? transaction = null)
    {
        Account? byNumber = await connection.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            AccountSelectSql + " WHERE a.account_no = @Identifier;",
            new { Identifier = identifier }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (byNumber is not null)
            return byNumber;
        return await connection.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            AccountSelectSql + " WHERE a.nickname = @Identifier;",
            new { Identifier = identifier }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a repository Account to the MCP-facing AccountInfo shape.
    /// </summary>
    internal static AccountInfo ToAccountInfo(Account account) =>
        new(account.AccountNo, account.Nickname, account.Broker, account.IsDefault);

    /// <summary>
    /// Gets a holding from an open connection.
    /// </summary>
    static Task<Holding?> GetHoldingAsync(SqliteConnection connection, long accountId, string symbol, CancellationToken cancellationToken, System.Data.IDbTransaction? transaction = null) =>
        connection.QuerySingleOrDefaultAsync<Holding>(new CommandDefinition(
            """
            SELECT h.id AS Id, h.account_id AS AccountId, h.symbol AS Symbol,
                   s.name AS Name, s.market AS Market,
                   h.quantity AS Quantity, h.avg_price AS AvgPrice, h.notes AS Notes, h.updated_at AS UpdatedAt
            FROM holdings h
            JOIN stocks s ON s.symbol = h.symbol
            WHERE h.account_id = @AccountId AND h.symbol = @Symbol;
            """,
            new { AccountId = accountId, Symbol = symbol }, transaction, cancellationToken: cancellationToken));

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

