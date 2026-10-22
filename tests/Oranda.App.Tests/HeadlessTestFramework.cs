using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("Oranda.App.Tests.HeadlessTestFramework", "Oranda.App.Tests")]

namespace Oranda.App.Tests;

/// <summary>
/// 시험이 하나라도 돌기 전에 헤드리스 앱 세션을 먼저 띄운다. 공유 앱(PerAssembly)의 첫 설정이 병렬로 시작한 다른 시험과 겹치면
/// 그 시험이 Dispatcher.UIThread 를 먼저 소유해 설정이 실패하고 이후 UI 시험이 모두 같은 예외로 무너진다(R6).
/// 모듈 초기화(ModuleInitializer)에서 기다리면 같은 모듈의 형식 로드와 교착할 수 있어 시험 프레임워크 생성 시점을 쓴다.
/// </summary>
public sealed class HeadlessTestFramework : XunitTestFramework
{
    public HeadlessTestFramework(IMessageSink messageSink)
        : base(messageSink) => HeadlessHost.WarmUp();
}
