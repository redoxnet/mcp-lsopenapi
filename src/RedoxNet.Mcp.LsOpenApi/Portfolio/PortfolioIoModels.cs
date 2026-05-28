using System.Text.Json.Serialization;

namespace RedoxNet.Mcp.LsOpenApi.Portfolio;

/// <summary>
/// Versioned portfolio snapshot used by <c>ls_portfolio_io</c>
/// (export / import actions). Ships schema_version=1.
/// </summary>
/// <remarks>
/// Fields and casing follow the spec §3.3 export envelope. Property names
/// rely on the global SnakeCaseLower naming policy; only fields whose
/// snake-cased name diverges from the C# name need an explicit attribute.
/// </remarks>
internal sealed class PortfolioExportDto
{
    public int SchemaVersion { get; set; }
    public string ExportedAt { get; set; } = "";
    public string ExporterVersion { get; set; } = "";
    public List<AccountExportDto> Accounts { get; set; } = new();
    public List<WatchlistGroupExportDto> WatchlistGroups { get; set; } = new();
    public List<WatchedThemeExportDto> WatchedThemes { get; set; } = new();
}

internal sealed class AccountExportDto
{
    public string AccountNumber { get; set; } = "";
    public string Nickname { get; set; } = "";
    public string Broker { get; set; } = "";
    public bool IsDefault { get; set; }
    public string CreatedAt { get; set; } = "";
    public List<HoldingExportDto> Holdings { get; set; } = new();
}

internal sealed class HoldingExportDto
{
    public string Shcode { get; set; } = "";
    public int Quantity { get; set; }
    public double AvgPrice { get; set; }
    public string? Notes { get; set; }
    public string UpdatedAt { get; set; } = "";
}

internal sealed class WatchlistGroupExportDto
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public string CreatedAt { get; set; } = "";
    public List<WatchlistItemExportDto> Items { get; set; } = new();
}

internal sealed class WatchlistItemExportDto
{
    public string Shcode { get; set; } = "";
    public string? Notes { get; set; }
    public string AddedAt { get; set; } = "";
}

internal sealed class WatchedThemeExportDto
{
    public string ThemeCode { get; set; } = "";
    public string ThemeName { get; set; } = "";
    public string? Notes { get; set; }
    public string AddedAt { get; set; } = "";
}

/// <summary>Per-domain row counts used in both export and import envelopes.</summary>
internal sealed record PortfolioIoCounts(
    int Accounts,
    int Holdings,
    int WatchlistGroups,
    int WatchlistItems,
    int WatchedThemes);

/// <summary>Response envelope for <c>ls_portfolio_io(action="export")</c>.</summary>
internal sealed record PortfolioExportResult(
    string Path,
    int SchemaVersion,
    PortfolioIoCounts Counts,
    long SizeBytes);

/// <summary>Response envelope for <c>ls_portfolio_io(action="import")</c>.</summary>
internal sealed record PortfolioImportResult(
    string Mode,
    string SourcePath,
    int SchemaVersion,
    PortfolioIoCounts Imported,
    PortfolioImportSkipped Skipped,
    string? AutoBackupPath);

/// <summary>Per-domain skip lists from the import operation.</summary>
internal sealed record PortfolioImportSkipped(
    IReadOnlyList<AccountSkip> Accounts,
    IReadOnlyList<HoldingSkip> Holdings,
    IReadOnlyList<WatchlistGroupSkip> WatchlistGroups,
    IReadOnlyList<WatchlistItemSkip> WatchlistItems,
    IReadOnlyList<WatchedThemeSkip> WatchedThemes);

internal sealed record AccountSkip(string AccountNumber, string Nickname, string Reason);
internal sealed record HoldingSkip(string AccountNumber, string Shcode, string Reason);
internal sealed record WatchlistGroupSkip(string Name, string Reason);
internal sealed record WatchlistItemSkip(string Group, string Shcode, string Reason);
internal sealed record WatchedThemeSkip(string ThemeCode, string Reason);

/// <summary>
/// Repository-layer result of <c>ApplyImportAsync</c> — counts + skip lists.
/// The service wraps this in <see cref="PortfolioImportResult"/> with the
/// path/mode/auto-backup-path fields once file IO completes.
/// </summary>
internal sealed record ApplyImportResult(PortfolioIoCounts Imported, PortfolioImportSkipped Skipped);
