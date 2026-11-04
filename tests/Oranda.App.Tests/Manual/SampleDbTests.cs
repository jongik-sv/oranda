using OracleConnection = Oracle.ManagedDataAccess.Client.OracleConnection;

namespace Oranda.App.Tests.Manual;

/// <summary>샘플 스크립트 분해 시험과 설치 실행(<c>ORANDA_MANUAL_DB_INSTALL</c> 이 있을 때만, 로컬 샘플 DB 전용).</summary>
public sealed class SampleDbTests
{
    [Fact]
    public void Split_separates_plain_statements_and_plsql_blocks()
    {
        const string script = """
            -- 주석
            CREATE TABLE t (id NUMBER);
            INSERT INTO t VALUES (1);

            BEGIN
              NULL;
            END;
            /

            CREATE OR REPLACE PACKAGE p AS
              PROCEDURE q;
            END p;
            /
            """;

        var statements = SampleDb.Split(script);

        Assert.Equal(4, statements.Count);
        Assert.Equal("CREATE TABLE t (id NUMBER)", statements[0]);
        Assert.Equal("INSERT INTO t VALUES (1)", statements[1]);
        Assert.StartsWith("BEGIN", statements[2], StringComparison.Ordinal);
        Assert.EndsWith("END;", statements[2], StringComparison.Ordinal);
        Assert.StartsWith("CREATE OR REPLACE PACKAGE p", statements[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Every_script_file_splits_into_at_least_one_statement()
    {
        foreach (var file in Directory.GetFiles(SampleDb.ScriptDir, "0*.sql"))
        {
            Assert.NotEmpty(SampleDb.Split(File.ReadAllText(file)));
        }
    }

    [Fact]
    public async Task Install_scripts_into_the_sample_account_when_requested()
    {
        var mode = Environment.GetEnvironmentVariable("ORANDA_MANUAL_DB_INSTALL");
        if (string.IsNullOrEmpty(mode) || !SampleDb.Configured)
        {
            return;
        }

        await SampleDb.InstallAsync(reset: mode == "reset");
    }

    /// <summary>설치된 샘플 DB 의 모양(행 수·인덱스·패키지). 캡처가 이 값에 의존하므로 환경 변수가 있을 때 확인한다.</summary>
    [Fact]
    public async Task Installed_sample_has_the_documented_shape()
    {
        if (!SampleDb.Configured)
        {
            return;
        }

        await using var connection = new OracleConnection(SampleDb.ConnectionString);
        await connection.OpenAsync();
        async Task<long> Count(string sql) => Convert.ToInt64(await SampleDb.ScalarAsync(connection, sql), System.Globalization.CultureInfo.InvariantCulture);

        if (await Count("SELECT COUNT(*) FROM user_tables WHERE table_name = 'ORDERS'") == 0)
        {
            return; // 아직 설치하지 않은 계정
        }

        Assert.Equal(8, await Count("SELECT COUNT(*) FROM departments"));
        Assert.Equal(120, await Count("SELECT COUNT(*) FROM employees"));
        Assert.Equal(2000, await Count("SELECT COUNT(*) FROM customers"));
        Assert.Equal(80, await Count("SELECT COUNT(*) FROM products"));
        Assert.Equal(20000, await Count("SELECT COUNT(*) FROM orders"));
        Assert.True(await Count("SELECT COUNT(*) FROM order_items") > 40000);
        Assert.Equal(4, await Count("SELECT COUNT(DISTINCT status) FROM orders"));
        Assert.Equal(0, await Count("SELECT COUNT(*) FROM orders WHERE total_amount = 0"));
        Assert.Equal(4, await Count("SELECT COUNT(*) FROM user_indexes WHERE index_name IN ('ORDERS_CUSTOMER_IX','ORDERS_DATE_IX','ORDER_ITEMS_PRODUCT_IX','EMPLOYEES_DEPT_IX')"));
        Assert.Equal(2, await Count("SELECT COUNT(*) FROM user_views"));
        Assert.Equal(2, await Count("SELECT COUNT(*) FROM user_objects WHERE object_name = 'SAMPLE_PKG' AND status = 'VALID'"));
    }
}
