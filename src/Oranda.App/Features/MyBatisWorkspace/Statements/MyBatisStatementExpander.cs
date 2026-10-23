using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Shell.Composition;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 문장을 값 없이 모두 켜서 펼친 SQL 로 바꾼다(「펼친 SQL 을 새 SQL 탭으로」). 계산기(<see cref="IMyBatisEvaluator"/>, 02-02)는 Language 형이라
/// 처리기 형이 시작 때 읽히지 않게 이 형 안에만 둔다(design.md 9장 R21). 이 형은 펼칠 때 처음 쓰인다.
/// </summary>
internal static class MyBatisStatementExpander
{
    /// <summary>
    /// 계산기를 얻는 길. 시험은 가짜 계산기를 돌려주는 함수를 넣는다. null 이면 앱 서비스 공급자에서
    /// <c>IEnumerable&lt;IMyBatisEvaluator&gt;</c> 의 마지막 구현을 쓰고, 그것도 없으면 null.
    /// </summary>
    internal static Func<IMyBatisEvaluator?>? EvaluatorSource { get; set; }

    /// <summary>계산기를 찾는다. 없으면 null(02-02 가 아직 등록되지 않은 빌드).</summary>
    internal static bool IsAvailable(IServiceProvider? services) => Find(services) is not null;

    /// <summary>
    /// 문장 <paramref name="statementId"/> 를 펼친 SQL. 계산기가 없거나 문장을 찾을 수 없으면 null.
    /// 값은 비우고 if·when 은 모두 켠다(지금 읽기 전용 펼침과 같은 모양, <see cref="MyBatisEvalOptions.AllOn"/>).
    /// </summary>
    internal static string? Expand(MyBatisWorkspaceSession session, string statementId, IServiceProvider? services)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Find(services) is not { } evaluator || session.ElementOf(statementId) is not { } element)
        {
            return null;
        }

        var values = new Dictionary<string, object?>();
        return evaluator.Evaluate(element, session.Fragments, values, null, new MyBatisEvalOptions(AllOn: true)).Sql;
    }

    private static IMyBatisEvaluator? Find(IServiceProvider? services)
    {
        if (EvaluatorSource is { } source)
        {
            return source();
        }

        if (services is null)
        {
            return null;
        }

        // 구현은 지연 어셈블리(Oranda.Language)에 있을 수 있고 목록형 해석은 그것을 깨우지 않으므로, 펼칠 때 한 번 훑는다.
        (services as ServiceContainer)?.ScanDeferred();
        return services.GetService(typeof(IEnumerable<IMyBatisEvaluator>)) is IEnumerable<IMyBatisEvaluator> found ? found.LastOrDefault() : null;
    }
}
