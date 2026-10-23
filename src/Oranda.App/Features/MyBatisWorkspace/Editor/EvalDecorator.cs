using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Editor;

/// <summary>
/// 문장 편집기 계산 꾸밈(Order 100, design.md 9장 R3, TSK-03-02): 이번 값으로 빠지는 블록을 흐리게, if·when 머리 줄 여백에 켬(채운 점)·끔(빈 점),
/// 계산 못 한 식·조각 없음 같은 문제에 물결 밑줄. 여백 점을 누르면 그 블록을 덮어써 켬·끔을 고른다(우클릭 = 켬·끔·계산대로).
/// 계산은 문장의 <see cref="MyBatisStatementEval"/>(파라미터 창·펼친 SQL 과 같은 것)이 하고, 이 꾸밈은 그 결과를 그린다.
/// </summary>
[OrandaService(typeof(IMyBatisEditorDecorator))]
public sealed class EvalDecorator : IMyBatisEditorDecorator
{
    private readonly Func<MyBatisEvalHub?> _hub;

    /// <summary>셸(DI)이 쓰는 생성자. 계산 상태 묶음은 처음 붙일 때 꺼낸다.</summary>
    public EvalDecorator(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _hub = () => services.GetService(typeof(MyBatisEvalHub)) as MyBatisEvalHub;
    }

    /// <summary>시험용: 계산 상태 묶음을 직접 넣는다.</summary>
    internal EvalDecorator(MyBatisEvalHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        _hub = () => hub;
    }

    public int Order => 100;

    public IDisposable Attach(MyBatisEditorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_hub() is not { } hub || context.Session.StatementIdOf(context.Tab) is not { Length: > 0 } id)
        {
            return EmptyDisposable.Instance;
        }

        var eval = hub.For(context.Session, id);
        var attachment = new EvalDecoration(eval, context.Tab, context.Editor);
        _ = eval.EnsureLoadedAsync();
        return attachment;
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
