namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Base type for portfolio domain errors surfaced as MCP error envelopes.
/// </summary>
internal abstract class PortfolioException : Exception
{
    protected PortfolioException(string code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>Short identifier used as the error envelope's <c>error</c> field.</summary>
    public string Code { get; }
}

/// <summary>
/// Thrown when a write operation runs against an empty account table.
/// </summary>
internal sealed class RequiresAccountException : PortfolioException
{
    public RequiresAccountException(string message) : base("RequiresAccount", message) { }
}

/// <summary>
/// Thrown when a write target spans multiple accounts and the caller did not specify one.
/// </summary>
internal sealed class AmbiguousAccountException : PortfolioException
{
    public AmbiguousAccountException(string message, IReadOnlyList<AccountInfo> candidates)
        : base("AmbiguousAccount", message)
    {
        Candidates = candidates;
    }

    /// <summary>Candidate accounts. Always populated so the caller can disambiguate.</summary>
    public IReadOnlyList<AccountInfo> Candidates { get; }
}

/// <summary>
/// Thrown when an account identifier (nickname or account_number) does not resolve.
/// </summary>
internal sealed class AccountNotFoundException : PortfolioException
{
    public AccountNotFoundException(string identifier, IReadOnlyList<AccountInfo> candidates)
        : base("AccountNotFound", $"Account '{identifier}' was not found.")
    {
        Identifier = identifier;
        Candidates = candidates;
    }

    /// <summary>The unresolved identifier supplied by the caller.</summary>
    public string Identifier { get; }

    /// <summary>Currently known accounts so the caller can retry with a valid identifier.</summary>
    public IReadOnlyList<AccountInfo> Candidates { get; }
}

/// <summary>
/// Thrown when account removal would cascade through saved holdings without explicit confirmation.
/// </summary>
internal sealed class RequiresConfirmationException : PortfolioException
{
    public RequiresConfirmationException(AccountInfo account, int holdingCount, double? marketValue)
        : base("RequiresConfirmation",
               $"Account '{account.Nickname}' has {holdingCount} holding(s). Re-call with confirm=true to cascade-delete.")
    {
        Account = account;
        HoldingCount = holdingCount;
        MarketValue = marketValue;
    }

    public AccountInfo Account { get; }
    public int HoldingCount { get; }
    public double? MarketValue { get; }
}

/// <summary>
/// Thrown when a sell operation requests more shares than the account currently holds.
/// </summary>
internal sealed class InsufficientQuantityException : PortfolioException
{
    public InsufficientQuantityException(string symbol, int currentQuantity, int requestedQuantity, AccountInfo appliedTo)
        : base("InsufficientQuantity",
               $"Cannot sell {requestedQuantity} share(s) of '{symbol}'; current quantity is {currentQuantity}.")
    {
        Symbol = symbol;
        CurrentQuantity = currentQuantity;
        RequestedQuantity = requestedQuantity;
        AppliedTo = appliedTo;
    }

    public string Symbol { get; }
    public int CurrentQuantity { get; }
    public int RequestedQuantity { get; }
    public AccountInfo AppliedTo { get; }
}

/// <summary>
/// Thrown when an argument violates a domain rule that ArgumentException cannot capture cleanly.
/// </summary>
internal sealed class PortfolioValidationException : PortfolioException
{
    public PortfolioValidationException(string message) : base("ValidationError", message) { }
}

/// <summary>
/// Thrown when an import file declares a <c>schema_version</c> outside the
/// range this build supports.
/// </summary>
internal sealed class ImportSchemaMismatchException : PortfolioException
{
    public ImportSchemaMismatchException(int fileSchemaVersion, int supportedSchemaVersion)
        : base("ImportSchemaMismatch",
               $"Import file declares schema_version={fileSchemaVersion}; this build supports up to {supportedSchemaVersion}.")
    {
        FileSchemaVersion = fileSchemaVersion;
        SupportedSchemaVersion = supportedSchemaVersion;
    }

    public int FileSchemaVersion { get; }
    public int SupportedSchemaVersion { get; }
}

/// <summary>
/// Thrown when <c>ls_portfolio_import(mode=replace)</c> is invoked without
/// <c>confirm=true</c>. Replace mode wipes accounts/holdings/watchlist/watched_themes
/// so it always requires an explicit confirmation flag.
/// </summary>
internal sealed class ImportReplaceRequiresConfirmationException : PortfolioException
{
    public ImportReplaceRequiresConfirmationException(string sourcePath, int accountsInFile, int holdingsInFile)
        : base("RequiresConfirmation",
               $"replace mode wipes existing accounts/holdings/watchlists/themes. Re-call with confirm=true to proceed.")
    {
        SourcePath = sourcePath;
        AccountsInFile = accountsInFile;
        HoldingsInFile = holdingsInFile;
    }

    public string SourcePath { get; }
    public int AccountsInFile { get; }
    public int HoldingsInFile { get; }
}
