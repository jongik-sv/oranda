using System.Data.Common;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Tibero.Sessions;

namespace Oranda.Tibero.Results;

/// <summary>
/// 세션과 결과 집합을 잇는 곳(R2 연결): 리더 → 열 목록·셀 읽기·세션 연결점(바쁨 표시·오류 보고·서버 취소)을 채운 <see cref="TiberoResultSet"/>.
/// 실행 서비스(tb-exec)가 쿼리 리더와 REF CURSOR 리더를 모두 이 함수로 감싼다. 호출은 세션 명령 잠금을 쥔 채로 한다(여기서 다시 잡지 않는다).
/// </summary>
internal static class TiberoResultSets
{
    /// <param name="command">리더를 만든 명령(REF CURSOR 면 null). 결과 집합이 Dispose 하고, 하드 취소는 이 명령의 <see cref="DbCommand.Cancel"/> 로 서버 읽기를 끊는다.</param>
    /// <param name="lobPreviewSize">LOB 셀의 미리보기 길이(설정 <c>grid.initialLobFetchSize</c>).</param>
    public static TiberoResultSet Create(TiberoSession session, DbCommand? command, DbDataReader reader, int pageSize,
                                         int lobPreviewSize = TiberoValues.LobPreviewSize)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(reader);
        var driver = session.Driver ?? throw new InvalidOperationException("이 세션에는 드라이버 어댑터가 없어 결과를 읽을 수 없습니다.");
        var (columns, providerTypes) = TiberoValues.Describe(reader);
        var hooks = new TiberoResultHooks
        {
            SetBusy = session.SetBusy,
            ReportFailure = ex => session.ReportFailure(ex),
            CancelServer = command is null ? null : command.Cancel,
        };
        return new TiberoResultSet(session.CommandGate, command, reader, columns,
                                   TiberoValues.CreateCellReader(driver, providerTypes, lobPreviewSize), pageSize, hooks);
    }

    /// <summary>
    /// OUT 매개변수 값. REF CURSOR 는 결과 집합(호출자가 Dispose). 실행이 세션 명령 잠금을 쥔 채 부른다.
    /// </summary>
    public static Dictionary<string, object?> ReadOutValues(TiberoSession session, IReadOnlyList<TiberoOutBind> outs, int pageSize,
                                                            int lobPreviewSize = TiberoValues.LobPreviewSize)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(outs);
        var driver = session.Driver ?? throw new InvalidOperationException("이 세션에는 드라이버 어댑터가 없어 OUT 값을 읽을 수 없습니다.");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in outs)
        {
            var value = TiberoValues.ReadOut(driver, o.Parameter, o.Bind);
            values[o.Name] = value is DbDataReader cursor ? Create(session, null, cursor, pageSize, lobPreviewSize) : value;
        }

        return values;
    }
}
