using System.Data;
using DuckDB.NET.Data;
using Meridian.Core;
using Meridian.Sources.SqlServer;
using Meridian.Sources.TestData;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace Meridian.Sources.Sql.Tests;

/// <summary>SQL Server 2022 in a container, holding the same tables as <see cref="DuckDbFixture"/>.</summary>
public sealed class SqlServerFixture : IDatabaseFixture, IAsyncLifetime
{
    private MsSqlContainer? _container;

    public string? SkipReason { get; private set; }

    public DuckDbFixture Reference { get; } = new();

    public SqlPointSource Source(SqlSourceOptions options, DimensionId entity) =>
        new SqlServerPointSource(_container!.GetConnectionString(), options, entity);

    public async Task InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SkipReason = $"SQL Server needs Docker (with Linux containers), which isn't available here ({e.GetType().Name}: {e.Message.Split('\n')[0]}).";
            return;
        }

        await using var sql = new SqlConnection(_container.GetConnectionString());
        await sql.OpenAsync();
        await CopyTablesAsync(sql);
        await ExecuteAsync(sql, "CREATE VIEW wide_as_long AS SELECT entity_id, ts, 'load' AS metric, [load] AS value FROM wide UNION ALL SELECT entity_id, ts, 'hr', hr FROM wide");
        // The same readings as datetimeoffset values stored at +02:00: each is the same instant, with its own offset,
        // which the source must apply before bucketing.
        await ExecuteAsync(sql, "SELECT entity_id, metric, TODATETIMEOFFSET(DATEADD(hour, 2, ts), '+02:00') AS ts, value INTO datapoints_tz FROM datapoints");
    }

    public async Task DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
        Reference.Dispose();
    }

    /// <summary>Every DuckDB base table, recreated with matching column types and bulk-copied row for row.</summary>
    private async Task CopyTablesAsync(SqlConnection sql)
    {
        using var duck = new DuckDBConnection(Reference.ConnectionString);
        duck.Open();
        foreach (var table in Query(duck, "SELECT table_name FROM information_schema.tables WHERE table_schema = 'main' AND table_type = 'BASE TABLE' ORDER BY 1", r => r.GetString(0)))
        {
            var columns = Query(duck, $"SELECT column_name, data_type FROM information_schema.columns WHERE table_name = '{table}' ORDER BY ordinal_position",
                r => (Name: r.GetString(0), Type: SqlServerType(r.GetString(1))));
            await ExecuteAsync(sql, $"CREATE TABLE {table} ({string.Join(", ", columns.Select(c => $"[{c.Name}] {c.Type.Sql}"))})");

            var rows = new DataTable();
            foreach (var column in columns) rows.Columns.Add(column.Name, column.Type.Clr);
            using (var cmd = duck.CreateCommand())
            {
                cmd.CommandText = $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {table}";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var values = new object[columns.Count];
                    for (int i = 0; i < columns.Count; i++)
                    {
                        values[i] = reader.IsDBNull(i) ? DBNull.Value : Convert(reader.GetValue(i), columns[i].Type.Clr);
                    }
                    rows.Rows.Add(values);
                }
            }
            using var bulk = new SqlBulkCopy(sql) { DestinationTableName = table, BulkCopyTimeout = 0 };
            foreach (var column in columns) bulk.ColumnMappings.Add(column.Name, column.Name);
            await bulk.WriteToServerAsync(rows);
        }
    }

    private static (string Sql, Type Clr) SqlServerType(string duckType) => duckType.ToUpperInvariant() switch
    {
        "BIGINT" => ("bigint", typeof(long)),
        "INTEGER" => ("int", typeof(int)),
        "VARCHAR" => ("varchar(100)", typeof(string)),
        "TIMESTAMP" => ("datetime2", typeof(DateTime)),
        "DATE" => ("date", typeof(DateTime)),
        "DOUBLE" => ("float", typeof(double)),
        var d when d.StartsWith("DECIMAL", StringComparison.Ordinal) => (d, typeof(decimal)),
        var other => throw new NotSupportedException($"No SQL Server type for DuckDB's {other}; add one to the test fixture."),
    };

    private static object Convert(object value, Type clr) => value switch
    {
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => System.Convert.ChangeType(value, clr, System.Globalization.CultureInfo.InvariantCulture),
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

    private static async Task ExecuteAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
