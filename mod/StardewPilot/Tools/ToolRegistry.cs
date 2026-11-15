using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewPilot.Bridge;

namespace StardewPilot.Tools;

/// <summary>A tool the agent can call. Shape matches an MCP tool definition.</summary>
internal sealed class ToolDef
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public JObject InputSchema { get; init; } = Schema.Object();

    /// <summary>True for tools that only read state.</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>True if the tool needs a loaded save.</summary>
    public bool NeedsSave { get; init; } = true;

    /// <summary>Runs on the game's main thread. Return any JSON-serializable value.</summary>
    public Func<JObject, object?>? Handler { get; init; }

    /// <summary>For long actions: runs off the main thread and may await main-thread work itself.</summary>
    public Func<JObject, Task<object?>>? AsyncHandler { get; init; }
}

/// <summary>An error the agent should see as a tool failure, with a message it can act on.</summary>
internal sealed class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

internal sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDef> tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IMonitor monitor;

    public ToolRegistry(IMonitor monitor) => this.monitor = monitor;

    public void Add(ToolDef tool) => this.tools[tool.Name] = tool;

    public IEnumerable<ToolDef> All => this.tools.Values;

    public object List() => new
    {
        tools = this.tools.Values.OrderBy(t => t.Name).Select(t => new
        {
            name = t.Name,
            description = t.Description,
            inputSchema = t.InputSchema,
            annotations = new { readOnlyHint = t.ReadOnly },
        }),
    };

    /// <summary>Handle an MCP tools/call request. Failures come back as isError results, not protocol errors.</summary>
    public async Task<object?> Call(JObject? parameters)
    {
        string name = parameters?.Value<string>("name") ?? "";
        JObject args = parameters?["arguments"] as JObject ?? new JObject();
        if (!this.tools.TryGetValue(name, out ToolDef? tool))
            return Result($"Unknown tool '{name}'.", isError: true);

        try
        {
            object? value;
            if (tool.AsyncHandler != null)
            {
                if (tool.NeedsSave && !await MainThread.Run(() => Context.IsWorldReady))
                    return Result("No save is loaded. Ask the player to load a save first.", isError: true);
                value = await tool.AsyncHandler(args);
            }
            else
            {
                value = await MainThread.Run(() =>
                {
                    if (tool.NeedsSave && !Context.IsWorldReady)
                        throw new ToolError("No save is loaded. Ask the player to load a save first.");
                    return tool.Handler!(args);
                });
            }
            string text = value as string ?? JsonConvert.SerializeObject(value, JsonSettings);
            return Result(text, isError: false);
        }
        catch (ToolError ex)
        {
            return Result(ex.Message, isError: true);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Tool {name} failed: {ex}", LogLevel.Warn);
            return Result($"Tool failed inside the game: {ex.Message}", isError: true);
        }
    }

    private static object Result(string text, bool isError) => new
    {
        content = new[] { new { type = "text", text } },
        isError,
    };

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        NullValueHandling = NullValueHandling.Ignore,
        ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
        Formatting = Formatting.None,
    };
}

/// <summary>Small helpers for writing JSON Schema by hand.</summary>
internal static class Schema
{
    public static JObject Object(params (string name, JObject schema, bool required)[] props)
    {
        var properties = new JObject();
        var required = new JArray();
        foreach (var (name, schema, isRequired) in props)
        {
            properties[name] = schema;
            if (isRequired)
                required.Add(name);
        }
        var obj = new JObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
        if (required.Count > 0)
            obj["required"] = required;
        return obj;
    }

    public static JObject String(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JObject Enum(string description, params string[] values) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JArray(values) };
}

internal static class ArgExtensions
{
    public static string? Str(this JObject args, string name) =>
        args[name]?.Type == JTokenType.String ? args.Value<string>(name) : args[name]?.ToString();

    public static int? Int(this JObject args, string name) =>
        args[name] is { } t && t.Type is JTokenType.Integer or JTokenType.Float or JTokenType.String && int.TryParse(t.ToString(), out int v) ? v : null;

    public static bool? Bool(this JObject args, string name) =>
        args[name] is { } t && bool.TryParse(t.ToString(), out bool v) ? v : null;
}
