using System.Data.Common;
using System.Globalization;
using Meridian.Core;
using Meridian.Sources.Sql;
using Npgsql;
using NpgsqlTypes;

namespace Meridian.Sources.PostgreSql;

/// <summary>
/// An <see cref="Engine.IRollupPointSource"/> and <see cref="Engine.IBatchPointSource"/> over PostgreSQL (TimescaleDB
/// included): the shared <see cref="SqlPointSource"/> engine with <see cref="PostgreSqlDialect"/>. See
/// <see cref="SqlSourceOptions"/> for the table layout and how the timestamp column is stored.
/// <para>
/// Create one per database and keep it (a singleton): it owns a connection pool. In an app that already has an
/// <see cref="NpgsqlDataSource"/> (registered with <c>AddNpgsqlDataSource</c>, say), pass that instead — the source
/// uses it without disposing it. Either way, sessions run in UTC (see <see cref="PostgreSqlDialect"/>).
/// </para>
/// </summary>
public sealed class PostgreSqlPointSource : SqlPointSource
{
    /// <summary>A source with its own connection pool for <paramref name="connectionString"/>, disposed with it.</summary>
    public PostgreSqlPointSource(string connectionString, SqlSourceOptions options, DimensionId entityDimension)
        : base(options, entityDimension, new PostgreSqlDialect(connectionString))
    {
    }

    /// <summary>A source over an existing data source, which it doesn't dispose. Its connections are opened in UTC.</summary>
    public PostgreSqlPointSource(NpgsqlDataSource dataSource, SqlSourceOptions options, DimensionId entityDimension)
        : base(options, entityDimension, new PostgreSqlDialect(dataSource))
    {
    }
}

/// <summary>
/// PostgreSQL's SQL: <c>@name</c> parameters, <c>date_trunc</c> and <c>date_bin</c> (PostgreSQL 14+ for sub-daily
/// buckets), ordered-set <c>percentile_cont</c> for medians and percentiles, and an ordered <c>array_agg</c> for first
/// and last.
/// <para>
/// Sessions run in UTC (the connection's <c>Timezone</c> is set to <c>UTC</c>), so a <c>timestamptz</c> column behaves
/// exactly like a <c>timestamp</c> column holding UTC in every comparison, truncation and cast here — and nothing
/// depends on the server's or the role's configured time zone. Zone handling is Meridian's own (docs/TIME.md).
/// </para>
/// </summary>
public sealed class PostgreSqlDialect : SqlDialect
{
    private static readonly Dictionary<IAggregator, string> Aggregates = new()
    {
        [Aggregators.Mean] = "avg({v})",
        [Aggregators.Sum] = "sum({v})",
        [Aggregators.Min] = "min({v})",
        [Aggregators.Max] = "max({v})",
        [Aggregators.Count] = "count({v})",
        [Aggregators.Median] = "percentile_cont(0.5) WITHIN GROUP (ORDER BY {v})",
        [Aggregators.Last] = "(array_agg({v} ORDER BY {t} DESC) FILTER (WHERE {v} IS NOT NULL))[1]",
        [Aggregators.First] = "(array_agg({v} ORDER BY {t}) FILTER (WHERE {v} IS NOT NULL))[1]",
        [Aggregators.StdDev] = "stddev_samp({v})",
        [Aggregators.Variance] = "var_samp({v})",
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly bool _owned;
    private readonly bool _utc;

    /// <summary>A dialect with its own pool, whose sessions start in UTC.</summary>
    public PostgreSqlDialect(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.ConnectionStringBuilder.Timezone = "UTC";
        _dataSource = builder.Build();
        _owned = true;
        _utc = true;
    }

    /// <summary>A dialect over an existing data source, which it doesn't dispose. Unless the data source's sessions
    /// already start in UTC, each connection is switched to UTC when opened (and reset when returned to the pool).</summary>
    public PostgreSqlDialect(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _utc = string.Equals(new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).Timezone, "UTC", StringComparison.OrdinalIgnoreCase);
    }

    public override async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await _dataSource.OpenConnectionAsync(ct).ConfigureAwait(false);
        if (!_utc)
        {
            await using var set = new NpgsqlCommand("SET TIME ZONE 'UTC'", connection);
            await set.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return connection;
    }

    public override string Parameter(string name) => "@" + name;

    public override void AddParameter(DbCommand command, string name, object value)
    {
        // Times are sent as timestamp (no zone): compared with a timestamptz column they convert in the UTC session.
        command.Parameters.Add(value is DateTime time
            ? new NpgsqlParameter(name, NpgsqlDbType.Timestamp) { Value = DateTime.SpecifyKind(time, DateTimeKind.Unspecified) }
            : new NpgsqlParameter(name, value));
    }

    public override string ToDouble(string expression) => $"CAST({expression} AS double precision)";

    public override string ToTimestamp(string expression) => $"CAST({expression} AS timestamp)";

    public override string TimestampLiteral(DateTime value) =>
        $"TIMESTAMP '{value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'";

    public override string AddMicroseconds(string expression, long microseconds) =>
        $"({expression} + INTERVAL '{microseconds.ToString(CultureInfo.InvariantCulture)} microseconds')";

    public override string TruncateToDay(string expression) => $"date_trunc('day', {Timestamp(expression)})";

    public override string TruncateToWeek(string expression) => $"date_trunc('week', {Timestamp(expression)})"; // ISO weeks: Monday

    public override string TruncateToMonth(string expression) => $"date_trunc('month', {Timestamp(expression)})";

    // Spans divide a day, so a midnight origin aligns every bucket with midnight.
    public override string TruncateToSpan(string expression, TimeSpan span) =>
        $"date_bin(INTERVAL '{(span.Ticks / 10).ToString(CultureInfo.InvariantCulture)} microseconds', {Timestamp(expression)}, TIMESTAMP '2000-01-01')";

    public override string? Aggregate(IAggregator aggregator) =>
        Aggregates.TryGetValue(aggregator, out var sql) ? sql
            : Aggregators.IsPercentile(aggregator, out var percent)
                ? $"percentile_cont({(percent / 100).ToString("R", CultureInfo.InvariantCulture)}) WITHIN GROUP (ORDER BY {{v}})"
                : null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && _owned) _dataSource.Dispose();
    }

    /// <summary>A timestamptz as the UTC timestamp it is in this session; a timestamp stays as it is.</summary>
    private static string Timestamp(string expression) => $"CAST({expression} AS timestamp)";
}
