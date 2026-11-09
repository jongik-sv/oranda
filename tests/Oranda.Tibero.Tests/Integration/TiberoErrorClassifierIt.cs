using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>오류 분류·위치를 실제 서버의 오류 모양으로 확인한다(읽기 문장만). 서버·드라이버가 없으면 아무것도 하지 않는다.</summary>
[Trait("Category", "Integration")]
public class TiberoErrorClassifierIt
{
    [TiberoItFact]
    public async Task Server_error_maps_to_kind_and_statement_position()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        const string text = "SELECT *\n  FROM ORT_NO_SUCH_TABLE_XYZ";
        var session = await it.OpenAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(session, text));
        var error = TiberoErrorClassifier.ToDbError(ex, text);

        Assert.NotNull(error);
        Assert.Equal("TBR-8033", error.Code);
        Assert.Equal(DbErrorKind.ObjectNotFound, error.Kind);
        Assert.Equal(Dbms.Tibero, error.Dbms);
        Assert.Equal((16, 2, 8), (error.Offset, error.Line, error.Column));
    }

    [TiberoItFact]
    public async Task Command_on_a_closed_connection_is_a_disconnect()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        // 세션을 거치지 않고 밑 연결만 닫는다(끊긴 연결 모양). 세션 정리는 하니스가 한다.
        await session.Connection.CloseAsync();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ExecuteAsync(session, TiberoSession.KeepAliveSql));

        Assert.Equal(DbErrorKind.Disconnected, new TiberoErrorClassifier().FromException(ex)?.Kind);
    }

    private static async Task ExecuteAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await using var reader = await command.ExecuteReaderAsync();
            _ = await reader.ReadAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
