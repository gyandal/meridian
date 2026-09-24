using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;

namespace Meridian.Sample.Weather;

/// <summary>
/// An <see cref="IPointSource"/> backed by the free, no-auth Open-Meteo API — a "customer with loads of
/// data" you don't own. Each city is an entity; daily max temperature is the metric. Proves the exact
/// same engine that ran the in-memory tests works against a real public API with zero engine changes.
/// </summary>
public sealed class HttpWeatherSource(HttpClient http, IReadOnlyDictionary<long, (double Lat, double Lon)> cities)
    : IPointSource
{
    public async Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default)
    {
        var builder = new PointBlock.Builder(metric.Unit);
        foreach (var entity in entities)
        {
            if (!cities.TryGetValue(entity.Id, out var coord)) continue;

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={coord.Lat.ToString(CultureInfo.InvariantCulture)}" +
                      $"&longitude={coord.Lon.ToString(CultureInfo.InvariantCulture)}" +
                      "&daily=temperature_2m_max&timezone=UTC&past_days=30&forecast_days=1";

            var response = await http.GetFromJsonAsync<OpenMeteoResponse>(url, ct).ConfigureAwait(false);
            if (response?.Daily is not { } daily) continue;

            var key = PointKey.Of(KeyPart.Entity(entity.Dimension, entity.Id));
            for (int i = 0; i < daily.Time.Length; i++)
            {
                var date = DateTime.SpecifyKind(
                    DateTime.ParseExact(daily.Time[i], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    DateTimeKind.Utc);
                var at = Instant.FromUtc(date);
                if (at.UtcTicks < timeframe.Start.UtcTicks || at.UtcTicks >= timeframe.End.UtcTicks) continue;

                var temp = daily.TemperatureMax[i];
                builder.Add(key, temp is { } t ? Measurement.Of(t) : Measurement.Missing, at);
            }
        }
        return builder.Build();
    }

    private sealed record OpenMeteoResponse([property: JsonPropertyName("daily")] DailyBlock? Daily);

    private sealed record DailyBlock(
        [property: JsonPropertyName("time")] string[] Time,
        [property: JsonPropertyName("temperature_2m_max")] double?[] TemperatureMax);
}
