using System.Data;
using System.Data.Common;
using System.Globalization;
using Meridian.Core;
using Meridian.Sources.Sql;
using Microsoft.Data.SqlClient;

namespace Meridian.Sources.SqlServer;

/// <summary>
/// An <see cref="Engine.IRollupPointSource"/> and <see cref="Engine.IBatchPointSource"/> over SQL Server or Azure SQL:
/// the shared <see cref="SqlPointSource"/> engine with <see cref="SqlServerDialect"/>. See <see cref="SqlSourceOptions"/>
/// for the table layout and how the timestamp column is stored. Connections are pooled by the driver.
/// </summary>
public sealed class SqlServerPointSource(string connectionString, SqlSourceOptions options, DimensionId entityDimension)
    : SqlPointSource(options, entityDimension, new SqlServerDialect(connectionString));

/// <summary>
/// SQL Server's SQL (2016 and later): <c>@name</c> parameters, <c>DATEADD</c> / <c>DATEDIFF</c> arithmetic for calendar
/// buckets, and no GROUP BY ordinals.
/// <para>
/// Mean, sum, min, max, count, standard deviation and variance push down. SQL Server has no aggregate form of a median,
/// a percentile, or the first or last value in time (<c>PERCENTILE_CONT</c> and <c>FIRST_VALUE</c> are window functions
/// only), so reports using those are computed in the engine from raw rows — the same answer, fetched differently.
/// </para>
/// <para>
/// <c>datetimeoffset</c> columns are read as UTC (each value's own offset applied), <c>datetime2</c>, <c>datetime</c> and
/// <c>date</c> as stored — so the column's <see cref="Time.StoredTime"/> works the same as for any other database.
/// Metric names are sent as <c>varchar</c>, which SQL Server can compare with a <c>varchar</c> or <c>nvarchar</c> metric
/// column and still seek an index; keep metric ids to characters a <c>varchar</c> column holds.
/// </para>
/// </summary>
public sealed class SqlServerDialect(string connectionString) : SqlDialect
{
    private static readonly Dictionary<IAggregator, string> Aggregates = new()
    {
        // Averaging and summing in float: AVG of an int column would otherwise truncate, and SUM could overflow it.
        [Aggregators.Mean] = "AVG(CAST({v} AS float))",
        [Aggregators.Sum] = "SUM(CAST({v} AS float))",
        [Aggregators.Min] = "MIN({v})",
        [Aggregators.Max] = "MAX({v})",
        [Aggregators.Count] = "COUNT({v})",
        [Aggregators.StdDev] = "STDEV({v})",   // sample, as the engine's
        [Aggregators.Variance] = "VAR({v})",
    };

    public override async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    public override string Parameter(string name) => "@" + name;

    public override void AddParameter(DbCommand command, string name, object value)
    {
        command.Parameters.Add(value switch
        {
            // datetime2 outranks date and datetime, so those columns are compared as timestamps (a date is its midnight);
            // a datetimeoffset column compares it as UTC.
            DateTime time => new SqlParameter(name, SqlDbType.DateTime2) { Value = DateTime.SpecifyKind(time, DateTimeKind.Unspecified) },
            string text => new SqlParameter(name, SqlDbType.VarChar, Math.Max(text.Length, 1)) { Value = text },
            _ => new SqlParameter(name, value),
        });
    }

    public override string TimestampValue(string column) => $"CAST(SWITCHOFFSET({column}, '+00:00') AS datetime2)";

    public override string ToDouble(string expression) => $"CAST({expression} AS float)";

    public override string ToTimestamp(string expression) => $"CAST({expression} AS datetime2)";

    public override string TimestampLiteral(DateTime value) =>
        $"CAST('{value.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture)}' AS datetime2)";

    public override string AddMicroseconds(string expression, long microseconds)
    {
        // DATEADD takes an int: whole seconds cover every zone offset and day shift; the rest, if any, in microseconds.
        long seconds = microseconds / 1_000_000, rest = microseconds % 1_000_000;
        string shifted = seconds == 0 ? expression : $"DATEADD(second, {seconds.ToString(CultureInfo.InvariantCulture)}, {expression})";
        return rest == 0 ? shifted : $"DATEADD(microsecond, {rest.ToString(CultureInfo.InvariantCulture)}, {shifted})";
    }

    public override string TruncateToDay(string expression) => $"CAST(CAST({expression} AS date) AS datetime2)";

    // 1 January 1900 was a Monday: step back by the days since the last Monday.
    public override string TruncateToWeek(string expression) =>
        $"DATEADD(day, -((DATEDIFF(day, CAST('19000101' AS date), CAST({expression} AS date)) % 7 + 7) % 7), {TruncateToDay(expression)})";

    public override string TruncateToMonth(string expression) =>
        $"CAST(DATEFROMPARTS(YEAR({expression}), MONTH({expression}), 1) AS datetime2)";

    // Whole-second spans, counted from midnight (spans divide a day, so buckets align with it). Sub-second spans decline.
    public override string? TruncateToSpan(string expression, TimeSpan span)
    {
        if (span.Ticks % TimeSpan.TicksPerSecond != 0) return null;
        string seconds = (span.Ticks / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture);
        string midnight = TruncateToDay(expression);
        return $"DATEADD(second, (DATEDIFF(second, {midnight}, {expression}) / {seconds}) * {seconds}, {midnight})";
    }

    public override string? Aggregate(IAggregator aggregator) => Aggregates.GetValueOrDefault(aggregator);

    public override bool GroupsByOrdinal => false;
}
