using Oranda.Core.Execution;

namespace Oranda.App.Features.SqlEditor;

/// <summary>
/// 탭에서 마지막으로 서버에 보낸 일: 쓴 세션, 그 문장이 그 세션에서 몇 번째였는지(<paramref name="Sequence"/>, 0 = 기록 없음), 마지막 질의·DML 문장.
/// 실행 엔진은 커서(sql_id)를 알려 주지 않으므로 커서는 담지 않는다. 세션의 현재 순번(<see cref="Execution.SessionRunLog"/>)과 같을 때만
/// 이 탭이 세션의 마지막 실행 주체이고, 그때만 「세션의 직전 문장」의 계획이 이 탭의 것이다.
/// </summary>
public sealed record LastExecution(Guid? SessionId, long Sequence, SqlStatement? Statement);

// SqlEditorViewModel 의 일부: 실행 기억. 이 탭이 시작해서 서버에 보낸 실행만 담으므로 다른 탭·다른 기능의 실행과 섞이지 않는다.
public sealed partial class SqlEditorViewModel
{
    /// <summary>이 탭에서 마지막으로 서버에 보낸 일(없으면 <see cref="LastExecution.Sequence"/> 가 0).</summary>
    public LastExecution LastRun { get; private set; } = new(null, 0, null);

    /// <summary>실행 컨트롤러가 이 탭의 문장을 서버에 보내고 마칠 때 부른다. 질의·DML 만 「다시 실행」 대상으로 기억한다.</summary>
    internal void RecordRun(SqlStatement statement, Guid sessionId, long sequence)
    {
        ArgumentNullException.ThrowIfNull(statement);
        LastRun = new LastExecution(
            sessionId,
            sequence,
            statement.Kind is StatementKind.Query or StatementKind.Dml ? statement : LastRun.Statement);
    }
}
