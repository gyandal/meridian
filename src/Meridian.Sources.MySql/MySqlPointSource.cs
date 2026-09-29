using System.Data.Common;
using System.Globalization;
using Meridian.Core;
using Meridian.Sources.Sql;
using Meridian.Time;
using MySqlConnector;

namespace Meridian.Sources.MySql;

/// <summary>Maps a metric fetch onto a MySQL table of one row per value. Column names are configurable so you point it
/// at an existing datapoints table without renaming anything. <see cref="Time"/> says how the timestamp column is
/// stored — MySQL <c>DATETIME</c> columns often hold local wall-clock times (docs/TIME.md). For wide tables, dimensions
/// or a view, construct <see cref="MySqlPointSource"/> with <see cref="SqlSourceOptions"/> instead.</summary>
public sealed record MySqlSourceOptions(
    string ConnectionString,
    string Table = "datapoints",
    string EntityColumn = "entity_id",
    string MetricColumn = "metric",
    string ValueColumn = "value",
    string TimestampColumn = "recorded_at",
    StoredTime? StoredTime = null)
{
    public StoredTime Time => StoredTime ?? Meridian.Time.StoredTime.Utc;

    internal SqlSourceOptions ToSql() =>
        new($"`{Table}`", $"`{EntityColumn}`", $"`{MetricColumn}`", $"`{ValueColumn}`", $"`{TimestampColumn}`", StoredTime);
}

/// <summary>
/// An <see cref="Engine.IRollupPointSource"/> and <see cref="Engine.IBatchPointSource"/> over MySQL 8 (and compatible
/// servers): the shared <see cref="SqlPointSource"/> engine with <see cref="MySqlDialect"/> — long or wide tables and
/// views, dimensions, batching, and calendar bucketing pushed down. Connections are pooled by the driver.
/// </summary>
public sealed class MySqlPointSource : SqlPointSource
{
    /// <summary>A source over one table of one row per value, named by <paramref name="options"/>.</summary>
    public MySqlPointSource(MySqlSourceOptions options, DimensionId entityDimension)
        : base(options.ToSql(), entityDimension, new MySqlDialect(options.ConnectionString))
    {
    }

    /// <summary>A source over any relation and layout (see <see cref="SqlSourceOptions"/>). Names are SQL: quote
    /// reserved words with backticks.</summary>
    public MySqlPointSource(string connectionString, SqlSourceOptions options, DimensionId entityDimension)
        : base(options, entityDimension, new MySqlDialect(connectionString))
    {
    }
}

/// <summary>
/// MySQL's SQL (8.0.17 and later): <c>@name</c> parameters, <c>DATE</c> / <c>WEEKDAY</c> / <c>INTERVAL</c> arithmetic for
/// calendar buckets. Mean, sum, min, max, count, standard deviation and variance push down; MySQL has no aggregate
/// form of a median, a percentile, or the first or last value in time, so reports using those are computed in the
/// engine from raw rows — the same answer, fetched differently.
/// <para>
/// Each connection's session runs in UTC (<c>time_zone = '+00:00'</c>), so a <c>TIMESTAMP</c> column — which MySQL
/// converts through the session zone — reads as the UTC instant it stores, whatever the server's zone. <c>DATETIME</c>
/// and <c>DATE</c> columns are read as stored.
/// </para>
/// </summary>
public sealed class MySqlDialect(string connectionString) : SqlDialect
{
    private static readonly Dictionary<IAggregator, string> Aggregates = new()
    {
        [Aggregators.Mean] = "AVG({v})",
        [Aggregators.Sum] = "SUM({v})",
        [Aggregators.Min] = "MIN({v})",
        [Aggregators.Max] = "MAX({v})",
        [Aggregators.Count] = "COUNT({v})",
        [Aggregators.StdDev] = "STDDEV_SAMP({v})",
        [Aggregators.Variance] = "VAR_SAMP({v})",
    };

    public override async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        // Per open: the pool resets session variables when a connection is returned.
        await using var utc = new MySqlCommand("SET time_zone = '+00:00'", connection);
        await utc.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public override string Parameter(string name) => "@" + name;

    // A time parameter as DATETIME(6): a DATE column compared with it is its midnight.
    public override string TimeParameter(string name) => $"CAST(@{name} AS DATETIME(6))";

    public override void AddParameter(DbCommand command, string name, object value) =>
        command.Parameters.Add(value is DateTime time
            ? new MySqlParameter(name, MySqlDbType.DateTime) { Value = DateTime.SpecifyKind(time, DateTimeKind.Unspecified) }
            : new MySqlParameter(name, value));

    public override string ToDouble(string expression) => $"CAST({expression} AS DOUBLE)";

    public override string ToTimestamp(string expression) => $"CAST({expression} AS DATETIME(6))";

    public override string TimestampLiteral(DateTime value) =>
        $"TIMESTAMP '{value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}'";

    public override string AddMicroseconds(string expression, long microseconds) =>
        $"({expression} + INTERVAL {microseconds.ToString(CultureInfo.InvariantCulture)} MICROSECOND)";

    public override string TruncateToDay(string expression) => $"CAST(DATE({expression}) AS DATETIME(6))";

    // WEEKDAY is 0 for Monday.
    public override string TruncateToWeek(string expression) =>
        $"CAST(DATE({expression}) - INTERVAL WEEKDAY({expression}) DAY AS DATETIME(6))";

    public override string TruncateToMonth(string expression) =>
        $"CAST(DATE({expression}) - INTERVAL (DAYOFMONTH({expression}) - 1) DAY AS DATETIME(6))";

    // Counted from midnight in microseconds (spans divide a day, so buckets align with it).
    public override string TruncateToSpan(string expression, TimeSpan span)
    {
        string micros = (span.Ticks / 10).ToString(CultureInfo.InvariantCulture);
        string midnight = TruncateToDay(expression);
        return $"({midnight} + INTERVAL (TIMESTAMPDIFF(MICROSECOND, {midnight}, {expression}) DIV {micros}) * {micros} MICROSECOND)";
    }

    public override string? Aggregate(IAggregator aggregator) => Aggregates.GetValueOrDefault(aggregator);
}
