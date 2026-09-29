using System.Data.Common;
using System.Globalization;
using ClickHouse.Driver.ADO;
using Meridian.Core;
using Meridian.Sources.Sql;

namespace Meridian.Sources.ClickHouse;

/// <summary>
/// An <see cref="Engine.IRollupPointSource"/> and <see cref="Engine.IBatchPointSource"/> over ClickHouse: the shared
/// <see cref="SqlPointSource"/> engine with <see cref="ClickHouseDialect"/>. See <see cref="SqlSourceOptions"/> for the
/// table layout and how the timestamp column is stored. Connections use ClickHouse's HTTP interface.
/// </summary>
public sealed class ClickHousePointSource(string connectionString, SqlSourceOptions options, DimensionId entityDimension)
    : SqlPointSource(options, entityDimension, new ClickHouseDialect(connectionString));

/// <summary>
/// ClickHouse's SQL: typed <c>{name:Type}</c> parameters, <c>toStartOfDay</c> / <c>toMonday</c> / <c>toStartOfMonth</c>
/// buckets (sub-daily ones counted from midnight), and every built-in aggregator — medians and percentiles with
/// <c>quantileExactInclusive</c> (exact, interpolating as the engine does; ClickHouse's own <c>median</c> is
/// approximate), first and last with <c>argMinIf</c> / <c>argMaxIf</c>, spread with the numerically stable
/// <c>stddevSampStable</c> / <c>varSampStable</c>.
/// <para>
/// ClickHouse timestamps carry a time zone, and its calendar functions work in it. Every value is read as a UTC
/// <c>DateTime64</c> first, and all arithmetic is in UTC, so a column's zone never moves a bucket. Store wall-clock
/// readings (<see cref="Time.StoredTime.InZone"/>) and dates as given (<see cref="Time.StoredTime.Local"/>) in a
/// <c>DateTime64(…, 'UTC')</c> or <c>Date</c> column, so their wall clock is what's stored.
/// </para>
/// </summary>
public sealed class ClickHouseDialect(string connectionString) : SqlDialect
{
    private static readonly Dictionary<IAggregator, string> Aggregates = new()
    {
        [Aggregators.Mean] = "avg({v})",
        [Aggregators.Sum] = "sum({v})",
        [Aggregators.Min] = "min({v})",
        [Aggregators.Max] = "max({v})",
        [Aggregators.Count] = "count({v})",
        [Aggregators.Median] = "quantileExactInclusive(0.5)({v})",
        [Aggregators.Last] = "argMaxIf({v}, {t}, isNotNull({v}))",
        [Aggregators.First] = "argMinIf({v}, {t}, isNotNull({v}))",
        // ClickHouse gives +inf for the sample spread of one value; there is none (NULL, as elsewhere).
        [Aggregators.StdDev] = "if(count({v}) > 1, stddevSampStable({v}), NULL)",
        [Aggregators.Variance] = "if(count({v}) > 1, varSampStable({v}), NULL)",
    };

    private const string Utc = "'UTC'";

    public override async ValueTask<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new ClickHouseConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    // Parameters are typed in the SQL. Plain ones are metric names; times have their own type.
    public override string Parameter(string name) => $"{{{name}:String}}";

    public override string TimeParameter(string name) => $"{{{name}:DateTime64(6, {Utc})}}";

    public override void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is DateTime time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : value;
        command.Parameters.Add(parameter);
    }

    public override string TimestampValue(string column) => $"toDateTime64({column}, 6, {Utc})";

    public override string ToDouble(string expression) => $"CAST({expression} AS Nullable(Float64))";

    public override string ToTimestamp(string expression) => $"toDateTime64({expression}, 6, {Utc})";

    public override string TimestampLiteral(DateTime value) =>
        $"toDateTime64('{value.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture)}', 6, {Utc})";

    public override string AddMicroseconds(string expression, long microseconds) =>
        $"addMicroseconds({expression}, {microseconds.ToString(CultureInfo.InvariantCulture)})";

    public override string TruncateToDay(string expression) => $"toDateTime64(toStartOfDay({expression}, {Utc}), 6, {Utc})";

    public override string TruncateToWeek(string expression) => $"toDateTime64(toMonday({expression}, {Utc}), 6, {Utc})";

    public override string TruncateToMonth(string expression) => $"toDateTime64(toStartOfMonth({expression}, {Utc}), 6, {Utc})";

    // Counted from midnight (spans divide a day, so buckets align with it).
    public override string TruncateToSpan(string expression, TimeSpan span)
    {
        string micros = (span.Ticks / 10).ToString(CultureInfo.InvariantCulture);
        string midnight = TruncateToDay(expression);
        return $"addMicroseconds({midnight}, intDiv(dateDiff('microsecond', {midnight}, {expression}), {micros}) * {micros})";
    }

    public override string? Aggregate(IAggregator aggregator) =>
        Aggregates.TryGetValue(aggregator, out var sql) ? sql
            : Aggregators.IsPercentile(aggregator, out var percent)
                ? $"quantileExactInclusive({(percent / 100).ToString("R", CultureInfo.InvariantCulture)})({{v}})"
                : null;
}
