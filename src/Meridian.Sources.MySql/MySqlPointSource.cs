using Meridian.Caching;
using Meridian.Core;
using Meridian.Engine;
using Meridian.Semantics;
using Meridian.Time;
using MySqlConnector;

namespace Meridian.Sources.MySql;

/// <summary>Maps a metric fetch onto a MySQL table. Column names are configurable so you point it at
/// an existing datapoints table without renaming anything. <see cref="Time"/> says how the timestamp
/// column is stored — MySQL <c>DATETIME</c> columns often hold local wall-clock times (docs/TIME.md).</summary>
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
}

/// <summary>
/// A real <see cref="IPointSource"/> over MySQL — the ~20% per-client adapter. The engine calls this
/// only for cache misses, per entity, so a warm/interactive report never touches the database. This is
/// the whole integration surface: one query, and reporting/caching/dashboard/agent tools work unchanged.
/// </summary>
public sealed class MySqlPointSource(MySqlSourceOptions options, DimensionId entityDimension) : IPointSource
{
    public async Task<PointBlock> FetchAsync(
        MetricDefinition metric,
        IReadOnlyList<EntityRef> entities,
        DateInterval timeframe,
        CancellationToken ct = default)
    {
        var o = options;
        var time = o.Time;
        var builder = new PointBlock.Builder(metric.Unit, time.Axis);
        if (entities.Count == 0) return builder.Build();
        var (start, end) = time.StoredRange(timeframe);

        var inParams = string.Join(",", entities.Select((_, i) => "@e" + i));
        var sql =
            $"SELECT `{o.EntityColumn}`, `{o.ValueColumn}`, `{o.TimestampColumn}` " +
            $"FROM `{o.Table}` " +
            $"WHERE `{o.MetricColumn}` = @metric " +
            $"AND `{o.EntityColumn}` IN ({inParams}) " +
            $"AND `{o.TimestampColumn}` >= @start AND `{o.TimestampColumn}` < @end";

        await using var conn = new MySqlConnection(o.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@metric", metric.Id.Value);
        cmd.Parameters.AddWithValue("@start", start);
        cmd.Parameters.AddWithValue("@end", end);
        for (int i = 0; i < entities.Count; i++) cmd.Parameters.AddWithValue("@e" + i, entities[i].Id);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            var measure = reader.IsDBNull(1) ? Measurement.Missing : Measurement.Of(reader.GetDouble(1));
            long ticks = time.ToTicks(reader.GetDateTime(2));
            if (time.NeedsExactFilter && (ticks < timeframe.Start.UtcTicks || ticks >= timeframe.End.UtcTicks)) continue;
            builder.Add(PointKey.Of(KeyPart.Entity(entityDimension, id)), measure, new Instant(ticks));
        }
        return builder.Build();
    }
}
