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
        // Budget bumped to 10000 in v1.6 to cover the two-sources
        // disambiguation paragraph + v1.7 non-goal callout (SPEC v1.6 §4).
        // The paragraph is a system-message contract; trimming it risks the
        // model conflating ls_holdings_list (local paper) with
        // ls_account_holdings (live broker) — the exact confusion the v1.6
        // routing language is meant to prevent.
        ServerInstructions.Text.Length.Should().BeInRange(200, 10000);
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
    [InlineData("does not provide news")]       // news discovery still out of scope (v1.6 narrowed wording)
    [InlineData("Order placement is NOT supported in v1.6")] // v1.6 explicitly defers placement to v1.7
    [InlineData("ls_holdings_list")]            // local-portfolio entry point (v1.6 disambiguation anchor)
    [InlineData("ls_account_holdings")]         // LS-live entry point (v1.6 disambiguation anchor)
    [InlineData("TWO distinct sources")]        // the routing instruction the v1.6 paragraph builds (v1.6)
    [InlineData("read-only inquiry")]           // v1.6 scope statement (v1.6)
    [InlineData("LS_MARKET")]                   // mode-routing anchor (v1.6)
    [InlineData("모의투자")]                     // virtual / paper account label users actually type (v1.6)
    [InlineData("paper portfolio")]             // local-source positioning preserved across rewording
    [InlineData("ls_list_screeners")]           // Q-Click discovery entry point (v1.4)
    [InlineData("ls_combine_screeners")]        // compound AND/OR screening (v1.4)
    [InlineData("LS-curated catalog")]          // catalog provenance is curated, not user-saved (v1.4)
    [InlineData("data_as_of")]                  // envelope field surfaced in natural language (v1.4, kept in v1.6)
    [InlineData("trails today")]                // KSD lag wording — model must not claim "today's data" when stale (v1.4, kept in v1.6)
    [InlineData("LLM's clock + calendar")]      // v1.6 explicit handoff of session/holiday classification to the model (v1.6 §1.3)
    [InlineData("deduplicates inputs")]         // ls_combine_screeners dedupe contract (v1.4)
    [InlineData("rapid_change group")]          // minute-bucket noise caveat anchor (v1.4)
    // v1.5 narration-honesty + anti-synthesis paragraph (SPEC v1.5 §2.2 + §2.3).
    [InlineData("render_status")]               // the meta field name itself — model must read it (v1.5)
    [InlineData("delivered")]                   // render_status value: chart shown inline (v1.5)
    [InlineData("stripped_text_only")]          // render_status value: chart NOT shown (v1.5)
    [InlineData("Do not self-synthesize")]      // the absolute prohibition (v1.5)
    [InlineData("regardless of `render_status`")] // self-synthesis ban applies in both modes (v1.5)
    [InlineData("ls_add_indicator")]            // canonical customization path: add indicator (v1.5)
    [InlineData("ls_reframe_chart")]            // canonical customization path: reframe (v1.5)
    [InlineData("host panel constraint")]       // layout-tweak honest narration (v1.5)
    [InlineData("output_mode=export")]          // the term used in the do-not-route-around context (v1.5)
    [InlineData("analysis_only")]               // data_purpose value model sees on export responses (v1.5)
    [InlineData("Python")]                      // explicit named rendering path the model must NOT take (v1.5)
    [InlineData("JavaScript")]                  // ditto (v1.5)
    [InlineData("PNG")]                         // ditto (v1.5)
    [InlineData("recompute")]                   // server-side indicator authority is non-negotiable (v1.5)
    // v1.5.1 chart theme override surface — see docs/MCP-APPS-INTEROP.md §3 Q8.
    [InlineData("theme=\"dark\"")]               // explicit tool-mediated theme override (v1.5.1)
    public void Text_CarriesTheRoutingBoundaryPhrase(string phrase)
    {
        ServerInstructions.Text.Should().Contain(phrase);
    }

    [Fact]
    public void Text_DropsTheV14WrapAndRouteHtmlScaffold()
    {
        // v1.5 removes the v1.4 wrap-and-route fallback that handed a Plotly
        // HTML scaffold to a peer visualize MCP. The new paragraph forbids
        // that route, so the scaffold must not survive — leaving it would
        // re-open the synthesis surface the v1.5 paragraph is closing.
        // (mcp__visualize__show_widget is still mentioned, but only as a
        // forbidden example in the new self-synthesis ban.)
        ServerInstructions.Text.Should().NotContain("Plotly.newPlot");
    }
}
