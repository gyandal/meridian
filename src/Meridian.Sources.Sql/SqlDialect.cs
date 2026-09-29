using System.Data.Common;
using Meridian.Core;

namespace Meridian.Sources.Sql;

/// <summary>
/// What differs between databases, and nothing else: connections, parameters, casts, timestamp arithmetic,
/// calendar truncation and aggregate SQL. <see cref="SqlPointSource"/> writes every query from these pieces, so
/// a new database is a dialect, and everything that makes pushdown exact — zone conversion generated from
/// NodaTime's rules, DST resolution, the exact timeframe filter — is shared and tested once.
/// <para>
/// A dialect returns null for what its database can't compute exactly (a truncation, an aggregate), and the
/// source then declines to push that rollup down: the engine fetches rows and computes it instead. Wrong but
/// fast is never an option.
/// </para>
/// </summary>
public abstract class SqlDialect : IDisposable
{
    /// <summary>An open connection for one query. The source disposes it.</summary>
    public abstract ValueTask<DbConnection> OpenAsync(CancellationToken ct);

    /// <summary>A parameter reference in SQL text, e.g. <c>$start</c> or <c>@start</c>.</summary>
    public abstract string Parameter(string name);

    /// <summary>Adds a parameter value to <paramref name="command"/> for <see cref="Parameter"/>(<paramref name="name"/>).</summary>
    public virtual void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>The expression as a double-precision number.</summary>
    public virtual string ToDouble(string expression) => $"CAST({expression} AS DOUBLE PRECISION)";

    /// <summary>The expression as a timestamp without a zone (how bucket starts are read back).</summary>
    public virtual string ToTimestamp(string expression) => $"CAST({expression} AS TIMESTAMP)";

    /// <summary>A timestamp literal, to microsecond precision, for a wall-clock or UTC time.</summary>
    public abstract string TimestampLiteral(DateTime value);

    /// <summary>The timestamp expression moved by <paramref name="microseconds"/> (which may be negative).</summary>
    public abstract string AddMicroseconds(string expression, long microseconds);

    /// <summary>The start of the day containing the timestamp expression.</summary>
    public abstract string? TruncateToDay(string expression);

    /// <summary>The start of the Monday-based week containing the timestamp expression.</summary>
    public abstract string? TruncateToWeek(string expression);

    /// <summary>The start of the month containing the timestamp expression.</summary>
    public abstract string? TruncateToMonth(string expression);

    /// <summary>The start of the <paramref name="span"/>-long bucket, aligned to midnight, containing the expression;
    /// null if the database can't. Spans divide a day.</summary>
    public virtual string? TruncateToSpan(string expression, TimeSpan span) => null;

    /// <summary>
    /// SQL for a built-in aggregator over value <c>{v}</c> (and <c>{t}</c>, the time that orders values for first
    /// and last), or null if the database can't compute it exactly. Custom aggregators are never guessed from
    /// their name: match built-ins by identity (see <see cref="Aggregators.IsPercentile"/> for percentiles).
    /// </summary>
    public abstract string? Aggregate(IAggregator aggregator);

    /// <summary>Whether GROUP BY and ORDER BY may use select-list ordinals (<c>GROUP BY 1, 2</c>); otherwise
    /// the source repeats the expressions.</summary>
    public virtual bool GroupsByOrdinal => true;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
