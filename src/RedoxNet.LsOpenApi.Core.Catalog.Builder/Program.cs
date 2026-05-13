using System.Text;

namespace RedoxNet.LsOpenApi.Core.Catalog.Builder;

/// <summary>
/// Dev-only entry point for the LS OpenAPI TR catalog scraper.
/// </summary>
/// <remarks>
/// This tool is not packaged or shipped. It is invoked manually when LS
/// publishes API changes; the regenerated <c>TrCatalog.json</c> is committed
/// to the Core project as an embedded resource.
/// </remarks>
public static class Program
{
    /// <summary>
    /// Entry point. Accepts <c>--output &lt;path&gt;</c> to control where the
    /// generated catalog JSON is written.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>0 on success, non-zero on failure.</returns>
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        string outputPath = ResolveOutputPath(args)
                            ?? Path.Combine(
                                AppContext.BaseDirectory,
                                "..", "..", "..", "..", "RedoxNet.LsOpenApi.Core",
                                "Catalog", "TrCatalog.json");

        Console.Error.WriteLine($"[catalog-builder] Output: {Path.GetFullPath(outputPath)}");
        Console.Error.WriteLine("[catalog-builder] Scraper not implemented yet (M2).");

        await Task.CompletedTask;
        return 0;
    }

    static string? ResolveOutputPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--output", StringComparison.OrdinalIgnoreCase)
                || string.Equals(args[i], "-o", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }
        return null;
    }
}
