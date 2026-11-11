using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 「대상에 열 추가」(PC1, 결정 D-066)가 Tibero 대상에 보내는 문장(<see cref="CopyAddColumnDdl"/>)이 Tibero 에서 그대로 되는지 잰다. 첫 계정 하나로만 돈다(두 번째 계정은 쓰지 않는다).
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>. 없으면 건너뛴다(<see cref="TiberoWriteFactAttribute"/>).</item>
/// <item>시나리오: 행이 있는 <c>ORT_PC1_AC</c> 에 같은 DBMS 열(Tibero 사전의 VARCHAR → <c>VARCHAR2(n CHAR)</c>)과 교차 열(Oracle 긴 RAW → <c>BLOB</c>)을 더하고,
/// 두 열이 NULL 허용으로 생겼는지, 원래 행이 그대로인지 본다. 끝에 <c>DROP TABLE … PURGE</c>.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoAddColumnIt
{
    private static readonly string Table = TiberoItSessions.ObjectName("PC1_AC");

    [TiberoWriteFact]
    public async Task Add_column_statements_run_on_tibero_and_add_nullable_columns_to_a_table_with_rows()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser;
        Assert.False(string.IsNullOrEmpty(owner), "the session user of the connection is unknown");
        await SilentAsync(session, $"DROP TABLE {Table} PURGE");
        try
        {
            await ExecAsync(session, $"CREATE TABLE {Table} (ID NUMBER PRIMARY KEY)");
            await ExecAsync(session, $"INSERT INTO {Table} VALUES (1)");
            await ExecAsync(session, "COMMIT");

            var same = CopyAddColumnDdl.Plan(Dbms.Tibero, Dbms.Tibero,
                new ColumnInfo("NOTE", 2, "VARCHAR") { Length = 30, IsNullable = false, DefaultValue = "'x'" }, owner!, Table, null, out var sameRefused);
            Assert.Null(sameRefused);
            var cross = CopyAddColumnDdl.Plan(Dbms.Oracle, Dbms.Tibero,
                new ColumnInfo("PHOTO", 3, "RAW") { Length = 3000 }, owner!, Table, null, out var crossRefused);
            Assert.Null(crossRefused);

            await ExecAsync(session, same!.Ddl);
            await ExecAsync(session, cross!.Ddl);

            Assert.Equal("Y", await ScalarAsync(session, $"SELECT NULLABLE FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{Table}' AND COLUMN_NAME = 'NOTE'"));
            Assert.Equal("BLOB", await ScalarAsync(session, $"SELECT DATA_TYPE FROM USER_TAB_COLUMNS WHERE TABLE_NAME = '{Table}' AND COLUMN_NAME = 'PHOTO'"));
            Assert.Equal("1", await ScalarAsync(session, $"SELECT COUNT(*) FROM {Table} WHERE NOTE IS NULL AND PHOTO IS NULL"));
        }
        finally
        {
            await SilentAsync(session, $"DROP TABLE {Table} PURGE");
        }
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task<string?> ScalarAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            session.CommandGate.Release();
        }
    }

    private static async Task SilentAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 없는 표를 지우려 했다 — 실패해도 시험 결과를 가리지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
