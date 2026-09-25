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
/// Pushdown covers fixed sub-daily, day, week (any start day) and month buckets with the built-in aggregators, in any
/// report zone, for UTC data, wall-clock data in a zone (<see cref="StoredTime.InZone"/>, unless its DST
/// policy rejects times) and local data as given. Zone conversions are generated from NodaTime's rules
/// (<see cref="ZoneSql"/>), so a pushed-down result always equals the in-engine one; seasons fall back
/// to a raw fetch.
/// </summary>
public sealed class DuckDbPointSource(DuckDbSourceOptions options, DimensionId entityDimension) : IRollupPointSource, IDisposable
{
    // An in-memory database (e.g. one that queries Parquet files) is created once for the source's lifetime
    // and each query gets a duplicated connection to it: creating a DuckDB database costs far more than a
    // query over it, and ':memory:' would otherwise be a new, empty database every time. File databases are
    // opened per query; DuckDB.NET shares the underlying instance within a process.
    private readonly bool _inMemory = new DuckDBConnectionStringBuilder { ConnectionString = options.ConnectionString }
        .DataSource.StartsWith(":memory:", StringComparison.Ordinal);
    private readonly Lazy<DuckDBConnection> _root = new(() =>
    {
        var root = new DuckDBConnection(options.ConnectionString);
        root.Open();
        return root;
    });
    private readonly Lock _gate = new();

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
            // Values may be DECIMAL or INTEGER in real schemas (fares, counts): DuckDB casts, we read doubles.
            $"SELECT {o.EntityColumn}, CAST({o.ValueColumn} AS DOUBLE), {o.TimestampColumn} FROM {o.Relation} " +
            Where(entities) +
            $" ORDER BY {o.EntityColumn}, {o.TimestampColumn}";
        return QueryAsync(sql, metric, timeframe, entities, o.Time.Axis, raw: true, ct);
    }

    public bool CanRollup(MetricDefinition metric, SourceRollup rollup)
    {
        var time = options.Time;
        bool wallClockConvertible = time.Zone is null
            || (time.Resolution!.Ambiguous != AmbiguousTime.Reject && time.Resolution.Skipped != SkippedTime.Reject);
        return wallClockConvertible && Truncate(rollup, "x") is not null && SqlAggregates.ContainsKey(rollup.Aggregator);
    }

    public Task<PointBlock> FetchRollupAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup rollup,
        CancellationToken ct = default)
    {
        if (!CanRollup(metric, rollup))
        {
            throw new NotSupportedException($"Cannot push down a '{rollup.Period.Name}' / '{rollup.Aggregator.Name}' rollup of {options.Time} data.");
        }

        var o = options;
        var time = o.Time;
        var ts = o.TimestampColumn;

        // Every row's moment in UTC (instants) — converted from wall clock in SQL when the column is InZone —
        // then its wall clock in the report's zone. Local data is bucketed exactly as stored.
        string? utc = time.Kind == TimeKind.Local ? null
            : time.Zone is { } storedZone ? ZoneSql.WallToUtc(ts, storedZone, time.Resolution!, timeframe)
            : ts;
        string local = utc is null ? ts
            // Same zone in and out, day-or-longer buckets: the round trip can't change the bucket, so skip it
            // (see ZoneSql). Sub-daily buckets need it — a skipped time shifts forward into the next hour.
            : time.Zone is { } z && z.Id == rollup.Calendar.Zone.Id && rollup.Period is not FixedPeriod
                && ZoneSql.RoundTripKeepsDays(z, timeframe) ? ts
            : TimeZones.IsUtc(rollup.Calendar.Zone) ? utc
            : ZoneSql.UtcToWall(utc, rollup.Calendar.Zone, timeframe);

        var bucket = Truncate(rollup, local)!;
        var aggregate = SqlAggregates[rollup.Aggregator].Replace("{v}", o.ValueColumn).Replace("{t}", utc ?? ts);

        // InZone rows were pre-filtered on a widened wall-clock range; keep exactly the timeframe's instants —
        // on the stored column when that is provably equivalent (it prunes Parquet row groups and avoids
        // converting every row), otherwise on the converted instants.
        string exact = "";
        (DateTime Start, DateTime End)? wallRange = null;
        if (time.NeedsExactFilter)
        {
            wallRange = ZoneSql.WallRange(time.Zone!, timeframe);
            exact = wallRange is null
                ? $" AND {utc} >= $utcStart AND {utc} < $utcEnd"
                : $" AND {ts} >= $wallStart AND {ts} < $wallEnd";
        }

        // A bucket whose values are all NULL must come back as missing (not 0 or dropped) to match the engine.
        var sql =
            $"SELECT {o.EntityColumn}, " +
            $"CASE WHEN count({o.ValueColumn}) = 0 THEN NULL ELSE CAST({aggregate} AS DOUBLE) END, " +
            $"CAST({bucket} AS TIMESTAMP) AS bucket " +
            $"FROM {o.Relation} " +
            Where(entities) + exact +
            $" GROUP BY 1, 3 ORDER BY 1, 3";

        // Buckets are local (docs/TIME.md): tagged with the grain, and with the zone that drew the boundaries.
        var zone = time.Kind == TimeKind.Instant ? rollup.Calendar.Zone.Id : null;
        return QueryAsync(sql, metric, timeframe, entities, TimeAxis.Local(rollup.Period.Name, zone), raw: false, ct, wallRange);
    }

    /// <summary>SQL for the start of the bucket containing the wall-clock expression, or null for periods
    /// with no SQL equivalent (domain seasons).</summary>
    private static string? Truncate(SourceRollup rollup, string local)
    {
        if (rollup.Period is FixedPeriod fixedPeriod)
        {
            // Spans divide a day, and DuckDB's default origin is a midnight, so buckets align with midnight.
            return $"time_bucket(to_microseconds({fixedPeriod.Span.Ticks / 10}), {local})";
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
        string sql, MetricDefinition metric, DateInterval timeframe, IReadOnlyList<EntityRef> entities,
        TimeAxis axis, bool raw, CancellationToken ct, (DateTime Start, DateTime End)? wallRange = null)
    {
        var builder = new PointBlock.Builder(metric.Unit, axis);
        if (entities.Count == 0) return builder.Build();

        var time = options.Time;
        var (start, end) = time.StoredRange(timeframe);

        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.Add(new DuckDBParameter("metric", metric.Id.Value));
        cmd.Parameters.Add(new DuckDBParameter("start", start));
        cmd.Parameters.Add(new DuckDBParameter("end", end));
        if (sql.Contains("$utcStart", StringComparison.Ordinal))
        {
            cmd.Parameters.Add(new DuckDBParameter("utcStart", timeframe.Start.UtcDateTime));
            cmd.Parameters.Add(new DuckDBParameter("utcEnd", timeframe.End.UtcDateTime));
        }
        if (wallRange is { } wr)
        {
            cmd.Parameters.Add(new DuckDBParameter("wallStart", wr.Start));
            cmd.Parameters.Add(new DuckDBParameter("wallEnd", wr.End));
        }

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

    private async Task<DuckDBConnection> ConnectAsync(CancellationToken ct)
    {
        DuckDBConnection conn;
        if (_inMemory)
        {
            lock (_gate)
            {
                conn = _root.Value.Duplicate(); // a new connection to the same database, not yet open
            }
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        conn = new DuckDBConnection(options.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public void Dispose()
    {
        if (_root.IsValueCreated) _root.Value.Dispose();
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
