using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewPilot.Apprentice.Model;

namespace StardewPilot.Apprentice;

/// <summary>Between tool arguments (JSON) and the routine model's plain values, and from a tool schema to an <see cref="ArgSpec"/>.</summary>
internal static class RoutineJson
{
    public static Dictionary<string, object?> ToPlain(JObject obj) => obj.Properties().ToDictionary(p => p.Name, p => Value(p.Value));

    private static object? Value(JToken t) => t.Type switch
    {
        JTokenType.String => t.Value<string>(),
        JTokenType.Integer => (int)t.Value<long>(),
        JTokenType.Boolean => t.Value<bool>(),
        JTokenType.Array => t.Select(Value).ToList(),
        JTokenType.Object => ToPlain((JObject)t),
        JTokenType.Null => null,
        _ => t.ToString(), // floats and the rest fail the type check as text
    };

    public static JObject ToJson(Dictionary<string, object?> args)
    {
        var obj = new JObject();
        foreach (var (k, v) in args)
            obj[k] = Token(v);
        return obj;
    }

    private static JToken Token(object? v) => v switch
    {
        null => JValue.CreateNull(),
        List<object?> l => new JArray(l.Select(Token)),
        Dictionary<string, object?> d => ToJson(d),
        _ => new JValue(v),
    };

    public static ArgSpec Spec(JObject schema)
    {
        var spec = new ArgSpec();
        if (schema["properties"] is JObject props)
            foreach (var p in props.Properties())
                spec.Props[p.Name] = Type((JObject)p.Value);
        if (schema["required"] is JArray req)
            spec.Required = req.Select(r => r.ToString()).ToHashSet();
        return spec;
    }

    private static ArgType Type(JObject s) => new()
    {
        Type = s.Value<string>("type") ?? "string",
        Enum = (s["enum"] as JArray)?.Select(v => v.ToString()).ToList(),
        Items = s["items"] is JObject items ? Type(items) : null,
        Object = s.Value<string>("type") == "object" ? Spec(s) : null,
    };
}
