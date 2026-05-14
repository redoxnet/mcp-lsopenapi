using System.Reflection;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedoxNet.LsOpenApi.Core;
using RedoxNet.Mcp.LsOpenApi.Apps;

// CLI subcommand dispatch (one-shot mode for diagnostics).
if (args.Length > 0 && IsCliCommand(args[0]))
{
    Console.OutputEncoding = Encoding.UTF8;
    return args[0].ToLowerInvariant() switch
    {
        "version" or "--version" or "-v" => PrintVersion(),
        "help" or "--help" or "-h" => PrintUsage(),
        _ => PrintUsage(),
    };
}

// MCP stdio server mode.
var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

builder.Logging.AddConsole(options =>
{
    // MCP uses stdout for protocol traffic; logs must go to stderr.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services
    .AddLsOpenApiCore()
    .ConfigureLsOptionsFromEnvironment();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "mcp-lsopenapi",
            Title = "RedoxNet LS OpenAPI",
            Description = "LS증권 OpenAPI tools — read-only Korean stock market data (TR catalog, quotes, charts, indicators).",
            Version = GetPublicVersion(),
        };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    // MCP Apps (SEP-1865): publish the generic Plotly UI template so hosts
    // (Claude Desktop, ChatGPT, Goose, VS Code) can render inline charts
    // returned by ls_get_chart / ls_get_etf_holdings.
    .WithListResourcesHandler(UiResources.ListAsync)
    .WithReadResourceHandler(UiResources.ReadAsync)
    // Attribute-based tool registration can't express nested _meta.ui — so
    // we attach the SEP-1865 envelope via a tools/list filter that mutates
    // the descriptors for chart-emitting tools just before they ship out.
    .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (ctx, ct) =>
    {
        var result = await next(ctx, ct);
        foreach (var tool in result.Tools)
            UiResources.PatchToolMetaIfChartEmitting(tool);
        return result;
    }));

var app = builder.Build();
await app.RunAsync();
return 0;

static bool IsCliCommand(string arg) =>
    arg.StartsWith('-') || string.Equals(arg, "version", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase);

static int PrintVersion()
{
    Console.Out.WriteLine(GetPublicVersion());
    return 0;
}

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  mcp-lsopenapi              Start MCP server (stdio).");
    Console.Error.WriteLine("  mcp-lsopenapi version      Print package version.");
    Console.Error.WriteLine("  mcp-lsopenapi help         Print this message.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Environment variables:");
    Console.Error.WriteLine("  LS_APPKEY          LS OpenAPI app key (required).");
    Console.Error.WriteLine("  LS_APPSECRETKEY    LS OpenAPI app secret key (required).");
    Console.Error.WriteLine("  LS_MARKET          'real' or 'virtual' (default: virtual).");
    Console.Error.WriteLine("  LS_BASEURL         Override REST base URL (optional).");
    return 0;
}

static string GetPublicVersion()
{
    string? info = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion;
    if (string.IsNullOrEmpty(info))
        return "0.0.0";

    int plus = info.IndexOf('+');
    return plus > 0 ? info[..plus] : info;
}
