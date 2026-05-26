using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedoxNet.LsOpenApi.Core;
using RedoxNet.Mcp.LsOpenApi;
using RedoxNet.Mcp.LsOpenApi.Apps;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Server;
using RedoxNet.Mcp.LsOpenApi.Tools;

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

// Opt-in HTTP transport for SEP-1865 verification against the ext-apps
// basic-host (and other Streamable-HTTP MCP clients). Enable via `--http`
// arg or LS_MCP_HTTP env. Port override via LS_MCP_HTTP_URL (default
// http://localhost:3001). The published NuGet package keeps stdio as the
// default — HTTP mode is only reached on explicit opt-in.
//
// This branch (spike/sep1865-verify) is the maintained home of the HTTP
// transport feature. If a future release promotes HTTP to the default or
// main-line surface, the stdio vs HTTP code paths should be unified via a
// shared service-config extension method (the duplication in RunHttpAsync
// below is intentional for opt-in scope).
bool useHttp = args.Any(a => a == "--http") ||
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LS_MCP_HTTP"));
if (useHttp)
{
    return await RunHttpAsync();
}

// MCP stdio server mode.
var builder = Host.CreateApplicationBuilder(Array.Empty<string>());

builder.Logging.AddConsole(options =>
{
    // MCP uses stdout for protocol traffic; logs must go to stderr.
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Opt-in trace verbosity: `LS_LOG_LEVEL=Trace` lets operators capture every
// JSON-RPC message (incoming tools/call payloads included) when diagnosing
// host-side interop issues. Default minimum stays at Information so normal
// runs aren't drowned in framework chatter.
string? logLevelEnv = Environment.GetEnvironmentVariable("LS_LOG_LEVEL");
if (!string.IsNullOrWhiteSpace(logLevelEnv) &&
    Enum.TryParse(logLevelEnv, ignoreCase: true, out LogLevel minLevel))
{
    builder.Logging.SetMinimumLevel(minLevel);
}

builder.Services
    .AddLsOpenApiCore()
    .ConfigureLsOptionsFromEnvironment();

builder.Services.AddPortfolio();
builder.Services.AddSingleton<IndustryDataCache>();

// Tool-surface profile (SPEC-v0.10 §2.4): `standard` (default) hides the
// catalog trio from tools/list; `all` exposes them.
var toolProfile = ToolProfile.FromEnvironment();

builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "mcp-lsopenapi",
            Title = "RedoxNet LS OpenAPI",
            Description = "LS Securities OpenAPI tools - read-only Korean stock market data (TR catalog, quotes, charts, indicators).",
            Version = GetPublicVersion(),
        };
        // Server-level routing guidance surfaced in the initialize response;
        // MCP hosts inject it as a system message (see ServerInstructions).
        options.ServerInstructions = ServerInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    // MCP Apps (SEP-1865): publish the generic Plotly UI template so hosts
    // (Claude Desktop, ChatGPT, Goose, VS Code) can render the inline charts
    // returned by the chart-emitting tools (see UiResources).
    .WithListResourcesHandler(UiResources.ListAsync)
    .WithReadResourceHandler(UiResources.ReadAsync)
    // Tools/list response post-processing:
    //   0. ToolProfile — drops the catalog trio under the `standard` profile.
    //   1. SchemaNormalizer — rewrites `"type": ["X","null"]` to `"X"` so
    //      strict MCP host validators (Ajv / Draft-7 style) accept the
    //      schemas the .NET SDK auto-emits for `T?` parameters.
    //   2. ApplyChartSurface — gated on the resolved ChartRenderingMode
    //      (SPEC v1.2 W3b): a SEP-1865 host gets the _meta.ui envelope, a
    //      legacy chart host keeps include_chart without it, a text-only host
    //      has include_chart stripped from the schema.
    // The tools/call filter handles LS_TOOL_PROFILE_STRICT and W3c — stripping
    // the UI-only structuredContent.chart for text-only hosts.
    .WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next => async (ctx, ct) =>
        {
            var result = await next(ctx, ct);
            if (!toolProfile.IsAll)
                result.Tools = result.Tools.Where(t => toolProfile.IsVisible(t.Name)).ToList();
            var chartMode = ChartHostSupport.Resolve(
                ctx.Server.ClientCapabilities, ctx.Server.ClientInfo);
            foreach (var tool in result.Tools)
            {
                SchemaNormalizer.NormalizeInputSchema(tool);
                UiResources.ApplyChartSurface(tool, chartMode);
            }
            return result;
        });
        filters.AddCallToolFilter(next => async (ctx, ct) =>
        {
            // Strict mode rejects a tools/call for a profile-hidden tool; the
            // default leaves hidden tools internally callable (SPEC-v0.10 §2.4).
            string? name = ctx.Params?.Name;
            if (toolProfile.Strict && name is not null && !toolProfile.IsVisible(name))
                return McpJson.ErrorResult(
                    "Tool not available in the current LS_TOOL_PROFILE.",
                    new { tool = name, profile = "standard", hint = "set LS_TOOL_PROFILE=all to expose catalog tools" });

            var result = await next(ctx, ct);

            // SPEC v1.2 W3c: structuredContent is a generic MCP field — a
            // text-only host feeds it into the model context. Strip the chart
            // payload for those hosts so the text summary the model actually
            // reads survives intact (Spike B, SPEC §6).
            var chartMode = ChartHostSupport.Resolve(
                ctx.Server.ClientCapabilities, ctx.Server.ClientInfo);
            if (chartMode == ChartRenderingMode.TextOnly)
                UiResources.StripChartStructuredContent(result);

            // SPEC v1.5 §2.1: emit a render_status signal on every
            // chart-emitting tool's result so the model can distinguish a
            // delivered chart from one that was stripped. The model uses
            // this to gate narration honesty (no "I drew the chart" on a
            // stripped_text_only response).
            UiResources.AttachRenderStatus(result, chartMode, name);

            return result;
        });
    });

var app = builder.Build();
await app.RunAsync();
return 0;

static bool IsCliCommand(string arg) =>
    (arg.StartsWith('-') && arg != "--http")
                        || string.Equals(arg, "version", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(arg, "help", StringComparison.OrdinalIgnoreCase);

// HTTP transport mode for SEP-1865 verification with ext-apps basic-host and
// other Streamable-HTTP MCP clients. Mirrors the stdio mode's service
// registration; the duplication is intentional for opt-in scope — refactor
// only if HTTP is promoted to the main-line surface.
static async Task<int> RunHttpAsync()
{
    var webBuilder = WebApplication.CreateBuilder(Array.Empty<string>());

    // HTTP mode: normal console logging (stdout is HTTP body, not protocol).
    webBuilder.Logging.AddConsole();
    string? logLevelEnv = Environment.GetEnvironmentVariable("LS_LOG_LEVEL");
    if (!string.IsNullOrWhiteSpace(logLevelEnv) &&
        Enum.TryParse(logLevelEnv, ignoreCase: true, out LogLevel minLevel))
    {
        webBuilder.Logging.SetMinimumLevel(minLevel);
    }

    webBuilder.Services
        .AddLsOpenApiCore()
        .ConfigureLsOptionsFromEnvironment();
    webBuilder.Services.AddPortfolio();
    webBuilder.Services.AddSingleton<IndustryDataCache>();

    var toolProfile = ToolProfile.FromEnvironment();

    webBuilder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new()
            {
                Name = "mcp-lsopenapi",
                Title = "RedoxNet LS OpenAPI",
                Description = "LS Securities OpenAPI tools - read-only Korean stock market data (TR catalog, quotes, charts, indicators).",
                Version = GetPublicVersion(),
            };
            options.ServerInstructions = ServerInstructions.Text;
        })
        // Q4 caveat (MCP-APPS-INTEROP.md): Stateless=true creates a fresh
        // McpServer per HTTP request, so ctx.Server.ClientInfo is null on
        // every tools/list and tools/call — ChartHostSupport.Resolve then
        // always falls through to TextOnly. Stateless=false threads requests
        // to the same instance via Mcp-Session-Id so clientInfo and
        // capabilities survive. Stdio is implicitly stateful so this only
        // affects HTTP transport.
        .WithHttpTransport(o => o.Stateless = false)
        .WithToolsFromAssembly()
        .WithListResourcesHandler(UiResources.ListAsync)
        .WithReadResourceHandler(UiResources.ReadAsync)
        .WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (ctx, ct) =>
            {
                var result = await next(ctx, ct);
                if (!toolProfile.IsAll)
                    result.Tools = result.Tools.Where(t => toolProfile.IsVisible(t.Name)).ToList();
                var chartMode = ChartHostSupport.Resolve(
                    ctx.Server.ClientCapabilities, ctx.Server.ClientInfo);
                foreach (var tool in result.Tools)
                {
                    SchemaNormalizer.NormalizeInputSchema(tool);
                    UiResources.ApplyChartSurface(tool, chartMode);
                }
                return result;
            });
            filters.AddCallToolFilter(next => async (ctx, ct) =>
            {
                string? name = ctx.Params?.Name;
                if (toolProfile.Strict && name is not null && !toolProfile.IsVisible(name))
                    return McpJson.ErrorResult(
                        "Tool not available in the current LS_TOOL_PROFILE.",
                        new { tool = name, profile = "standard", hint = "set LS_TOOL_PROFILE=all to expose catalog tools" });

                var result = await next(ctx, ct);

                // Mirror the stdio path's v1.5 filter chain (SPEC v1.5
                // §2.1): strip the chart payload on TextOnly hosts, then
                // attach _meta.render_status so the model can narrate
                // honestly regardless of which transport delivered the call.
                var chartMode = ChartHostSupport.Resolve(
                    ctx.Server.ClientCapabilities, ctx.Server.ClientInfo);
                if (chartMode == ChartRenderingMode.TextOnly)
                    UiResources.StripChartStructuredContent(result);

                UiResources.AttachRenderStatus(result, chartMode, name);

                return result;
            });
        });

    // CORS for ext-apps basic-host (host on :8080, sandbox on :8081). The
    // basic-host fetches /mcp from the host page origin. Streamable HTTP
    // exposes Mcp-Session-Id for stateful sessions; we run stateless so it's
    // harmless but matches the SDK sample convention.
    webBuilder.Services.AddCors(options =>
    {
        options.AddPolicy("McpBrowserClient", policy =>
            policy.WithOrigins(
                    "http://localhost:8080",
                    "http://localhost:8081",
                    "http://127.0.0.1:8080",
                    "http://127.0.0.1:8081")
                  .AllowAnyMethod()
                  .AllowAnyHeader()
                  .WithExposedHeaders("Mcp-Session-Id"));
    });

    var webApp = webBuilder.Build();
    webApp.UseCors("McpBrowserClient");
    webApp.MapMcp("/mcp");

    string url = Environment.GetEnvironmentVariable("LS_MCP_HTTP_URL") ?? "http://localhost:3001";
    Console.Error.WriteLine($"[http] MCP HTTP server listening on {url}/mcp");
    Console.Error.WriteLine($"[http] basic-host: SERVERS='[\"{url}/mcp\"]' npm run start");
    await webApp.RunAsync(url);
    return 0;
}

static int PrintVersion()
{
    Console.Out.WriteLine(GetPublicVersion());
    return 0;
}

static int PrintUsage()
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  mcp-lsopenapi              Start MCP server (stdio).");
    Console.Error.WriteLine("  mcp-lsopenapi --http       Start MCP server (Streamable HTTP).");
    Console.Error.WriteLine("  mcp-lsopenapi version      Print package version.");
    Console.Error.WriteLine("  mcp-lsopenapi help         Print this message.");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Environment variables:");
    Console.Error.WriteLine("  LS_APPKEY          LS OpenAPI app key (required).");
    Console.Error.WriteLine("  LS_APPSECRETKEY    LS OpenAPI app secret key (required).");
    Console.Error.WriteLine("  LS_MARKET          'real' or 'virtual' (default: real).");
    Console.Error.WriteLine("  LS_BASEURL         Override REST base URL (optional).");
    Console.Error.WriteLine("  LS_LOG_LEVEL       Minimum log level: Trace|Debug|Information|Warning|Error|Critical|None (default: Information).");
    Console.Error.WriteLine("  LS_TOOL_PROFILE    'standard' (default — catalog tools hidden) or 'all'.");
    Console.Error.WriteLine("  LS_TOOL_PROFILE_STRICT  'true' rejects tools/call for profile-hidden tools (default: false).");
    Console.Error.WriteLine("  LSOPENAPI_DB_PATH  Override local portfolio SQLite path (optional).");
    Console.Error.WriteLine("  LS_MCP_HTTP        Any non-empty value enables HTTP transport (same as --http).");
    Console.Error.WriteLine("  LS_MCP_HTTP_URL    HTTP bind URL when --http is active (default: http://localhost:3001).");
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

