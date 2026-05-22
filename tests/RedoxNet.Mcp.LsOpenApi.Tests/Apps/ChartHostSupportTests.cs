using System.Collections.Generic;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Apps;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Apps;

/// <summary>
/// Covers the v1.2 dual-signal chart gating: <see cref="McpAppsCapability.Read"/>
/// parsing the SEP-1865 capability, and <see cref="ChartHostSupport.Resolve"/>
/// combining it with the clientInfo allowlist into a
/// <see cref="ChartRenderingMode"/>.
/// </summary>
public class ChartHostSupportTests
{
    const string UiExtensionId = "io.modelcontextprotocol/ui";
    const string HtmlAppMime = "text/html;profile=mcp-app";

    /// <summary>Builds a <see cref="ClientCapabilities"/> with the MCP Apps UI
    /// extension set to <paramref name="uiExtensionValue"/> (null = no extension).</summary>
    static ClientCapabilities Capabilities(object? uiExtensionValue)
    {
#pragma warning disable MCPEXP001
        return new ClientCapabilities
        {
            Extensions = uiExtensionValue is null
                ? null
                : new Dictionary<string, object> { [UiExtensionId] = uiExtensionValue },
        };
#pragma warning restore MCPEXP001
    }

    static Implementation Client(string name) => new() { Name = name, Version = "1.0.0" };

    // ── McpAppsCapability.Read ──────────────────────────────────────────────

    [Fact]
    public void Read_NullCapabilities_ReturnsNull() =>
        McpAppsCapability.Read(null).Should().BeNull();

    [Fact]
    public void Read_NoExtensions_ReturnsNull() =>
        McpAppsCapability.Read(new ClientCapabilities()).Should().BeNull();

    [Fact]
    public void Read_UiExtensionWithHtmlMime_ParsesObjectValue()
    {
        var cap = McpAppsCapability.Read(
            Capabilities(new { mimeTypes = new[] { HtmlAppMime } }));

        cap.Should().NotBeNull();
        cap!.SupportsHtmlApp.Should().BeTrue();
        cap.MimeTypes.Should().Contain(HtmlAppMime);
    }

    [Fact]
    public void Read_UiExtensionAsJsonElement_ParsesWireShape()
    {
        // Off the wire, the per-extension settings object is a JsonElement.
        JsonElement wire = JsonSerializer.SerializeToElement(
            new { mimeTypes = new[] { HtmlAppMime } });

        var cap = McpAppsCapability.Read(Capabilities(wire));

        cap.Should().NotBeNull();
        cap!.SupportsHtmlApp.Should().BeTrue();
    }

    [Fact]
    public void Read_UiExtensionWithoutHtmlMime_SupportsHtmlAppFalse()
    {
        var cap = McpAppsCapability.Read(
            Capabilities(new { mimeTypes = new[] { "text/plain" } }));

        cap.Should().NotBeNull();
        cap!.SupportsHtmlApp.Should().BeFalse();
    }

    [Fact]
    public void Read_UiExtensionEmptyObject_HasNoMimeTypes()
    {
        var cap = McpAppsCapability.Read(Capabilities(new { }));

        cap.Should().NotBeNull();
        cap!.MimeTypes.Should().BeEmpty();
        cap.SupportsHtmlApp.Should().BeFalse();
    }

    // ── ChartHostSupport.Resolve ────────────────────────────────────────────

    [Fact]
    public void Resolve_Sep1865Capability_ReturnsSep1865()
    {
        var mode = ChartHostSupport.Resolve(
            Capabilities(new { mimeTypes = new[] { HtmlAppMime } }),
            Client("Claude Code"));

        mode.Should().Be(ChartRenderingMode.Sep1865);
    }

    [Fact]
    public void Resolve_NoCapability_AssistStudioClient_ReturnsLegacy()
    {
        var mode = ChartHostSupport.Resolve(new ClientCapabilities(), Client("AssistStudio"));

        mode.Should().Be(ChartRenderingMode.LegacyStructuredContent);
    }

    [Fact]
    public void Resolve_NoCapability_UnknownClient_ReturnsTextOnly()
    {
        var mode = ChartHostSupport.Resolve(new ClientCapabilities(), Client("Claude Code"));

        mode.Should().Be(ChartRenderingMode.TextOnly);
    }

    [Fact]
    public void Resolve_NoCapability_NullClientInfo_ReturnsTextOnly()
    {
        ChartHostSupport.Resolve(new ClientCapabilities(), null)
            .Should().Be(ChartRenderingMode.TextOnly);
    }

    [Fact]
    public void Resolve_CapabilityWinsOverAllowlist()
    {
        // Once AssistStudio advertises the capability it upgrades to SEP-1865 —
        // the dual signal lets the path switch with no server change (SPEC §6).
        var mode = ChartHostSupport.Resolve(
            Capabilities(new { mimeTypes = new[] { HtmlAppMime } }),
            Client("AssistStudio"));

        mode.Should().Be(ChartRenderingMode.Sep1865);
    }

    // ── Cross-repo contract: the capability AssistStudio actually advertises ──

    [Fact]
    public void Resolve_RoundTripsTheCapabilityAssistStudioAdvertises()
    {
        // Mirrors fieldcure-assiststudio McpServerConnection.AddMcpAppsUiCapability:
        //   capabilities.Extensions["io.modelcontextprotocol/ui"]
        //       = new { mimeTypes = new[] { "text/html;profile=mcp-app" } };
        var advertised = new ClientCapabilities();
#pragma warning disable MCPEXP001
        advertised.Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [UiExtensionId] = new { mimeTypes = new[] { HtmlAppMime } },
        };
#pragma warning restore MCPEXP001

        // Round-trip through the SDK's own wire serializer, as the initialize
        // handshake does (client serializes, server deserializes).
        string wire = JsonSerializer.Serialize(advertised, McpJsonUtilities.DefaultOptions);
        var received = JsonSerializer.Deserialize<ClientCapabilities>(
            wire, McpJsonUtilities.DefaultOptions);

        wire.Should().Contain(HtmlAppMime, "the extension must survive serialization");
        ChartHostSupport.Resolve(received, Client("AssistStudio"))
            .Should().Be(ChartRenderingMode.Sep1865,
                "the io.modelcontextprotocol/ui capability AssistStudio advertises must " +
                "resolve to the SEP-1865 path end-to-end");
    }
}
