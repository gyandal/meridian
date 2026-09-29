using System.Data;
using ClickHouse.Driver.ADO;
using ClickHouse.Driver;
using DuckDB.NET.Data;
using Meridian.Core;
using Meridian.Sources.ClickHouse;
using Meridian.Sources.TestData;
using Testcontainers.ClickHouse;

namespace Meridian.Sources.Sql.Tests;

/// <summary>ClickHouse in a container, holding the same tables as <see cref="DuckDbFixture"/>.</summary>
public sealed class ClickHouseFixture : IDatabaseFixture, IAsyncLifetime
{
    private ClickHouseContainer? _container;

    public string? SkipReason { get; private set; }

    public DuckDbFixture Reference { get; } = new();

    public SqlPointSource Source(SqlSourceOptions options, DimensionId entity) =>
        new ClickHousePointSource(_container!.GetConnectionString(), options, entity);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new ClickHouseBuilder("clickhouse/clickhouse-server:25.8").Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SkipReason = $"ClickHouse needs Docker (with Linux containers), which isn't available here ({e.GetType().Name}: {e.Message.Split('\n')[0]}).";
            return;
        }

        await using var ch = new ClickHouseConnection(_container.GetConnectionString());
        await ch.OpenAsync();
        await CopyTablesAsync(ch);
        await ExecuteAsync(ch, "CREATE VIEW wide_as_long AS SELECT entity_id, ts, 'load' AS metric, load AS value FROM wide UNION ALL SELECT entity_id, ts, 'hr', hr FROM wide");
        // The same instants in a column whose zone is Asia/Kolkata (+05:30): its calendar functions would bucket there.
        await ExecuteAsync(ch, "CREATE TABLE datapoints_tz ENGINE = MergeTree ORDER BY tuple() AS SELECT entity_id, metric, toDateTime64(ts, 6, 'Asia/Kolkata') AS ts, value FROM datapoints");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        Reference.Dispose();
    }

    /// <summary>Every DuckDB base table, recreated with matching column types (all nullable) and bulk-copied.</summary>
    private async Task CopyTablesAsync(ClickHouseConnection ch)
    {
        using var duck = new DuckDBConnection(Reference.ConnectionString);
        duck.Open();
        foreach (var table in Query(duck, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_type = 'BASE TABLE' ORDER BY 1", r => r.GetString(0)))
        {
            var columns = Query(duck, $"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position",
                r => (Name: r.GetString(0), Type: ClickHouseType(r.GetString(1))));
            await ExecuteAsync(ch, $"CREATE TABLE {table} ({string.Join(", ", columns.Select(c => $"{c.Name} Nullable({c.Type})"))}) ENGINE = MergeTree ORDER BY tuple()");

            var rows = Query(duck, $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {table}", r =>
                Enumerable.Range(0, columns.Count).Select(i => r.IsDBNull(i) ? null : Convert(r.GetValue(i))).ToArray());
            using var client = new ClickHouseClient(_container!.GetConnectionString());
            await client.InsertBinaryAsync(table, [.. columns.Select(c => c.Name)], rows);
        }
    }

    private static string ClickHouseType(string duckType) => duckType.ToUpperInvariant() switch
    {
        "BIGINT" => "Int64",
        "INTEGER" => "Int32",
        "VARCHAR" => "String",
        "TIMESTAMP" => "DateTime64(6, 'UTC')",
        "DATE" => "Date32",
        "DOUBLE" => "Float64",
        var d when d.StartsWith("DECIMAL", StringComparison.Ordinal) => d.Replace("DECIMAL", "Decimal", StringComparison.Ordinal),
        var other => throw new NotSupportedException($"No ClickHouse type for DuckDB's {other}; add one to the test fixture."),
    };

    private static object Convert(object value) => value switch
    {
        DateTime t => DateTime.SpecifyKind(t, DateTimeKind.Utc),
        _ => value,
    };

    private static List<T> Query<T>(DuckDBConnection conn, string sql, Func<IDataRecord, T> read)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private static async Task ExecuteAsync(ClickHouseConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
