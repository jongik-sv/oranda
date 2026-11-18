using System.Runtime.CompilerServices;
using Oranda.App.Features.Ai.Tuning;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.MyBatisWorkspace.Run;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Composition;
using Oranda.Core.Execution;

namespace Oranda.App.Features.MyBatisWorkspace.Ai;

/// <summary>
/// AI 튜닝·SQL 설명에서 MyBatis 문장 탭을 「지금 값으로 펼친 SQL」 로 보이게 한다(설계 §3.4, TSK-15-08). 대상은 그 탭의 계산 상태(<see cref="MyBatisEvalHub.ForTab"/>)가
/// 낸 펼친 SQL 이고, 바인드 값은 「시나리오 값으로 실행」 과 같은 변환(<see cref="MyBatisRunBinds"/>)을 쓴다. 매퍼 XML 은 AI 경로로 바뀌지 않는다(결정 AE-5):
/// 제안은 새 SQL 탭으로 가고, 이 탭의 글을 바꾸는 호출은 감싸는 쪽(<see cref="AiSqlSourceEditor"/>)이 거절한다.
/// </summary>
/// <remarks>
/// 시작 시험 제약: 계산 결과(<c>EvalResult</c>, Oranda.Language 형)는 필드로 두지 않고 맡은 탭일 때만 도는 메서드에서 읽는다.
/// 생성자는 등록된 서비스(<see cref="IServiceProvider"/>)만 받는다.
/// </remarks>
[OrandaService(typeof(IAiSqlSource))]
public sealed class MyBatisAiSqlSource : IAiSqlSource
{
    /// <summary>재작성 제안 카드에 붙는 한 줄.</summary>
    public const string Note = "펼친 SQL 기준입니다. XML 은 바뀌지 않습니다.";

    private readonly IServiceProvider _services;

    public MyBatisAiSqlSource(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
    }

    /// <summary>시험용: 허브를 직접 넣는다.</summary>
    internal static MyBatisAiSqlSource For(MyBatisEvalHub hub) => new(new MyBatisServiceMap().With(hub));

    public string? ProposalNote => Note;

    private MyBatisEvalHub? Hub => _services.GetService(typeof(MyBatisEvalHub)) as MyBatisEvalHub;

    public bool Owns(Guid tabId) => EvalOf(tabId) is not null;

    public string? SqlOf(Guid tabId) => EvalOf(tabId) is { } eval ? ReadSql(eval) : null;

    public IReadOnlyDictionary<string, BindValue> BindsOf(Guid tabId) =>
        EvalOf(tabId) is { } eval ? ReadBinds(eval) : new Dictionary<string, BindValue>();

    public void CopyBinds(Guid fromTab, Guid toTab)
    {
        if (EvalOf(fromTab) is not { } eval || Hub?.Host.Tabs.FirstOrDefault(t => t.TabId == toTab) is not { } target)
        {
            return;
        }

        CopyInto(eval, target);
    }

    /// <summary>탭이 MyBatis 문장 탭이면 그 계산 상태. 아니면(일반 SQL 탭·닫힌 탭·허브 없음) null.</summary>
    private MyBatisStatementEval? EvalOf(Guid tabId) =>
        Hub is { } hub && hub.Host.Tabs.FirstOrDefault(t => t.TabId == tabId) is { } tab ? hub.ForTab(tab) : null;

    /// <summary>예약된 다시 계산을 끝내고(실행과 같다) 펼친 SQL 을 읽는다. 아직 계산한 적이 없으면 지금 계산한다. 못 만들었거나 비었으면 null.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? ReadSql(MyBatisStatementEval eval)
    {
        if (eval.Result is null && eval.Error is null)
        {
            _ = eval.EnsureLoadedAsync(); // 처음 읽기: 계산은 바로 끝나고 시나리오 읽기만 이어진다
        }

        eval.Flush();
        return eval.Result is { Sql: { } sql } && !string.IsNullOrWhiteSpace(sql) ? sql : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Dictionary<string, BindValue> ReadBinds(MyBatisStatementEval eval)
    {
        eval.Flush();
        return eval.Result is { } result
            ? MyBatisRunBinds.Build(result.Binds, out _, eval.RowTypeOf) ?? new Dictionary<string, BindValue>()
            : new Dictionary<string, BindValue>();
    }

    /// <summary>새 탭이 쓰는 이름 중 그 탭에 값이 아직 없는 줄만 채운다(일반 탭의 바인드 복사와 같은 규칙: 사용자가 넣은 값은 건드리지 않는다).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyInto(MyBatisStatementEval eval, SqlEditorViewModel target)
    {
        eval.Flush();
        if (eval.Result is not { } result)
        {
            return;
        }

        var referenced = new HashSet<string>(target.ReferencedBindNames, StringComparer.OrdinalIgnoreCase);
        var rows = MyBatisRunBinds.ToWorkspaceBinds(result.Binds, eval.RowTypeOf)
            .Where(b => referenced.Contains(b.Name) && !b.IsNull && target.Binds.Find(b.Name) is null or { Text.Length: 0 });
        target.Binds.Restore(rows.ToList());
    }
}
