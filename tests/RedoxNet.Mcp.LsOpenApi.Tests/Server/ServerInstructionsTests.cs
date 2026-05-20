using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Server;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Server;

/// <summary>
/// Pins <see cref="ServerInstructions.Text"/> — the routing guidance shipped
/// in the MCP <c>initialize</c> response. The exact wording can evolve, but
/// the routing boundary it encodes — LS tools for structured Korean-market
/// data, web search for news / narrative — must survive any edit.
/// </summary>
public sealed class ServerInstructionsTests
{
    [Fact]
    public void Text_IsPresentAndReasonablyScoped()
    {
        ServerInstructions.Text.Should().NotBeNullOrWhiteSpace();
        // A system-message-level instruction — comprehensive but not an essay.
        ServerInstructions.Text.Length.Should().BeInRange(200, 1500);
    }

    [Theory]
    [InlineData("KRX/KOSDAQ")]          // names the data domain
    [InlineData("prefer these LS")]     // LS tools first for structured data
    [InlineData("web search")]          // the routing-boundary keyword
    [InlineData("news")]                // web-search side of the boundary
    [InlineData("why did it move")]     // narrative questions go to web search
    [InlineData("does not provide")]    // the explicit out-of-scope statement
    [InlineData("order placement")]     // trading is out of scope
    [InlineData("local manual notes")]  // portfolio scope is local-only
    public void Text_CarriesTheRoutingBoundaryPhrase(string phrase)
    {
        ServerInstructions.Text.Should().Contain(phrase);
    }
}
