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
/// <see cref="Time"/> says how the timestamp column is stored (docs/TIME.md); the default is UTC.
/// </summary>
public sealed record DuckDbSourceOptions(
    string ConnectionString,
    string Relation = "datapoints",
    string EntityColumn = "entity_id",
    string MetricColumn = "metric",
    string ValueColumn = "value",
    string TimestampColumn = "ts",
    StoredTime? StoredTime = null)
{
    public StoredTime Time => StoredTime ?? Meridian.Time.StoredTime.Utc;
}

/// <summary>
/// An <see cref="IRollupPointSource"/> over DuckDB. Raw fetches stream rows for the requested entities;
/// rollups push the calendar bucketing into SQL so only one row per (entity, bucket) leaves the database.
///
/// Pushdown covers day, week (any start day) and month buckets with the built-in aggregators, for UTC data
/// in any zone (converted with DuckDB's time-zone support) and for local data as given. Wall-clock data in
/// a zone (<see cref="StoredTime.InZone"/>) and season buckets fall back to a raw fetch, so a pushed-down
/// result always equals the in-engine one.
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
        return QueryAsync(sql, metric, timeframe, entities, o.Time.Axis, raw: true, ct);
    }

    public bool CanRollup(MetricDefinition metric, SourceRollup rollup) =>
        BucketExpression(rollup) is not null && SqlAggregates.ContainsKey(rollup.Aggregator);

    public Task<PointBlock> FetchRollupAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup rollup,
        CancellationToken ct = default)
    {
        var bucket = BucketExpression(rollup)
            ?? throw new NotSupportedException($"Cannot push down a '{rollup.Period.Name}' rollup of {options.Time} data.");
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
            $"CAST({bucket} AS TIMESTAMP) AS bucket " +
            $"FROM {o.Relation} " +
            Where(entities) +
            $" GROUP BY 1, 3 ORDER BY 1, 3";

        // Buckets are local (docs/TIME.md): tagged with the grain, and with the zone that drew the boundaries.
        var zone = o.Time.Kind == TimeKind.Instant ? rollup.Calendar.Zone.Id : null;
        return QueryAsync(sql, metric, timeframe, entities, TimeAxis.Local(rollup.Period.Name, zone), raw: false, ct);
    }

    /// <summary>SQL for the local start of each row's bucket, or null if it can't be computed exactly.</summary>
    private string? BucketExpression(SourceRollup rollup)
    {
        var o = options;
        if (o.Time.NeedsExactFilter) return null; // wall-clock-in-zone data: DST resolution stays in the engine

        string local;
        if (o.Time.Kind == TimeKind.Local || TimeZones.IsUtc(rollup.Calendar.Zone))
        {
            local = o.TimestampColumn;
        }
        else
        {
            // UTC timestamp → that moment's wall clock in the report's zone (DuckDB ICU time zones).
            local = $"timezone('{rollup.Calendar.Zone.Id.Replace("'", "''")}', {o.TimestampColumn} AT TIME ZONE 'UTC')";
        }

        if (ReferenceEquals(rollup.Period, Period.Day)) return $"date_trunc('day', {local})";
        if (ReferenceEquals(rollup.Period, Period.Month)) return $"date_trunc('month', {local})";
        if (ReferenceEquals(rollup.Period, Period.Week))
        {
            // date_trunc('week') starts on Monday; shift by the days from the configured start to Monday.
            int shift = ((int)DayOfWeek.Monday - (int)rollup.Calendar.WeekStart + 7) % 7;
            return shift == 0
                ? $"date_trunc('week', {local})"
                : $"(date_trunc('week', {local} + INTERVAL {shift} DAY) - INTERVAL {shift} DAY)";
        }
        return null; // seasons: domain calendars have no SQL equivalent here
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
        string sql, MetricDefinition metric, DateInterval timeframe, IReadOnlyList<EntityRef> entities,
        TimeAxis axis, bool raw, CancellationToken ct)
    {
        var builder = new PointBlock.Builder(metric.Unit, axis);
        if (entities.Count == 0) return builder.Build();

        var time = options.Time;
        var (start, end) = time.StoredRange(timeframe);

        await using var conn = new DuckDBConnection(options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter("metric", metric.Id.Value));
        cmd.Parameters.Add(new DuckDBParameter("start", start));
        cmd.Parameters.Add(new DuckDBParameter("end", end));

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        PointKey key = default;
        long currentId = long.MinValue;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Raw rows are normalised by the column's StoredTime; rollup buckets are already local wall-clock.
            long ticks = raw ? ReadTicks(reader.GetValue(2), time) : ToDateTime(reader.GetValue(2)).Ticks;
            if (raw && time.NeedsExactFilter && (ticks < timeframe.Start.UtcTicks || ticks >= timeframe.End.UtcTicks)) continue;

            long id = reader.GetInt64(0);
            if (id != currentId)
            {
                currentId = id;
                key = PointKey.Of(KeyPart.Entity(entityDimension, id));
            }
            var measure = reader.IsDBNull(1) ? Measurement.Missing : Measurement.Of(reader.GetDouble(1));
            builder.Add(key, measure, new Instant(ticks));
        }
        return builder.Build();
    }

    private static long ReadTicks(object value, StoredTime time) => value switch
    {
        DateTimeOffset dto => time.ToTicks(dto),
        _ => time.ToTicks(ToDateTime(value)),
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        DateTimeOffset dto => dto.UtcDateTime,
        _ => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
    };
}
