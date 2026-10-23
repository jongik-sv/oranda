using Oranda.App.Shell;
using Oranda.Core.Composition;

namespace Oranda.App.Features.MyBatisWorkspace.Conversion;

/// <summary>
/// 바꾸기 기능이 들어왔음을 셸에 알린다(<c>mybatisConvertReady</c>, design.md 4.3·위험 R24). 이 키가 거짓이면
/// 「MyBatis 작업 공간으로 바꾸기…」 메뉴·문서 탭 우클릭 항목은 보이되 누를 수 없다.
/// 생성자는 등록된 Shell 서비스만 받는다(시작 시험 제약).
/// </summary>
[OrandaService(typeof(IStartupTask))]
public sealed class MyBatisConvertReady(ContextKeyService keys) : IStartupTask
{
    internal const string ReadyKey = "mybatisConvertReady";

    public StartupPhase Phase => StartupPhase.AfterFirstFrame;

    public Task RunAsync(CancellationToken ct)
    {
        keys.Set(ReadyKey, true);
        return Task.CompletedTask;
    }
}
