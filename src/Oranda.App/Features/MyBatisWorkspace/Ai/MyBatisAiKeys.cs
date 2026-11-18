using System.Runtime.CompilerServices;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Ai;

/// <summary>
/// MyBatis 문서에서 AI 튜닝·SQL 설명을 켤지 정하는 문맥 키 두 개를 알린다(설계 §3.4, <c>AiCommands.ExpandedSqlEnablement</c>).
/// <list type="bullet">
/// <item><c>mybatisExpandedTab</c>: 활성 탭이 MyBatis 문장 탭이다(펼칠 조합이 있다).</item>
/// <item><c>mybatisExpandedReady</c>: 그 탭을 지금 값으로 펼친 SQL 이 있다(펼치기 실패·빈 SQL 이면 거짓).</item>
/// </list>
/// 활성 탭이 바뀌거나 그 탭의 계산 상태가 다시 계산될 때마다 다시 본다. 따로 시작 작업으로 두지 않고 <see cref="Hooks.MyBatisWorkspaceWiring"/> 이 첫 화면 뒤에 붙인다
/// (역할별 시작 작업 목록을 늘리지 않게).
/// </summary>
public sealed class MyBatisAiKeys
{
    internal const string TabKey = "mybatisExpandedTab";
    internal const string ReadyKey = "mybatisExpandedReady";

    private readonly ContextKeyService _keys;
    private MyBatisEvalHub? _hub;
    private MyBatisStatementEval? _eval;

    public MyBatisAiKeys(ContextKeyService keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
    }

    /// <summary>허브와 그 호스트를 따라가기 시작한다(한 번만. 시험도 부른다).</summary>
    internal void Attach(MyBatisEvalHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        if (_hub is not null)
        {
            return;
        }

        _hub = hub;
        hub.Host.ActiveChanged += (_, _) => Follow();
        hub.Host.ActiveWorkspaceChanged += (_, _) => Follow();
        hub.ActiveChanged += (_, _) => Follow();
        Follow();
    }

    private void Follow()
    {
        var next = _hub?.Host.Active is { } tab ? _hub.ForTab(tab) : null;
        if (!ReferenceEquals(next, _eval))
        {
            if (_eval is not null)
            {
                _eval.Changed -= OnEvalChanged;
            }

            _eval = next;
            if (next is not null)
            {
                next.Changed += OnEvalChanged;
            }
        }

        Push();
    }

    private void OnEvalChanged(object? sender, EventArgs e) => Push();

    private void Push()
    {
        _keys.Set(TabKey, _eval is not null);
        _keys.Set(ReadyKey, _eval is not null && IsReady(_eval));
    }

    /// <summary>펼친 SQL 이 있다(계산 결과는 Language 형이라 이 메서드에서만 읽는다).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool IsReady(MyBatisStatementEval eval) => eval.Result is { Sql: { } sql } && !string.IsNullOrWhiteSpace(sql);
}
