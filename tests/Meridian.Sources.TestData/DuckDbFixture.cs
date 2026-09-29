using DuckDB.NET.Data;

namespace Meridian.Sources.TestData;

/// <summary>
/// A small DuckDB file with the awkward cases. <c>datapoints</c> holds UTC readings every 5 hours from
/// January to May 2025 — across the US (9 Mar), UK (30 Mar) and Sydney (6 Apr) DST changes — with dropped
/// rows, NULL values, a multi-week hole, an all-NULL week, leading all-NULL days, and a second metric.
/// <c>wellness</c> holds calendar dates (DATE). <c>legacy</c> holds London wall-clock times, including the
/// hour that happens twice and the hour that never happens.
/// </summary>
public sealed class DuckDbFixture : IDisposable
{
    public string FilePath { get; } = Path.Combine(Path.GetTempPath(), $"meridian-{Guid.NewGuid():N}.duckdb");
    public string ConnectionString => $"Data Source={FilePath}";

    public DuckDbFixture()
    {
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE datapoints AS
            WITH raw AS (
                SELECT e.entity_id, h.h, hash(e.entity_id * 1000003 + h.h) AS hv
                FROM range(1, 4) AS e(entity_id), range(0, 2880, 5) AS h(h)   -- 120 days, every 5 hours
            )
            SELECT entity_id,
                   m.metric,
                   TIMESTAMP '2025-01-01' + to_hours(h) AS ts,
                   CASE
                       WHEN hv % 11 = 0 THEN NULL
                       WHEN entity_id = 1 AND h < 48 THEN NULL                       -- leading all-NULL days
                       WHEN entity_id = 3 AND h BETWEEN 60 * 24 AND 67 * 24 THEN NULL -- an all-NULL week
                       ELSE (hv % 1000) / 10.0 + CASE WHEN m.metric = 'hr' THEN 1000 ELSE 0 END
                   END AS value
            FROM raw, (VALUES ('load'), ('hr')) AS m(metric)
            WHERE hv % 7 <> 0
              AND NOT (entity_id = 2 AND h BETWEEN 30 * 24 AND 45 * 24);         -- a multi-week hole

            CREATE TABLE wellness AS
            SELECT e.entity_id, 'wellness' AS metric, DATE '2025-01-01' + CAST(d.d AS INTEGER) AS ts,
                   CAST(hash(e.entity_id * 7919 + d.d) % 10 AS DOUBLE) AS value
            FROM range(1, 4) AS e(entity_id), range(0, 120) AS d(d)
            WHERE hash(e.entity_id * 31 + d.d) % 6 <> 0;

            -- The same readings in the wide layout (a column per metric), with extra NULLs in hr; and that wide
            -- table unpivoted back to the long layout, NULLs kept, as the reference it must match.
            CREATE TABLE wide AS
            SELECT entity_id, ts,
                   max(value) FILTER (WHERE metric = 'load') AS load,
                   CASE WHEN hash(entity_id * 131 + epoch(ts)) % 5 = 0 THEN NULL
                        ELSE max(value) FILTER (WHERE metric = 'hr') END AS hr
            FROM datapoints GROUP BY entity_id, ts;
            CREATE VIEW wide_as_long AS SELECT * FROM wide UNPIVOT INCLUDE NULLS (value FOR metric IN (load, hr));

            -- Goals with the metadata dashboards group by: venue (sometimes unknown) and competition.
            CREATE TABLE goals AS
            SELECT e.entity_id, 'goals' AS metric, TIMESTAMP '2025-01-01' + to_hours(m.m * 37) AS ts,
                   CAST(hash(e.entity_id * 11 + m.m) % 3 AS DOUBLE) AS value,
                   CASE WHEN hash(e.entity_id * 5 + m.m) % 7 = 0 THEN NULL
                        WHEN hash(e.entity_id * 13 + m.m) % 2 = 0 THEN 'Home' ELSE 'Away' END AS venue,
                   CASE hash(e.entity_id * 3 + m.m) % 3 WHEN 0 THEN 'League' WHEN 1 THEN 'Cup' ELSE 'Europe' END AS competition
            FROM range(1, 4) AS e(entity_id), range(0, 80) AS m(m);
            CREATE TABLE goals_wide AS SELECT entity_id, ts, value AS goals, venue, competition FROM goals;

            -- Appearances: one minutes row per match; goal rows only when a goal was scored (events).
            CREATE TABLE apps AS
            WITH m AS (
                SELECT e.entity_id, TIMESTAMP '2025-01-04 15:00:00' + to_days(g.g * 7) AS ts,
                       CASE WHEN hash(e.entity_id * 17 + g.g) % 2 = 0 THEN 'Home' ELSE 'Away' END AS venue,
                       CAST(10 + hash(e.entity_id * 29 + g.g) % 81 AS DOUBLE) AS minutes,
                       CAST(hash(e.entity_id * 31 + g.g) % 4 AS DOUBLE) - 1 AS goals,
                       CAST(hash(e.entity_id * 37 + g.g) % 3 AS DOUBLE) - 1 AS assists
                FROM range(1, 4) AS e(entity_id), range(0, 22) AS g(g)
                WHERE hash(e.entity_id * 7 + g.g) % 5 <> 0 AND NOT (e.entity_id = 3 AND g.g BETWEEN 9 AND 14)
            )
            SELECT entity_id, 'minutes' AS metric, ts, minutes AS value, venue FROM m
            UNION ALL
            SELECT entity_id, 'goals', ts, goals, venue FROM m WHERE goals > 0
            UNION ALL
            SELECT entity_id, 'assists', ts, assists, venue FROM m WHERE assists > 0;

            -- Two seasons of weekly matches (July 2023 to June 2025), for comparisons with an earlier period.
            CREATE TABLE seasons AS
            WITH m AS (
                SELECT e.entity_id, TIMESTAMP '2023-07-05 15:00:00' + to_days(g.g * 7) AS ts,
                       CASE WHEN hash(e.entity_id * 19 + g.g) % 2 = 0 THEN 'Home' ELSE 'Away' END AS venue,
                       CAST(20 + hash(e.entity_id * 23 + g.g) % 71 AS DOUBLE) AS minutes,
                       CAST(hash(e.entity_id * 41 + g.g) % 3 AS DOUBLE) AS goals
                FROM range(1, 4) AS e(entity_id), range(0, 104) AS g(g)
                WHERE hash(e.entity_id * 43 + g.g) % 6 <> 0
            )
            SELECT entity_id, 'minutes' AS metric, ts, minutes AS value, venue FROM m
            UNION ALL
            SELECT entity_id, 'goals', ts, goals, venue FROM m WHERE goals > 0;

            -- A level: open issues per site, family and priority, snapshotted daily (a few snapshots missing).
            CREATE TABLE levels AS
            SELECT s.site AS entity_id, 'open-issues' AS metric, TIMESTAMP '2025-01-01' + to_days(d.d) AS ts,
                   CASE WHEN hash(s.site * 101 + d.d * 7 + f.f * 3 + p.p) % 13 = 0 THEN NULL
                        ELSE CAST(5 * f.f + 2 * p.p + (hash(s.site + d.d * 31 + f.f * 5 + p.p) % 9) AS DOUBLE) END AS value,
                   'family' || f.f AS family, 'p' || p.p AS priority
            FROM range(1, 3) AS s(site), range(0, 120) AS d(d), range(1, 3) AS f(f), range(1, 4) AS p(p);

            CREATE TABLE typed (entity_id INTEGER, metric VARCHAR, ts TIMESTAMP, value DECIMAL(10, 2));
            INSERT INTO typed VALUES (1, 'typed', TIMESTAMP '2025-02-01 10:00', 12.50), (1, 'typed', TIMESTAMP '2025-02-02 10:00', 7.25);

            CREATE TABLE legacy (entity_id BIGINT, metric VARCHAR, ts TIMESTAMP, value DOUBLE);
            INSERT INTO legacy VALUES
                (1, 'legacy', TIMESTAMP '2025-03-30 00:30:00', 1),   -- before the spring gap
                (1, 'legacy', TIMESTAMP '2025-03-30 01:30:00', 2),   -- never happens in London
                (1, 'legacy', TIMESTAMP '2025-07-01 12:00:00', 3),   -- summer: UTC+1
                (1, 'legacy', TIMESTAMP '2025-10-26 01:30:00', 4);   -- happens twice in London

            -- New York wall-clock readings around both 2025 DST changes, as a system that logs local time
            -- would store them: the skipped spring hour has rows, and the repeated autumn hour is logged twice.
            -- Spacing avoids exact ties after conversion ("last" among identical instants is unspecified):
            -- March readings are 7 minutes apart, so a skipped time shifted forward an hour never lands on a
            -- real one; the autumn second pass is offset by 5 minutes.
            CREATE TABLE legacy_ny AS
            WITH walls AS (
                SELECT e.entity_id, TIMESTAMP '2025-03-01' + to_minutes(m.m * 7) AS ts, m.m
                FROM range(1, 3) AS e(entity_id), range(0, 20 * 1440 // 7) AS m(m)
                UNION ALL
                SELECT e.entity_id, TIMESTAMP '2025-10-20' + to_minutes(m.m * 10), m.m + 100000
                FROM range(1, 3) AS e(entity_id), range(0, 26 * 144) AS m(m)
                UNION ALL
                SELECT e.entity_id, TIMESTAMP '2025-11-02 01:05' + to_minutes(m.m * 10), m.m + 200000
                FROM range(1, 3) AS e(entity_id), range(0, 6) AS m(m)
            )
            SELECT entity_id, 'legacy-ny' AS metric, ts,
                   CASE WHEN hash(entity_id * 131 + m) % 13 = 0 THEN NULL
                        ELSE CAST(hash(entity_id * 977 + m) % 500 AS DOUBLE) / 10 END AS value
            FROM walls
            WHERE hash(entity_id * 17 + m) % 9 <> 0;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The datapoints table as a Parquet file, for sources that query files in place.</summary>
    public string ParquetPath { get; } = Path.Combine(Path.GetTempPath(), $"meridian-{Guid.NewGuid():N}.parquet");

    public void ExportParquet()
    {
        if (File.Exists(ParquetPath)) return;
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"COPY datapoints TO '{ParquetPath.Replace(Path.DirectorySeparatorChar, '/')}' (FORMAT parquet)";
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<T> Rows<T>(string sql, Func<System.Data.IDataRecord, T> read)
    {
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    public long Scalar(string sql)
    {
        using var conn = new DuckDBConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Dispose()
    {
        try { File.Delete(FilePath); File.Delete(FilePath + ".wal"); File.Delete(ParquetPath); } catch (IOException) { }
    }
}
