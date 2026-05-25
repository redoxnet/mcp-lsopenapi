using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Server;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Server;

/// <summary>
/// Pins <see cref="ServerInstructions.Text"/> — the routing guidance shipped
/// in the MCP <c>initialize</c> response. The exact wording can evolve, but
/// the routing boundary it encodes — LS tools for structured Korean AND
/// US/overseas stock data, web search for news / narrative — must survive
/// any edit.
/// </summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void Text_IsPresentAndReasonablyScoped()
    {
        ServerInstructions.Text.Should().NotBeNullOrWhiteSpace();
        // A system-message-level instruction — comprehensive but not an essay.
        // Budget bumped to 4800 in v1.4 to cover the Q-Click signal catalog
        // paragraph, the envelope-narration guide, the ambiguity-strategy
        // guide, dedupe contract, and the rapid_change noise caveat (~250
        // tokens / ~1700 chars on top of the v1.2 baseline).
        ServerInstructions.Text.Length.Should().BeInRange(200, 4800);
    }

    [Theory]
    [InlineData("KRX/KOSDAQ")]                  // names the Korean data domain
    [InlineData("Nasdaq")]                      // names a US data domain (v1.3)
    [InlineData("ls_search_overseas_stock")]    // overseas resolution entry point (v1.3)
    [InlineData("NOT a web-search question")]   // closes the v1.2 "Korean-only" gap (v1.3)
    [InlineData("prefer these LS")]             // LS tools first for structured data
    [InlineData("web search")]                  // the routing-boundary keyword
    [InlineData("news")]                        // web-search side of the boundary
    [InlineData("why did it move")]             // narrative questions go to web search
    [InlineData("does not provide")]            // the explicit out-of-scope statement
    [InlineData("order placement")]             // trading is out of scope
    [InlineData("local manual notes")]          // portfolio scope is local-only
    [InlineData("ls_list_screeners")]           // Q-Click discovery entry point (v1.4)
    [InlineData("ls_combine_screeners")]        // compound AND/OR screening (v1.4)
    [InlineData("LS-curated catalog")]          // catalog provenance is curated, not user-saved (v1.4)
    [InlineData("data_as_of")]                  // envelope field surfaced in natural language (v1.4)
    [InlineData("query_date_resolution")]       // envelope resolution field (v1.4)
    [InlineData("trails today")]                // KSD lag wording — model must not claim "today's data" when stale (v1.4)
    [InlineData("deduplicates inputs")]         // ls_combine_screeners dedupe contract (v1.4)
    [InlineData("rapid_change group")]          // minute-bucket noise caveat anchor (v1.4)
    public void Text_CarriesTheRoutingBoundaryPhrase(string phrase)
    {
        ServerInstructions.Text.Should().Contain(phrase);
    }
}
