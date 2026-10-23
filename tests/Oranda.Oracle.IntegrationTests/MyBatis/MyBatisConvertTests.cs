using System.Globalization;
using System.Text.Json;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;

namespace Oranda.Oracle.IntegrationTests.MyBatis;

// 소유: TSK-03-05. acceptance 「탭 4개 작업 공간을 바꿔 연 매퍼에서 각 문장을 첫 시나리오로 실행하면 원래 탭 결과와 같다」(CI 몫).

/// <summary>
/// 바꾸기 사례 정답 파일(<c>MyBatisConvertCases.json</c>) 한 줄: 원래 탭 SQL·바인드와 바꾼 요소·첫 시나리오.
/// 바꾸기 엔진(App)이 이 정답과 같은 요소·시나리오를 만드는지는 App.Tests 의 ConvertTests 가 같은 파일로 확인한다
/// (이 시험 프로젝트는 App 을 참조하지 않는다).
/// </summary>
public sealed record MyBatisConvertCase(
    string Tab,
    string Sql,
    IReadOnlyList<MyBatisConvertCaseBind> Binds,
    string Id,
    string Kind,
    string Element,
    IReadOnlyDictionary<string, MyBatisConvertCaseValue> Scenario)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>저장소 안 정답 파일을 읽는다(실행 폴더에서 위로 올라가며 Oranda.sln 을 찾는다).</summary>
    public static IReadOnlyList<MyBatisConvertCase> Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        var path = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Oranda.sln 을 찾지 못했다"),
            "tests", "Oranda.Oracle.IntegrationTests", "MyBatis", "MyBatisConvertCases.json");
        return JsonSerializer.Deserialize<List<MyBatisConvertCase>>(File.ReadAllText(path), Json) ?? [];
    }
}

/// <summary>탭 바인드 변수 창 한 줄(이름은 창처럼 대문자, 값이 비면 NULL).</summary>
public sealed record MyBatisConvertCaseBind(string Name, string Type, string Value);

/// <summary>시나리오 값 하나(<see cref="MyBatisScenarioValue"/> 의 Kind·Text).</summary>
public sealed record MyBatisConvertCaseValue(string Kind, string? Text);

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class MyBatisConvertTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;

    public MyBatisConvertTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "BEGIN EXECUTE IMMEDIATE 'DROP TABLE MBC_ORDERS'; EXCEPTION WHEN OTHERS THEN NULL; END;");
        await ExecAsync(conn, "CREATE TABLE MBC_ORDERS (ORDER_NO NUMBER PRIMARY KEY, CUST_ID VARCHAR2(10), ORDER_DT DATE, STATUS VARCHAR2(10), MEMO VARCHAR2(40))");
        await ExecAsync(conn, """
            INSERT INTO MBC_ORDERS
            SELECT LEVEL, 'C' || TO_CHAR(MOD(LEVEL, 3), 'FM0000'), DATE '2026-09-25' + LEVEL, 'NEW', 'a:b & <c>'
              FROM DUAL CONNECT BY LEVEL <= 12
            """);
    }

    public async Task DisposeAsync()
    {
        await using var conn = await OpenAsync();
        await ExecAsync(conn, "DROP TABLE MBC_ORDERS PURGE");
    }

    private async Task<OracleConnection> OpenAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("free-system", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task Each_converted_statement_run_with_its_first_scenario_matches_the_original_tab()
    {
        await using var conn = await OpenAsync();
        var cases = MyBatisConvertCase.Load();
        Assert.Equal(4, cases.Count);
        foreach (var c in cases)
        {
            var original = await RunAsync(conn, c.Sql, c.Binds.ToDictionary(b => b.Name, b => (object?)Typed(b.Type, b.Value), StringComparer.OrdinalIgnoreCase));

            var element = Assert.Single(new MapperParser().Parse("<mapper namespace=\"t\">\n" + c.Element + "\n</mapper>").Elements);
            var values = c.Scenario.ToDictionary(kv => kv.Key, kv => ScenarioObject(kv.Value), StringComparer.Ordinal);
            var eval = new MyBatisEvaluator().Evaluate(element, NoFragments.Instance, values, null);
            Assert.Empty(eval.Issues);
            var converted = await RunAsync(conn, eval.Sql, eval.Binds.ToDictionary(b => b.Name, b => Typed(b.JdbcType, b.Value)));

            Assert.True(original == converted, $"{c.Tab}: 원래 {original} / 바꾼 것 {converted}");
        }
    }

    /// <summary>질의면 모든 행을 글자로, DML 이면 영향 행 수와 그 뒤 표 모습을 돌려주고 되돌린다.</summary>
    private static async Task<string> RunAsync(OracleConnection conn, string sql, IReadOnlyDictionary<string, object?> binds)
    {
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new OracleCommand(sql, conn) { BindByName = true, Transaction = (OracleTransaction)tx };
        foreach (var (name, value) in binds)
        {
            cmd.Parameters.Add(new OracleParameter(name, value ?? DBNull.Value));
        }

        string result;
        if (sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            result = await RowsAsync(cmd);
        }
        else
        {
            int affected = await cmd.ExecuteNonQueryAsync();
            await using var after = new OracleCommand("SELECT * FROM MBC_ORDERS ORDER BY ORDER_NO", conn) { Transaction = (OracleTransaction)tx };
            result = affected.ToString(CultureInfo.InvariantCulture) + "|" + await RowsAsync(after);
        }

        await tx.RollbackAsync();
        return result;
    }

    private static async Task<string> RowsAsync(OracleCommand cmd)
    {
        var rows = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var cells = new object[r.FieldCount];
            r.GetValues(cells);
            rows.Add(string.Join(",", cells.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture))));
        }

        return rows.Count + ":" + string.Join(";", rows);
    }

    /// <summary>원래 탭: 바인드 변수 창 형식대로. 바꾼 문장: jdbcType 이 있으면 그 형, 없으면 값 그대로(TSK-03-02 실행 규칙).</summary>
    private static object? Typed(string? type, object? value)
    {
        if (value is null || value is string { Length: 0 })
        {
            return null;
        }

        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        return type?.ToUpperInvariant() switch
        {
            "DATE" => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None),
            "TIMESTAMP" => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.None),
            "NUMBER" or "NUMERIC" or "DECIMAL" or "INTEGER" or "BIGINT" => decimal.Parse(text, CultureInfo.InvariantCulture),
            _ => value,
        };
    }

    private static object? ScenarioObject(MyBatisConvertCaseValue v) => v.Kind switch
    {
        "Null" => null,
        "Number" => decimal.Parse(v.Text!, CultureInfo.InvariantCulture),
        "Boolean" => bool.Parse(v.Text!),
        _ => v.Text,
    };

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = new OracleCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public static NoFragments Instance { get; } = new();

        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }
}
