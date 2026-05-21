using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using RedoxNet.Mcp.LsOpenApi.Server;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Server;

/// <summary>
/// Pins the model-facing MCP tool surface for the v1.0.0 freeze. These tests
/// intentionally use the actual <see cref="McpServerToolAttribute"/> metadata
/// rather than a hand-maintained list in docs, so a rename or accidental new
/// tool is caught before the release line hardens.
/// </summary>
public sealed class ToolSurfaceFreezeTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    static readonly string[] StandardToolNames =
    [
        "ls_account",
        "ls_add_indicator",
        "ls_get_analyst_opinions",
        "ls_get_chart",
        "ls_get_etf_holdings",
        "ls_get_etf_info",
        "ls_get_fundamentals_rank",
        "ls_get_global_market_quote",
        "ls_get_high_low_stocks",
        "ls_get_index_history",
        "ls_get_index_quote",
        "ls_get_industry_indices",
        "ls_get_industry_stocks",
        "ls_get_investor_flow",
        "ls_get_market_funds_trend",
        "ls_get_market_warnings",
        "ls_get_multi_quote",
        "ls_get_quote",
        "ls_get_short_selling_trend",
        "ls_get_stock_events",
        "ls_get_stock_info",
        "ls_get_stock_themes",
        "ls_get_theme_stocks",
        "ls_get_top_stocks",
        "ls_holding",
        "ls_holdings_list",
        "ls_portfolio_io",
        "ls_reframe_chart",
        "ls_search_stock",
        "ls_stocks_refresh_metadata",
        "ls_watched_themes",
        "ls_watchlist",
    ];

    static readonly string[] AllToolNames =
    [
        ..StandardToolNames,
        "ls_call_tr",
        "ls_describe_tr",
        "ls_search_tr",
    ];

    [Fact]
    public void StandardProfile_ToolNames_AreFrozen()
    {
        IReadOnlyList<ToolSurface> tools = DiscoverSurface(ToolProfile.Resolve("standard", null));

        tools.Select(t => t.Name).Should().Equal(StandardToolNames.Order(StringComparer.Ordinal));
        tools.Should().HaveCount(32);
    }

    [Fact]
    public void AllProfile_ToolNames_AreFrozen()
    {
        IReadOnlyList<ToolSurface> tools = DiscoverSurface(ToolProfile.Resolve("all", null));

        tools.Select(t => t.Name).Should().Equal(AllToolNames.Order(StringComparer.Ordinal));
        tools.Should().HaveCount(35);
    }

    [Fact]
    public void StandardProfile_HidesOnlyTheCatalogTrio()
    {
        IReadOnlySet<string> standard = DiscoverSurface(ToolProfile.Resolve("standard", null))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);
        IReadOnlySet<string> all = DiscoverSurface(ToolProfile.Resolve("all", null))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        all.Except(standard, StringComparer.Ordinal)
            .Should().BeEquivalentTo(ToolProfile.CatalogTools);
    }

    [Fact]
    public void FrozenRowCapTools_UseLimitParameter()
    {
        IReadOnlyDictionary<string, ToolSurface> tools = DiscoverSurface(ToolProfile.Resolve("all", null))
            .ToDictionary(t => t.Name, StringComparer.Ordinal);

        string[] limitTools =
        [
            "ls_get_etf_holdings",
            "ls_get_top_stocks",
            "ls_get_high_low_stocks",
            "ls_get_industry_indices",
            "ls_get_industry_stocks",
            "ls_get_theme_stocks",
            "ls_get_fundamentals_rank",
            "ls_get_market_warnings",
        ];

        foreach (string toolName in limitTools)
        {
            ToolSurface tool = tools[toolName];
            tool.Parameters.Select(p => p.Name).Should().Contain("limit", $"{toolName} should use the frozen row-cap name");
            tool.Parameters.Select(p => p.Name).Should().NotContain("top_n", $"{toolName} should not expose the pre-v1 row-cap name");
            tool.Parameters.Select(p => p.Name).Should().NotContain("count", $"{toolName} should not expose the pre-v1 row-cap name");
        }
    }

    [Fact]
    public void StandardProfile_Surface_FitsTokenBudget()
    {
        string json = SerializeSurface(ToolProfile.Resolve("standard", null));

        // Reflection-surface proxy for tools/list. The historical v0.10
        // standard tools/list was about 60k chars; this budget catches
        // description/schema creep while leaving room for minor wording fixes.
        json.ShouldFitTokenBudget(30000);
    }

    [Fact]
    public void AllProfile_Surface_FitsTokenBudget()
    {
        string json = SerializeSurface(ToolProfile.Resolve("all", null));

        json.ShouldFitTokenBudget(34000);
    }

    static string SerializeSurface(ToolProfile profile) =>
        JsonSerializer.Serialize(new { tools = DiscoverSurface(profile) }, JsonOptions);

    static IReadOnlyList<ToolSurface> DiscoverSurface(ToolProfile profile)
    {
        return typeof(GetQuoteTool).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item => ToSurface(item.Method, item.Attribute!))
            .Where(tool => profile.IsVisible(tool.Name))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();
    }

    static ToolSurface ToSurface(MethodInfo method, McpServerToolAttribute attribute)
    {
        string name = attribute.Name ?? method.Name;
        string description = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
        ParameterSurface[] parameters = method.GetParameters()
            .Where(IsModelFacingParameter)
            .Select(ToParameterSurface)
            .OrderBy(parameter => parameter.Name, StringComparer.Ordinal)
            .ToArray();

        return new ToolSurface(name, description, parameters);
    }

    static bool IsModelFacingParameter(ParameterInfo parameter) =>
        parameter.ParameterType != typeof(CancellationToken)
        && parameter.GetCustomAttribute<DescriptionAttribute>() is not null;

    static ParameterSurface ToParameterSurface(ParameterInfo parameter)
    {
        string? defaultValue = parameter.HasDefaultValue
            ? parameter.DefaultValue switch
            {
                null => null,
                string value => value,
                bool value => value.ToString(),
                int value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                double value => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => parameter.DefaultValue?.ToString(),
            }
            : null;

        return new ParameterSurface(
            parameter.Name ?? "",
            ToFriendlyTypeName(parameter.ParameterType),
            parameter.HasDefaultValue,
            defaultValue,
            parameter.GetCustomAttribute<DescriptionAttribute>()!.Description);
    }

    static string ToFriendlyTypeName(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare.IsArray)
            return ToFriendlyTypeName(bare.GetElementType()!) + "[]";
        return bare.Name;
    }

    sealed record ToolSurface(string Name, string Description, IReadOnlyList<ParameterSurface> Parameters);

    sealed record ParameterSurface(
        string Name,
        string Type,
        bool HasDefault,
        string? Default,
        string Description);
}
