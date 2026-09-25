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
///
/// Two layouts are supported. <b>Long</b> (the default): one row per value, with <see cref="MetricColumn"/>
/// naming the metric and <see cref="ValueColumn"/> holding it. <b>Wide</b>: one row per (entity, time) with
/// a column per metric — set <see cref="MetricColumns"/> to map each metric id to its column (a SQL
/// expression from trusted configuration); <see cref="MetricColumn"/> and <see cref="ValueColumn"/> are then
/// unused. A NULL in a metric's column is a missing value for that metric, exactly as a NULL-valued row is
/// in the long layout.
///
/// <see cref="DimensionColumns"/> maps dimension names (venue, competition…) to columns so reports can
/// group by them. Attributes that live elsewhere — the venue on the match, a player's team on the date —
/// belong in <see cref="Relation"/>: make it a view that joins them onto each row, date-correctly.
/// </summary>
public sealed record DuckDbSourceOptions(
    string ConnectionString,
    string Relation = "datapoints",
    string EntityColumn = "entity_id",
    string MetricColumn = "metric",
    string ValueColumn = "value",
    string TimestampColumn = "ts",
    StoredTime? StoredTime = null,
    IReadOnlyDictionary<string, string>? MetricColumns = null,
    IReadOnlyDictionary<string, string>? DimensionColumns = null)
{
    public StoredTime Time => StoredTime ?? Meridian.Time.StoredTime.Utc;
}

/// <summary>
/// An <see cref="IRollupPointSource"/> and <see cref="IBatchPointSource"/> over DuckDB: any number of metrics
/// come back from one query. Raw fetches stream rows for the requested entities;
/// rollups push the calendar bucketing into SQL so only one row per (entity, bucket) leaves the database.
///
/// Pushdown covers fixed sub-daily, day, week (any start day) and month buckets with the built-in aggregators, in any
/// report zone, for UTC data, wall-clock data in a zone (<see cref="StoredTime.InZone"/>, unless its DST
/// policy rejects times) and local data as given. Zone conversions are generated from NodaTime's rules
/// (<see cref="ZoneSql"/>), so a pushed-down result always equals the in-engine one; seasons fall back
/// to a raw fetch.
/// </summary>
public sealed class DuckDbPointSource(DuckDbSourceOptions options, DimensionId entityDimension) : IRollupPointSource, IBatchPointSource, IDisposable
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

    public async Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default) =>
        (await FetchManyAsync([metric], entities, timeframe, rollup: null, ct).ConfigureAwait(false))[metric.Id];

    public bool CanRollup(MetricDefinition metric, SourceRollup rollup)
    {
        if (options.MetricColumns is { } columns && !columns.ContainsKey(metric.Id.Value)) return false;
        foreach (var dimension in rollup.GroupBy)
        {
            // Grouping must be exact: a dimension this source can't read would silently merge its values.
            if (options.DimensionColumns?.ContainsKey(dimension.Name) != true || !metric.ValidDimensions.Contains(dimension)) return false;
        }
        var time = options.Time;
        bool wallClockConvertible = time.Zone is null
            || (time.Resolution!.Ambiguous != AmbiguousTime.Reject && time.Resolution.Skipped != SkippedTime.Reject);
        return wallClockConvertible && Truncate(rollup, "x") is not null && SqlAggregates.ContainsKey(rollup.Aggregator);
    }

    public async Task<PointBlock> FetchRollupAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup rollup,
        CancellationToken ct = default) =>
        (await FetchManyAsync([metric], entities, timeframe, rollup, ct).ConfigureAwait(false))[metric.Id];

    /// <summary>
    /// Every fetch runs through here: one query for all requested metrics (<c>metric IN (…)</c>), split by
    /// metric on the way out. Raw rows when <paramref name="rollup"/> is null, pushed-down buckets otherwise.
    /// </summary>
    public Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchManyAsync(
        IReadOnlyList<MetricDefinition> metrics,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        SourceRollup? rollup,
        CancellationToken ct = default)
    {
        if (rollup is not null)
        {
            foreach (var metric in metrics)
            {
                if (!CanRollup(metric, rollup))
                {
                    throw new NotSupportedException($"Cannot push down a '{rollup.Period.Name}' / '{rollup.Aggregator.Name}' rollup of '{metric.Id}' ({options.Time} data).");
                }
            }
        }
        var distinct = metrics.DistinctBy(m => m.Id).ToList();
        var shape = rollup is null ? null : Shape(rollup, timeframe);
        // Raw rows carry every dimension the metrics declare that this source can read; a rollup groups by
        // exactly the ones asked for.
        var dimensions = rollup?.GroupBy.ToList() ?? KnownDimensions(distinct);
        return options.MetricColumns is null
            ? FetchLongAsync(distinct, entities, timeframe, shape, dimensions, ct)
            : FetchWideAsync(distinct, entities, timeframe, shape, dimensions, ct);
    }

    /// <summary>How a rollup is computed in SQL: the bucket expression, what orders values for <c>last</c>,
    /// any exact timeframe filter, and the axis of the resulting buckets.</summary>
    private sealed record RollupShape(IAggregator Aggregator, string Bucket, string OrderBy, string Exact, (DateTime Start, DateTime End)? WallRange, TimeAxis Axis);

    private RollupShape Shape(SourceRollup rollup, DateInterval timeframe)
    {
        var time = options.Time;
        var ts = options.TimestampColumn;

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

        // Buckets are local (docs/TIME.md): tagged with the grain, and with the zone that drew the boundaries.
        var zone = time.Kind == TimeKind.Instant ? rollup.Calendar.Zone.Id : null;
        return new RollupShape(rollup.Aggregator, Truncate(rollup, local)!, utc ?? ts, exact, wallRange, TimeAxis.Local(rollup.Period.Name, zone));
    }

    /// <summary>A bucket's aggregate of one value column; all-NULL buckets come back as missing (not 0 or
    /// dropped), to match the engine.</summary>
    private static string Aggregate(RollupShape shape, string value) =>
        $"CASE WHEN count({value}) = 0 THEN NULL ELSE CAST({SqlAggregates[shape.Aggregator].Replace("{v}", value).Replace("{t}", shape.OrderBy)} AS DOUBLE) END";

    private Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchLongAsync(
        List<MetricDefinition> metrics, IReadOnlyList<EntityRef> entities, DateInterval timeframe, RollupShape? shape,
        List<DimensionId> dimensions, CancellationToken ct)
    {
        var o = options;
        var (select, groupBy) = DimensionSql(dimensions, firstOrdinal: 5);
        var sql = shape is null
            // Values may be DECIMAL or INTEGER in real schemas (fares, counts): DuckDB casts, we read doubles.
            ? $"SELECT {o.MetricColumn}, {o.EntityColumn}, CAST({o.ValueColumn} AS DOUBLE), {o.TimestampColumn}{select} FROM {o.Relation} " +
              Where(entities, metrics.Count) + " ORDER BY 1, 2, 4"
            : $"SELECT {o.MetricColumn}, {o.EntityColumn}, {Aggregate(shape, o.ValueColumn)}, CAST({shape.Bucket} AS TIMESTAMP) AS bucket{select} " +
              $"FROM {o.Relation} " + Where(entities, metrics.Count) + shape.Exact + $" GROUP BY 1, 2, 4{groupBy} ORDER BY 1, 2, 4";
        var keys = new KeyReader(entityDimension, dimensions, entityOrdinal: 1, firstDimensionOrdinal: 4);
        return QueryAsync(sql, metrics, timeframe, entities, shape?.Axis ?? o.Time.Axis, raw: shape is null, keys, ct, shape?.WallRange);
    }

    /// <summary>
    /// The wide layout: one scan reads every requested metric's column (a rollup aggregates them all in one
    /// GROUP BY), so a multi-metric request touches each row once instead of once per metric.
    /// </summary>
    private async Task<IReadOnlyDictionary<MetricId, PointBlock>> FetchWideAsync(
        List<MetricDefinition> metrics, IReadOnlyList<EntityRef> entities, DateInterval timeframe, RollupShape? shape,
        List<DimensionId> dimensions, CancellationToken ct)
    {
        var o = options;
        var columns = metrics.Select(WideColumn).ToList();
        var (select, groupBy) = DimensionSql(dimensions, firstOrdinal: 3 + columns.Count);
        var sql = shape is null
            ? $"SELECT {o.EntityColumn}, {o.TimestampColumn}, {string.Join(", ", columns.Select(c => $"CAST({c} AS DOUBLE)"))}{select} " +
              $"FROM {o.Relation} " + Where(entities, metricCount: 0) + " ORDER BY 1, 2"
            : $"SELECT {o.EntityColumn}, CAST({shape.Bucket} AS TIMESTAMP) AS bucket, {string.Join(", ", columns.Select(c => Aggregate(shape, c)))}{select} " +
              $"FROM {o.Relation} " + Where(entities, metricCount: 0) + shape.Exact + $" GROUP BY 1, 2{groupBy} ORDER BY 1, 2";
        var keys = new KeyReader(entityDimension, dimensions, entityOrdinal: 0, firstDimensionOrdinal: 2 + columns.Count);

        var axis = shape?.Axis ?? o.Time.Axis;
        var builders = metrics.Select(m => new PointBlock.Builder(m.Unit, axis)).ToArray();
        if (entities.Count > 0 && metrics.Count > 0)
        {
            bool raw = shape is null;
            var time = o.Time;
            await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
            await using var cmd = Command(conn, sql, [], timeframe, shape?.WallRange);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                long ticks = raw ? ReadTicks(reader.GetValue(1), time) : ToDateTime(reader.GetValue(1)).Ticks;
                if (raw && time.NeedsExactFilter && (ticks < timeframe.Start.UtcTicks || ticks >= timeframe.End.UtcTicks)) continue;

                var key = keys.Read(reader);
                var at = new Instant(ticks);
                for (int m = 0; m < builders.Length; m++)
                {
                    var measure = reader.IsDBNull(m + 2) ? Measurement.Missing : Measurement.Of(reader.GetDouble(m + 2));
                    builders[m].Add(key, measure, at);
                }
            }
        }
        return metrics.Select((m, i) => (m.Id, Block: builders[i].Build())).ToDictionary(x => x.Id, x => x.Block);
    }

    /// <summary>Dimensions the metrics declare that this source has a column for.</summary>
    private List<DimensionId> KnownDimensions(IEnumerable<MetricDefinition> metrics) =>
        options.DimensionColumns is not { } columns ? []
            : [.. metrics.SelectMany(m => m.ValidDimensions).Distinct()
                .Where(d => d != entityDimension && columns.ContainsKey(d.Name)).Order()];

    /// <summary>The SELECT tail and GROUP BY ordinals for dimension columns starting at <paramref name="firstOrdinal"/> (1-based).</summary>
    private (string Select, string GroupBy) DimensionSql(List<DimensionId> dimensions, int firstOrdinal) =>
        dimensions.Count == 0 ? ("", "")
            : (string.Concat(dimensions.Select(d => ", " + options.DimensionColumns![d.Name])),
               string.Concat(dimensions.Select((_, i) => ", " + (firstOrdinal + i).ToString(CultureInfo.InvariantCulture))));

    /// <summary>Builds each row's key — the entity plus its dimension values — reusing the previous key
    /// while nothing changes. A NULL dimension value is the empty category.</summary>
    private sealed class KeyReader(DimensionId entity, List<DimensionId> dimensions, int entityOrdinal, int firstDimensionOrdinal)
    {
        private readonly string?[] _values = new string?[dimensions.Count];
        private long _id = long.MinValue;
        private PointKey _key;
        private bool _started;

        public PointKey Read(System.Data.Common.DbDataReader reader)
        {
            long id = reader.GetInt64(entityOrdinal);
            bool same = _started && id == _id;
            for (int i = 0; i < _values.Length; i++)
            {
                int ordinal = firstDimensionOrdinal + i;
                var value = reader.IsDBNull(ordinal) ? "" : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
                if (value != _values[i])
                {
                    _values[i] = value;
                    same = false;
                }
            }
            if (!same)
            {
                var parts = new KeyPart[_values.Length + 1];
                parts[0] = KeyPart.Entity(entity, id);
                for (int i = 0; i < _values.Length; i++) parts[i + 1] = KeyPart.Category(dimensions[i], _values[i]!);
                _key = PointKey.Of(parts);
                _id = id;
                _started = true;
            }
            return _key;
        }
    }

    private string WideColumn(MetricDefinition metric) =>
        options.MetricColumns!.TryGetValue(metric.Id.Value, out var column)
            ? column
            : throw new InvalidOperationException(
                $"Metric '{metric.Id}' has no column in DuckDbSourceOptions.MetricColumns (wide layout). Map it, or use the long layout.");

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

    /// <summary>The shared filter: timeframe, entities, and (long layout, <paramref name="metricCount"/> &gt; 0) metrics.</summary>
    private string Where(IReadOnlyList<EntityRef> entities, int metricCount)
    {
        var o = options;
        var sb = new StringBuilder($"WHERE {o.TimestampColumn} >= $start AND {o.TimestampColumn} < $end");
        if (metricCount > 0)
        {
            sb.Append($" AND {o.MetricColumn} ");
            sb.Append(metricCount == 1 ? "= $m0" : $"IN ({string.Join(", ", Enumerable.Range(0, metricCount).Select(i => "$m" + i))})");
        }
        sb.Append($" AND {o.EntityColumn} IN (");
        for (int i = 0; i < entities.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(entities[i].Id.ToString(CultureInfo.InvariantCulture)); // numeric ids: safe to inline
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Runs a query whose columns are (metric, entity, value, time) and splits it into a block per metric.</summary>
    private async Task<IReadOnlyDictionary<MetricId, PointBlock>> QueryAsync(
        string sql, IReadOnlyList<MetricDefinition> metrics, DateInterval timeframe, IReadOnlyList<EntityRef> entities,
        TimeAxis axis, bool raw, KeyReader keys, CancellationToken ct, (DateTime Start, DateTime End)? wallRange = null)
    {
        var builders = metrics.DistinctBy(m => m.Id).ToDictionary(m => m.Id.Value, m => new PointBlock.Builder(m.Unit, axis));
        if (entities.Count > 0 && builders.Count > 0)
        {
            await ReadAsync(sql, metrics, timeframe, raw, wallRange, builders, keys, ct).ConfigureAwait(false);
        }
        return builders.ToDictionary(kv => new MetricId(kv.Key), kv => kv.Value.Build());
    }

    private async Task ReadAsync(
        string sql, IReadOnlyList<MetricDefinition> metrics, DateInterval timeframe, bool raw,
        (DateTime Start, DateTime End)? wallRange, Dictionary<string, PointBlock.Builder> builders, KeyReader keys, CancellationToken ct)
    {
        var time = options.Time;

        await using var conn = await ConnectAsync(ct).ConfigureAwait(false);
        await using var cmd = Command(conn, sql, metrics, timeframe, wallRange);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        string? currentMetric = null;
        PointBlock.Builder builder = null!;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // Raw rows are normalised by the column's StoredTime; rollup buckets are already local wall-clock.
            long ticks = raw ? ReadTicks(reader.GetValue(3), time) : ToDateTime(reader.GetValue(3)).Ticks;
            if (raw && time.NeedsExactFilter && (ticks < timeframe.Start.UtcTicks || ticks >= timeframe.End.UtcTicks)) continue;

            var metric = Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!;
            if (metric != currentMetric)
            {
                currentMetric = metric;
                builder = builders[metric];
            }
            var key = keys.Read(reader);
            var measure = reader.IsDBNull(2) ? Measurement.Missing : Measurement.Of(reader.GetDouble(2));
            builder.Add(key, measure, new Instant(ticks));
        }
    }

    private DuckDBCommand Command(
        DuckDBConnection conn, string sql, IReadOnlyList<MetricDefinition> metrics, DateInterval timeframe, (DateTime Start, DateTime End)? wallRange)
    {
        var (start, end) = options.Time.StoredRange(timeframe);
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        for (int i = 0; i < metrics.Count; i++) cmd.Parameters.Add(new DuckDBParameter("m" + i, metrics[i].Id.Value));
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
        return cmd;
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
