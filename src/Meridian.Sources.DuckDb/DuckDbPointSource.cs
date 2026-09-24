using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;

namespace Meridian.Sources.DuckDb;

/// <summary>
/// Maps a metric fetch onto a DuckDB relation. <see cref="Relation"/> is a SQL fragment from trusted
/// configuration — a table or view name, or a table function such as
/// <c>read_parquet('data/*.parquet')</c>, which lets Meridian query Parquet files in place.
/// The timestamp column is expected to be a <c>TIMESTAMP</c> holding UTC.
/// </summary>
public sealed record DuckDbSourceOptions(
    string ConnectionString,
    string Relation = "datapoints",
    string EntityColumn = "entity_id",
    string MetricColumn = "metric",
    string ValueColumn = "value",
    string TimestampColumn = "ts");

/// <summary>
/// An <see cref="IRollupPointSource"/> over DuckDB. Raw fetches stream rows for the requested
/// entities; rollups push the time-bucket aggregation into SQL so only one row per (entity, bucket)
/// leaves the database. Rollup support is deliberately narrow — UTC buckets of day, Monday-start
/// week, or month, with the built-in aggregators — so a pushed-down result always equals the
/// in-engine one; anything else falls back to a raw fetch.
/// </summary>
public sealed class DuckDbPointSource(DuckDbSourceOptions options, DimensionId entityDimension) : IRollupPointSource
{
    private static readonly Dictionary<IAggregator, string> SqlAggregates = new()
    {
        [Aggregators.Mean] = "avg({v})",
        [Aggregators.Sum] = "sum({v})",
        [Aggregators.Min] = "min({v})",
        [Aggregators.Max] = "max({v})",
        [Aggregators.Count] = "count({v})",
        [Aggregators.Median] = "median({v})",
        [Aggregators.Last] = "arg_max({v}, {t}) FILTER (WHERE {v} IS NOT NULL)",
    };

    public Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default)
    {
        var o = options;
        var sql =
            $"SELECT {o.EntityColumn}, {o.ValueColumn}, {o.TimestampColumn} FROM {o.Relation} " +
            Where(entities) +
            $" ORDER BY {o.EntityColumn}, {o.TimestampColumn}";
        return QueryAsync(sql, metric, timeframe, entities, ct);
    }

    public bool CanRollup(MetricDefinition metric, SourceRollup rollup) =>
        BucketUnit(rollup) is not null && SqlAggregates.ContainsKey(rollup.Aggregator);

    public Task<PointBlock> FetchRollupAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup rollup,
        CancellationToken ct = default)
    {
        var unit = BucketUnit(rollup)
            ?? throw new NotSupportedException($"Cannot push down a '{rollup.Period.Name}' rollup in zone '{rollup.Calendar.Zone.Id}'.");
        if (!SqlAggregates.TryGetValue(rollup.Aggregator, out var template))
        {
            throw new NotSupportedException($"Cannot push down aggregator '{rollup.Aggregator.Name}'.");
        }

        var o = options;
        var aggregate = template.Replace("{v}", o.ValueColumn).Replace("{t}", o.TimestampColumn);
        // A bucket whose values are all NULL must come back as missing (not 0 or dropped) to match the engine.
        var sql =
            $"SELECT {o.EntityColumn}, " +
            $"CASE WHEN count({o.ValueColumn}) = 0 THEN NULL ELSE CAST({aggregate} AS DOUBLE) END, " +
            $"CAST(date_trunc('{unit}', {o.TimestampColumn}) AS TIMESTAMP) AS bucket " +
            $"FROM {o.Relation} " +
            Where(entities) +
            $" GROUP BY 1, 3 ORDER BY 1, 3";
        return QueryAsync(sql, metric, timeframe, entities, ct);
    }

    private static string? BucketUnit(SourceRollup rollup)
    {
        if (rollup.Calendar.Zone.Id != TimeZoneInfo.Utc.Id) return null;
        if (ReferenceEquals(rollup.Period, Period.Day)) return "day";
        if (ReferenceEquals(rollup.Period, Period.Month)) return "month";
        if (ReferenceEquals(rollup.Period, Period.Week) && rollup.Calendar.WeekStart == DayOfWeek.Monday) return "week";
        return null;
    }

    private string Where(IReadOnlyList<EntityRef> entities)
    {
        var o = options;
        var sb = new StringBuilder();
        sb.Append($"WHERE {o.MetricColumn} = $metric AND {o.TimestampColumn} >= $start AND {o.TimestampColumn} < $end AND {o.EntityColumn} IN (");
        for (int i = 0; i < entities.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(entities[i].Id.ToString(CultureInfo.InvariantCulture)); // numeric ids: safe to inline
        }
        return sb.Append(')').ToString();
    }

    private async Task<PointBlock> QueryAsync(
        string sql, MetricDefinition metric, DateInterval timeframe, IReadOnlyList<EntityRef> entities, CancellationToken ct)
    {
        var builder = new PointBlock.Builder(metric.Unit);
        if (entities.Count == 0) return builder.Build();

        await using var conn = new DuckDBConnection(options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter("metric", metric.Id.Value));
        cmd.Parameters.Add(new DuckDBParameter("start", timeframe.Start.UtcDateTime));
        cmd.Parameters.Add(new DuckDBParameter("end", timeframe.End.UtcDateTime));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        PointKey key = default;
        long currentId = long.MinValue;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            if (id != currentId)
            {
                currentId = id;
                key = PointKey.Of(KeyPart.Entity(entityDimension, id));
            }
            var measure = reader.IsDBNull(1) ? Measurement.Missing : Measurement.Of(reader.GetDouble(1));
            var at = Instant.FromUtc(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
            builder.Add(key, measure, at);
        }
        return builder.Build();
    }
}
