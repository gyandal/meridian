using System.Data.Common;
using System.Globalization;
using DuckDB.NET.Data;
using Meridian.Core;
using Meridian.Sources.Sql;
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

    internal SqlSourceOptions ToSql() =>
        new(Relation, EntityColumn, MetricColumn, ValueColumn, TimestampColumn, StoredTime, MetricColumns, DimensionColumns);
}

/// <summary>
/// An <see cref="Engine.IRollupPointSource"/> and <see cref="Engine.IBatchPointSource"/> over DuckDB: any number of
/// metrics come back from one query. Raw fetches stream rows for the requested entities; rollups push the calendar
/// bucketing into SQL so only one row per (entity, bucket) leaves the database.
///
/// Pushdown covers fixed sub-daily, day, week (any start day) and month buckets with every built-in aggregator, in
/// any report zone, for UTC data, wall-clock data in a zone (<see cref="StoredTime.InZone"/>, unless its DST
/// policy rejects times) and local data as given. Zone conversions are generated from NodaTime's rules, so a
/// pushed-down result always equals the in-engine one; seasons fall back to a raw fetch. The query engine is
/// <see cref="SqlPointSource"/>; this adds DuckDB's dialect.
/// </summary>
public sealed class DuckDbPointSource(DuckDbSourceOptions options, DimensionId entityDimension)
    : SqlPointSource(options.ToSql(), entityDimension, new DuckDbDialect(options.ConnectionString));

/// <summary>DuckDB's SQL: <c>$name</c> parameters, <c>date_trunc</c> and <c>time_bucket</c>, <c>arg_max</c> for
/// last, <c>quantile_cont</c> for percentiles.</summary>
public sealed class DuckDbDialect : SqlDialect
{
    private static readonly Dictionary<IAggregator, string> Aggregates = new()
    {
        [Aggregators.Mean] = "avg({v})",
        [Aggregators.Sum] = "sum({v})",
        [Aggregators.Min] = "min({v})",
        [Aggregators.Max] = "max({v})",
        [Aggregators.Count] = "count({v})",
        [Aggregators.Median] = "median({v})",
        [Aggregators.Last] = "arg_max({v}, {t}) FILTER (WHERE {v} IS NOT NULL)",
        [Aggregators.First] = "arg_min({v}, {t}) FILTER (WHERE {v} IS NOT NULL)",
        [Aggregators.StdDev] = "stddev_samp({v})",
        [Aggregators.Variance] = "var_samp({v})",
    };

    private readonly string _connectionString;

    // An in-memory database (e.g. one that queries Parquet files) is created once for the source's lifetime
    // and each query gets a duplicated connection to it: creating a DuckDB database costs far more than a
    // query over it, and ':memory:' would otherwise be a new, empty database every time. File databases are
    // opened per query; DuckDB.NET shares the underlying instance within a process.
    private readonly bool _inMemory;
    private readonly Lazy<DuckDBConnection> _root;
    private readonly Lock _gate = new();

    public DuckDbDialect(string connectionString)
    {
        _connectionString = connectionString;
        _inMemory = new DuckDBConnectionStringBuilder { ConnectionString = connectionString }.DataSource.StartsWith(":memory:", StringComparison.Ordinal);
        _root = new(() =>
        {
            var root = new DuckDBConnection(connectionString);
            root.Open();
            return root;
        });
    }

    public override async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        DuckDBConnection conn;
        if (_inMemory)
        {
            lock (_gate)
            {
                conn = _root.Value.Duplicate(); // a new connection to the same database, not yet open
            }
        }
        else
        {
            conn = new DuckDBConnection(_connectionString);
        }
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }

    public override string Parameter(string name) => "$" + name;

    public override void AddParameter(DbCommand command, string name, object value) =>
        command.Parameters.Add(new DuckDBParameter(name, value));

    public override string ToDouble(string expression) => $"CAST({expression} AS DOUBLE)";

    public override string TimestampLiteral(DateTime value) =>
        $"TIMESTAMP '{value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'"; // DuckDB: µs precision

    public override string AddMicroseconds(string expression, long microseconds) =>
        $"({expression} + to_microseconds({microseconds.ToString(CultureInfo.InvariantCulture)}))";

    public override string TruncateToDay(string expression) => $"date_trunc('day', {expression})";

    public override string TruncateToWeek(string expression) => $"date_trunc('week', {expression})";

    public override string TruncateToMonth(string expression) => $"date_trunc('month', {expression})";

    // Spans divide a day, and DuckDB's default origin is a midnight, so buckets align with midnight.
    public override string TruncateToSpan(string expression, TimeSpan span) =>
        $"time_bucket(to_microseconds({(span.Ticks / 10).ToString(CultureInfo.InvariantCulture)}), {expression})";

    public override string? Aggregate(IAggregator aggregator) =>
        Aggregates.TryGetValue(aggregator, out var sql) ? sql
            : Aggregators.IsPercentile(aggregator, out var percent)
                ? "quantile_cont({v}, " + (percent / 100).ToString("R", CultureInfo.InvariantCulture) + ")"
                : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && _root.IsValueCreated) _root.Value.Dispose();
    }
}
