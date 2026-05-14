using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace RedoxNet.Mcp.LsOpenApi.Server;

/// <summary>
/// Rewrites a tool's published <see cref="Tool.InputSchema"/> to drop the
/// JSON Schema 2020-12 nullable-as-type form (e.g. <c>"type": ["array","null"]</c>)
/// that the .NET MCP SDK auto-emits for <c>T?</c> parameters.
/// </summary>
/// <remarks>
/// <para>
/// Some MCP hosts validate tool inputs with strict Ajv / Draft-7 settings and
/// reject the type-as-array nullable form, even though it is valid JSON
/// Schema 2020-12. Optional parameters do not need <c>"null"</c> in their
/// <c>type</c>: the host simply omits the key, which is already captured by
/// the property's absence from <c>required</c>.
/// </para>
/// <para>
/// This normalizer runs through a <c>tools/list</c> request filter so the
/// attribute-based tool registration stays untouched. The resulting schema
/// is semantically equivalent for optional parameters and keeps interop
/// smooth across host validators.
/// </para>
/// <para>
/// Transformations applied to each entry under <c>inputSchema.properties</c>:
/// </para>
/// <list type="bullet">
///   <item><description><c>"type": ["X","null"]</c> → <c>"type": "X"</c>.</description></item>
///   <item><description><c>"type": ["X","Y","null"]</c> → <c>"type": ["X","Y"]</c>.</description></item>
///   <item><description><c>items</c> sub-schemas are normalized recursively.</description></item>
///   <item><description><c>"default": null</c> is removed once the type no longer admits null.</description></item>
/// </list>
/// </remarks>
internal static class SchemaNormalizer
{
    /// <summary>
    /// Normalizes <paramref name="tool"/>'s input schema in place. No-op when
    /// the schema is missing or not an object.
    /// </summary>
    /// <param name="tool">Tool descriptor whose <see cref="Tool.InputSchema"/> will be rewritten.</param>
    public static void NormalizeInputSchema(Tool tool)
    {
        if (tool is null) return;
        JsonElement schema = tool.InputSchema;
        if (schema.ValueKind != JsonValueKind.Object) return;

        JsonNode? node = JsonNode.Parse(schema.GetRawText());
        if (node is not JsonObject root) return;

        bool changed = NormalizeProperties(root);
        if (!changed) return;

        // The InputSchema setter validates that the root remains a {"type":"object"} schema.
        // We only touch property sub-schemas so the root invariant is preserved.
        tool.InputSchema = JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// Walks <c>properties</c> and rewrites each property sub-schema's
    /// <c>type</c> / <c>items</c> / <c>default</c> as documented on the class.
    /// </summary>
    /// <returns><see langword="true"/> if any change was made; lets the caller skip the round-trip when not needed.</returns>
    static bool NormalizeProperties(JsonObject root)
    {
        if (root["properties"] is not JsonObject props) return false;

        bool changed = false;
        foreach (KeyValuePair<string, JsonNode?> entry in props)
        {
            if (entry.Value is JsonObject prop)
                changed |= NormalizeSubSchema(prop);
        }
        return changed;
    }

    /// <summary>
    /// Rewrites a single sub-schema. Handles <c>type</c> arrays containing
    /// <c>"null"</c> and recurses into <c>items</c> for array element schemas.
    /// </summary>
    /// <returns><see langword="true"/> when the sub-schema was modified.</returns>
    static bool NormalizeSubSchema(JsonObject schema)
    {
        bool changed = false;

        // Recurse into "items" first so array-of-nullable-strings gets normalized too.
        if (schema["items"] is JsonObject items)
            changed |= NormalizeSubSchema(items);

        if (schema["type"] is not JsonArray typeArr) return changed;

        // Split the type array into null-vs-non-null entries.
        var nonNull = new List<JsonNode?>(typeArr.Count);
        bool hadNull = false;
        foreach (JsonNode? entry in typeArr)
        {
            if (entry is JsonValue v && v.TryGetValue(out string? s) && s == "null")
            {
                hadNull = true;
                continue;
            }
            nonNull.Add(entry?.DeepClone());
        }

        if (!hadNull || nonNull.Count == 0)
            return changed; // Nothing to rewrite, or only ["null"] (invalid — leave alone).

        schema["type"] = nonNull.Count == 1
            ? nonNull[0]
            : new JsonArray(nonNull.ToArray());

        // "default": null no longer matches the now-non-nullable type. Drop it
        // so strict validators don't complain about default/type mismatch.
        if (schema.ContainsKey("default") && schema["default"] is null)
            schema.Remove("default");

        return true;
    }
}
