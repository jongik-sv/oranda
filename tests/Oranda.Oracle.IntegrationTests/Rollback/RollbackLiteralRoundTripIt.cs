using System.Globalization;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Literals;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.Catalog;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Rollback;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Rollback;

// 소유: rb-m2-oracle RB-12. 이 PC 에서는 컴파일까지만 확인한다. 실행은 CI 의 Oracle 컨테이너에서 한다.

/// <summary>
/// 리터럴 왕복 행렬(설계 6.6·16.2, 반목표 「리터럴이 정확하지 않아 모든 원복 문장이 0행」): 형식마다 「값 → 읽는 식 → 리터럴 → <c>WHERE c = 리터럴</c>」 이
/// 정확히 그 한 행이다. NUMBER 38자리, TIMESTAMP(9), 시간대 지역 이름, LTZ, NCHAR 비 ASCII, BINARY_DOUBLE NaN·무한대, 간격, RAW, ROWID.
/// 방언(<see cref="OracleRollbackDialect"/>)과 통로(<see cref="OracleRollbackChannel"/>)를 실제 연결 위에서 쓴다.
/// 같이 보는 것: 카탈로그 <c>rollback.side_effects</c> 의 트리거·CASCADE 행.
/// </summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RollbackLiteralRoundTripIt(OracleFreeFixture db, ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly IReadOnlyDictionary<string, BindValue> NoBinds = new Dictionary<string, BindValue>();
    private readonly string _table = "RBIT_LIT_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly OracleSessionManager _manager = new(new NullBus());
    private OracleSession _session = null!;

    /// <summary>(열 이름, 형식, 넣을 값들의 SQL 식). 값 식은 서버에서 그대로 계산한다(.NET 값으로 바꾸지 않는다).</summary>
    private static readonly (string Column, string Type, string[] Values)[] Matrix =
    [
        ("C_VC", "VARCHAR2(200)", ["'유자차 스틱'", "'it''s'", "'  spaces  '", "NULL"]),
        ("C_CH", "CHAR(10)", ["'ab'"]),
        ("C_NVC", "NVARCHAR2(50)", ["UNISTR('\\C720\\C790 \\D83D\\DE00 \\005C')", "N'abc'"]),
        ("C_NUM", "NUMBER", ["12345678901234567890123456789012345678", "-0.000000000000000000000000000000000001", "4730", "1E125", "0"]),
        ("C_NUM2", "NUMBER(10,2)", ["4730.10", "-1.5"]),
        ("C_FLT", "BINARY_FLOAT", ["1.1F", "BINARY_FLOAT_NAN", "-BINARY_FLOAT_INFINITY"]),
        ("C_DBL", "BINARY_DOUBLE", ["0.1D", "BINARY_DOUBLE_NAN", "BINARY_DOUBLE_INFINITY", "-BINARY_DOUBLE_INFINITY", "-0D"]),
        ("C_DATE", "DATE", ["TO_DATE('2026-11-12 14:05:10', 'YYYY-MM-DD HH24:MI:SS')", "TO_DATE('-4712-01-01', 'SYYYY-MM-DD')"]),
        ("C_TS", "TIMESTAMP(9)", ["TO_TIMESTAMP('2026-11-12 14:05:10.123456789', 'YYYY-MM-DD HH24:MI:SS.FF9')"]),
        ("C_TZ", "TIMESTAMP(9) WITH TIME ZONE",
            ["TO_TIMESTAMP_TZ('2026-11-12 14:05:10.123456789 Asia/Seoul', 'YYYY-MM-DD HH24:MI:SS.FF9 TZR')",
             "TO_TIMESTAMP_TZ('2026-11-12 14:05:10.5 -03:30', 'YYYY-MM-DD HH24:MI:SS.FF TZH:TZM')"]),
        ("C_LTZ", "TIMESTAMP(6) WITH LOCAL TIME ZONE", ["TO_TIMESTAMP_TZ('2026-11-12 14:05:10.654321 +09:00', 'YYYY-MM-DD HH24:MI:SS.FF TZH:TZM')"]),
        ("C_IDS", "INTERVAL DAY(3) TO SECOND(6)", ["INTERVAL '12 03:04:05.123456' DAY(3) TO SECOND(6)", "INTERVAL '-1 00:00:00' DAY TO SECOND"]),
        ("C_IYM", "INTERVAL YEAR(3) TO MONTH", ["INTERVAL '120-11' YEAR(3) TO MONTH"]),
        ("C_RAW", "RAW(16)", ["HEXTORAW('00FF10A0')"]),
    ];

    public async Task InitializeAsync()
    {
        var profile = new ConnectionProfile("dev-example", "system", db.DataSource);
        using var cred = new SecureCredential(db.SystemPassword);
        _session = (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
        var columns = string.Join(", ", Matrix.Select(m => m.Column + " " + m.Type));
        await ExecAsync($"CREATE TABLE {_table} (ID NUMBER PRIMARY KEY, {columns})");
        var id = 0;
        foreach (var (column, _, values) in Matrix)
        {
            foreach (var value in values)
            {
                await ExecAsync($"INSERT INTO {_table} (ID, {column}) VALUES ({++id}, {value})");
            }
        }

        await ExecAsync("COMMIT");
    }

    public async Task DisposeAsync()
    {
        try
        {
            await ExecAsync($"DROP TABLE {_table} PURGE");
        }
        finally
        {
            await _session.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_value_reads_back_to_a_literal_that_finds_exactly_its_row()
    {
        var dialect = OracleRollbackDialect.Instance;
        var channel = OracleRollbackChannel.Locked(_session);
        var failures = new List<string>();
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            foreach (var (name, type, _) in Matrix)
            {
                var column = new RollbackColumn(name, type, dialect.KindOf(new ColumnInfo(name, 1, type)));
                Assert.NotEqual(RollbackValueKind.Large, column.Kind);
                var read = await channel.QueryAsync(
                    $"SELECT ID, {dialect.ReadExpression(column, "t." + name)} FROM {_table} t WHERE {name} IS NOT NULL OR ID = 1 ORDER BY ID",
                    NoBinds, 1000, CancellationToken.None);
                foreach (var row in read.Rows)
                {
                    var id = Convert.ToInt64(row[0], CultureInfo.InvariantCulture);
                    var literal = dialect.Literal(column, row[1], out var reason);
                    if (literal is null)
                    {
                        failures.Add($"{name} #{id}: 리터럴 없음({reason})");
                        continue;
                    }

                    var count = await CountAsync(channel, $"ID = {id} AND {OracleFamilyLiterals.Predicate("t." + name, literal)}");
                    output.WriteLine($"{name} #{id}: {literal} → {count}");
                    if (count != 1)
                    {
                        failures.Add($"{name} #{id}: {literal} 로 {count}행");
                    }
                }
            }
        }
        finally
        {
            _session.CommandGate.Release();
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Rowid_reads_back_to_a_literal_that_finds_its_row()
    {
        var dialect = OracleRollbackDialect.Instance;
        var channel = OracleRollbackChannel.Locked(_session);
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            var read = await channel.QueryAsync($"SELECT ID, {OracleFamilyLiterals.RowIdReadExpression("t")} FROM {_table} t", NoBinds, 1000,
                CancellationToken.None);
            Assert.NotEmpty(read.Rows);
            foreach (var row in read.Rows)
            {
                var literal = dialect.RowIdLiteral((string)row[1]!);
                Assert.Equal(1, await CountAsync(channel, $"ID = {Convert.ToInt64(row[0], CultureInfo.InvariantCulture)} AND ROWID = {literal}"));
            }
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }

    [Fact]
    public async Task Point_queries_and_as_of_read_the_table()
    {
        var dialect = OracleRollbackDialect.Instance;
        var channel = OracleRollbackChannel.Gated(_session);
        long? point = null;
        foreach (var sql in dialect.PointQueries)
        {
            var rows = await channel.QueryAsync(sql, NoBinds, 1, CancellationToken.None);
            var value = Convert.ToInt64(Assert.Single(rows.Rows)[0], CultureInfo.InvariantCulture);
            Assert.True(value > 0);
            point ??= value;
        }

        await Task.Delay(TimeSpan.FromSeconds(8)); // 표를 만든 직후 SCN 은 ORA-01466(대응 간격 몇 초)
        var now = Convert.ToInt64(Assert.Single((await channel.QueryAsync(dialect.PointQueries[0], NoBinds, 1, CancellationToken.None)).Rows)[0],
            CultureInfo.InvariantCulture);
        var asOf = await channel.QueryAsync($"SELECT COUNT(*) FROM {_table} {dialect.AsOf(now)}", NoBinds, 1, CancellationToken.None);
        Assert.Equal(Matrix.Sum(m => m.Values.Length), Convert.ToInt32(asOf.Rows[0][0], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Side_effects_lists_enabled_triggers_and_cascading_children()
    {
        var child = _table + "_C";
        var trigger = _table + "_T";
        await ExecAsync($"CREATE TABLE {child} (ID NUMBER PRIMARY KEY, P_ID NUMBER REFERENCES {_table}(ID) ON DELETE CASCADE)");
        await ExecAsync($"CREATE OR REPLACE TRIGGER {trigger} BEFORE UPDATE ON {_table} FOR EACH ROW BEGIN NULL; END;");
        try
        {
            var binds = new Dictionary<string, BindValue>
            {
                ["owner"] = new(BindDataType.Varchar2, "SYSTEM"),
                ["name"] = new(BindDataType.Varchar2, _table),
            };
            var rows = await OracleRollbackChannel.Gated(_session).QueryAsync(OracleRollbackDialect.Instance.SideEffectsSql!, binds, 100,
                CancellationToken.None);
            var found = rows.Rows.Select(r => (Kind: (string)r[0]!, Name: (string)r[1]!)).ToList();
            Assert.Contains(("TRIGGER", "SYSTEM." + trigger), found);
            Assert.Contains(("CASCADE", "SYSTEM." + child), found);
        }
        finally
        {
            await ExecAsync($"DROP TABLE {child} PURGE");
        }
    }

    private async Task<long> CountAsync(OracleRollbackChannel channel, string where)
    {
        var rows = await channel.QueryAsync($"SELECT COUNT(*) FROM {_table} t WHERE {where}", NoBinds, 1, CancellationToken.None);
        return Convert.ToInt64(rows.Rows[0][0], CultureInfo.InvariantCulture);
    }

    private async Task ExecAsync(string sql)
    {
        await _session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = _session.Connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            _session.CommandGate.Release();
        }
    }

    private sealed class NullBus : Oranda.Core.Events.IEventBus
    {
        public IDisposable Subscribe<T>(object owner, Action<T> handler) where T : Oranda.Core.Events.OrandaEvent => new Nothing();

        public void Publish<T>(T e) where T : Oranda.Core.Events.OrandaEvent
        {
        }

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
