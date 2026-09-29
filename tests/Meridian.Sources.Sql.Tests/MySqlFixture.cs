using System.Data;
using DuckDB.NET.Data;
using Meridian.Core;
using Meridian.Sources.MySql;
using Meridian.Sources.TestData;
using MySqlConnector;
using Testcontainers.MySql;

namespace Meridian.Sources.Sql.Tests;

/// <summary>
/// MySQL 8.4 in a container, holding the same tables as <see cref="DuckDbFixture"/>. The server runs at +05:00, so a
/// source that didn't put its sessions in UTC would read <c>TIMESTAMP</c> columns five hours out.
/// </summary>
public sealed class MySqlFixture : IDatabaseFixture, IAsyncLifetime
{
    private MySqlContainer? _container;

    public string? SkipReason { get; private set; }

    public DuckDbFixture Reference { get; } = new();

    public SqlPointSource Source(SqlSourceOptions options, DimensionId entity) =>
        new MySqlPointSource(_container!.GetConnectionString(), options, entity);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MySqlBuilder("mysql:8.4").WithCommand("--default-time-zone=+05:00").Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SkipReason = $"MySQL needs Docker (with Linux containers), which isn't available here ({e.GetType().Name}: {e.Message.Split('\n')[0]}).";
            return;
        }

        await using var sql = new MySqlConnection(_container.GetConnectionString());
        await sql.OpenAsync();
        await CopyTablesAsync(sql);
        await ExecuteAsync(sql, "CREATE VIEW wide_as_long AS SELECT entity_id, ts, 'load' AS metric, `load` AS value FROM wide UNION ALL SELECT entity_id, ts, 'hr', hr FROM wide");
        // The same readings in a TIMESTAMP column, written through a +02:00 session: MySQL stores the UTC instant.
        await ExecuteAsync(sql, """
            SET time_zone = '+02:00';
            CREATE TABLE datapoints_tz (entity_id BIGINT, metric VARCHAR(100), ts TIMESTAMP(6), value DOUBLE);
            INSERT INTO datapoints_tz SELECT entity_id, metric, ts + INTERVAL 2 HOUR, value FROM datapoints;
            """);
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        Reference.Dispose();
    }

    /// <summary>Every DuckDB base table, recreated with matching column types and inserted in batches.</summary>
    private async Task CopyTablesAsync(MySqlConnection sql)
    {
        using var duck = new DuckDBConnection(Reference.ConnectionString);
        duck.Open();
        foreach (var table in Query(duck, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_type = 'BASE TABLE' ORDER BY 1", r => r.GetString(0)))
        {
            var columns = Query(duck, $"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position",
                r => (Name: r.GetString(0), Type: MySqlType(r.GetString(1))));
            await ExecuteAsync(sql, $"CREATE TABLE {table} ({string.Join(", ", columns.Select(c => $"`{c.Name}` {c.Type}"))})");

            var rows = Query(duck, $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {table}", r =>
                Enumerable.Range(0, columns.Count).Select(i => r.IsDBNull(i) ? DBNull.Value : Convert(r.GetValue(i))).ToArray());
            string insert = $"INSERT INTO {table} ({string.Join(", ", columns.Select(c => $"`{c.Name}`"))}) VALUES ";
            foreach (var batch in rows.Chunk(500))
            {
                await using var cmd = new MySqlCommand { Connection = sql };
                var values = new List<string>(batch.Length);
                for (int r = 0; r < batch.Length; r++)
                {
                    values.Add("(" + string.Join(", ", Enumerable.Range(0, columns.Count).Select(c => $"@p{r}_{c}")) + ")");
                    for (int c = 0; c < columns.Count; c++) cmd.Parameters.AddWithValue($"@p{r}_{c}", batch[r][c]);
                }
                cmd.CommandText = insert + string.Join(", ", values);
                await cmd.ExecuteNonQueryAsync();
            }
        }
    }

    private static string MySqlType(string duckType) => duckType.ToUpperInvariant() switch
    {
        "BIGINT" => "BIGINT",
        "INTEGER" => "INT",
        "VARCHAR" => "VARCHAR(100)",
        "TIMESTAMP" => "DATETIME(6)",
        "DATE" => "DATE",
        "DOUBLE" => "DOUBLE",
        var d when d.StartsWith("DECIMAL", StringComparison.Ordinal) => d,
        var other => throw new NotSupportedException($"No MySQL type for DuckDB's {other}; add one to the test fixture."),
    };

    private static object Convert(object value) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
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

    private static async Task ExecuteAsync(MySqlConnection conn, string sql)
    {
        await using var cmd = new MySqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
