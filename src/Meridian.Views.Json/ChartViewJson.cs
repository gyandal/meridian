using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Views;

namespace Meridian.Views.Json;

/// <summary>
/// Source-generated JSON for <see cref="ChartView"/> — no runtime reflection, camelCase, enums as
/// strings, nulls omitted. This IS the wire contract: any front-end deserialises it, and so does the
/// MCP `query` result. No charting-library formats.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(typeof(ChartView))]
public partial class ChartViewJsonContext : JsonSerializerContext;

public static class ChartViewJson
{
    public static string Serialize(ChartView view) =>
        JsonSerializer.Serialize(view, ChartViewJsonContext.Default.ChartView);

    public static ChartView Deserialize(string json) =>
        JsonSerializer.Deserialize(json, ChartViewJsonContext.Default.ChartView)
        ?? throw new JsonException("ChartView JSON deserialised to null.");
}
