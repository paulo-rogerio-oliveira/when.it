using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbSense.Core.Reactions;

// Resolve placeholders ($.after.X, $.before.X, $payload.json, $rule.id, $rule.version)
// dentro de strings de uma config JSON. Retorna a config "resolvida" como string JSON.
public static class PlaceholderExpander
{
    public static string Expand(
        string configJson,
        JsonElement payload,
        Guid ruleId,
        int ruleVersion)
    {
        var node = JsonNode.Parse(configJson);
        if (node is null) return configJson;
        ExpandNode(node, payload, ruleId, ruleVersion);
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static void ExpandNode(JsonNode? node, JsonElement payload, Guid ruleId, int ruleVersion)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kvp in obj.ToList())
                {
                    if (kvp.Value is JsonValue v && v.TryGetValue<string>(out var s))
                        obj[kvp.Key] = ExpandString(s, payload, ruleId, ruleVersion);
                    else
                        ExpandNode(kvp.Value, payload, ruleId, ruleVersion);
                }
                break;
            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s))
                        arr[i] = ExpandString(s, payload, ruleId, ruleVersion);
                    else
                        ExpandNode(arr[i], payload, ruleId, ruleVersion);
                }
                break;
        }
    }

    private static JsonNode? ExpandString(string s, JsonElement payload, Guid ruleId, int ruleVersion)
    {
        if (s == "$payload.json")
            return JsonValue.Create(payload.GetRawText());

        if (s == "$rule.id")
            return JsonValue.Create(ruleId.ToString());

        if (s == "$rule.version")
            return JsonValue.Create(ruleVersion);

        // Aliases pra _meta (compat com shapes gerados pelo InferenceService.BuildShape):
        //   $event.timestamp     → $._meta.captured_at
        //   $trigger.table       → $._meta.table
        //   $trigger.schema      → $._meta.schema
        //   $trigger.operation   → $._meta.operation
        var aliasPath = s switch
        {
            "$event.timestamp"   => "_meta.captured_at",
            "$trigger.table"     => "_meta.table",
            "$trigger.schema"    => "_meta.schema",
            "$trigger.operation" => "_meta.operation",
            _ => null
        };
        if (aliasPath is not null)
        {
            var aliased = ResolvePath(payload, aliasPath);
            return aliased is null
                ? JsonValue.Create((string?)null)
                : JsonNode.Parse(aliased.Value.GetRawText());
        }

        if (s.StartsWith("$.", StringComparison.Ordinal))
        {
            var resolved = ResolvePath(payload, s[2..]);
            if (resolved is null) return JsonValue.Create((string?)null);
            return JsonNode.Parse(resolved.Value.GetRawText());
        }

        return s;
    }

    private static JsonElement? ResolvePath(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(part, out var next)) return null;
            current = next;
        }
        return current;
    }
}
