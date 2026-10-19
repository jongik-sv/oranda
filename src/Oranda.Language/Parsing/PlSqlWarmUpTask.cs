using Oranda.Core.Composition;

namespace Oranda.Language.Parsing;

/// <summary>
/// 앱 유휴 시간에 ANTLR 파서를 데운다(TRD 4.5·8, D-006: 시작 경로 밖). 첫 파싱의 약 2 s(ATN 역직렬화·JIT·DFA 캐시)를
/// 사용자가 처음 입력하기 전에 백그라운드에서 치른다. 소유: TSK-04-03.
/// </summary>
[OrandaService(typeof(IStartupTask))]
internal sealed class PlSqlWarmUpTask : IStartupTask
{
    public StartupPhase Phase => StartupPhase.Idle;

    public Task RunAsync(CancellationToken ct) => Task.Run(() => PlSqlAnalyzer.WarmUp(ct), ct);
}
