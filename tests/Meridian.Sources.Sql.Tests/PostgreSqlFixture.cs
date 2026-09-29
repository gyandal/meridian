using DuckDB.NET.Data;
using Meridian.Core;
using Meridian.Sources.PostgreSql;
using Meridian.Sources.TestData;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.PostgreSql;

namespace Meridian.Sources.Sql.Tests;

/// <summary>A database under test: the shared DuckDB data copied into it, or the reason it isn't available.</summary>
public interface IDatabaseFixture
{
    /// <summary>Why the database can't be used here (no Docker, say), or null when it can.</summary>
    string? SkipReason { get; }

    /// <summary>The generated data, in DuckDB: the reference every other database is compared with.</summary>
    DuckDbFixture Reference { get; }

    SqlPointSource Source(SqlSourceOptions options, DimensionId entity);
}

/// <summary>PostgreSQL 17 in a container, holding the same tables as <see cref="DuckDbFixture"/>.</summary>
public sealed class PostgreSqlFixture : IDatabaseFixture, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private NpgsqlDataSource? _dataSource; // one pool for every test's sources, as an app would share one

    public string? SkipReason { get; private set; }

    public DuckDbFixture Reference { get; } = new();

    public SqlPointSource Source(SqlSourceOptions options, DimensionId entity) =>
        new PostgreSqlPointSource(_dataSource!, options, entity);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SkipReason = $"PostgreSQL needs Docker, which isn't available here ({e.GetType().Name}: {e.Message.Split('\n')[0]}).";
            return;
        }

        // Deliberately not in UTC: the source must switch borrowed connections to UTC itself.
        _dataSource = NpgsqlDataSource.Create(_container.GetConnectionString() + ";Timezone=Pacific/Chatham");
        await using var pg = new NpgsqlConnection(_container.GetConnectionString());
        await pg.OpenAsync();
        await CopyTablesAsync(pg);
        await ExecuteAsync(pg, """
            CREATE VIEW wide_as_long AS
                SELECT entity_id, ts, 'load' AS metric, load AS value FROM wide
                UNION ALL SELECT entity_id, ts, 'hr', hr FROM wide;
            -- The same readings in a timestamptz column: it must read exactly like the UTC timestamp column.
            CREATE TABLE datapoints_tz AS SELECT entity_id, metric, ts AT TIME ZONE 'UTC' AS ts, value FROM datapoints;
            ANALYZE;
            """);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
        Reference.Dispose();
    }

    /// <summary>Every DuckDB base table, recreated with matching column types and bulk-copied row for row.</summary>
    private async Task CopyTablesAsync(NpgsqlConnection pg)
    {
        using var duck = new DuckDBConnection(Reference.ConnectionString);
        duck.Open();
        var tables = Query(duck, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_type = 'BASE TABLE' ORDER BY 1", r => r.GetString(0));
        foreach (var table in tables)
        {
            var columns = Query(duck, $"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position",
                r => (Name: r.GetString(0), Type: PostgresType(r.GetString(1))));
            await ExecuteAsync(pg, $"CREATE TABLE {table} ({string.Join(", ", columns.Select(c => $"{c.Name} {c.Type.Sql}"))})");

            await using var writer = await pg.BeginBinaryImportAsync($"COPY {table} ({string.Join(", ", columns.Select(c => c.Name))}) FROM STDIN (FORMAT BINARY)");
            using var cmd = duck.CreateCommand();
            cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {table}";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                await writer.StartRowAsync();
                for (int i = 0; i < columns.Count; i++)
                {
                    if (reader.IsDBNull(i)) await writer.WriteNullAsync();
                    else await writer.WriteAsync(Convert(reader.GetValue(i), columns[i].Type.Db), columns[i].Type.Db);
                }
            }
            await writer.CompleteAsync();
        }
    }

    private static (string Sql, NpgsqlDbType Db) PostgresType(string duckType) => duckType.ToUpperInvariant() switch
    {
        "BIGINT" => ("bigint", NpgsqlDbType.Bigint),
        "INTEGER" => ("integer", NpgsqlDbType.Integer),
        "VARCHAR" => ("text", NpgsqlDbType.Text),
        "TIMESTAMP" => ("timestamp", NpgsqlDbType.Timestamp),
        "DATE" => ("date", NpgsqlDbType.Date),
        "DOUBLE" => ("double precision", NpgsqlDbType.Double),
        var d when d.StartsWith("DECIMAL", StringComparison.Ordinal) => (d.Replace("DECIMAL", "numeric", StringComparison.Ordinal), NpgsqlDbType.Numeric),
        var other => throw new NotSupportedException($"No PostgreSQL type for DuckDB's {other}; add one to the test fixture."),
    };

    private static object Convert(object value, NpgsqlDbType type) => (value, type) switch
    {
        (DateTime t, NpgsqlDbType.Timestamp) => DateTime.SpecifyKind(t, DateTimeKind.Unspecified),
        (DateTime t, NpgsqlDbType.Date) => DateOnly.FromDateTime(t),
        (DateOnly d, NpgsqlDbType.Date) => d,
        (_, NpgsqlDbType.Bigint) => System.Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
        (_, NpgsqlDbType.Integer) => System.Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
        (_, NpgsqlDbType.Double) => System.Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        _ => value,
    };

    private static List<T> Query<T>(DuckDBConnection conn, string sql, Func<System.Data.IDataRecord, T> read)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
