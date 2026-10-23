using System.Reflection;
using Oranda.App.Features.Localization;
using Oranda.App.Shell;
using Xunit.Sdk;

namespace Oranda.App.Tests.TestSupport;

/// <summary>
/// <see cref="LiteralTranslator"/> 는 프로세스에 하나이고 마지막에 만든 셸의 글자 조회에 묶인다. 앞 시험의 셸이 늦게 받은
/// 설정 변경 알림(SettingsChanged → Dispatcher.Post(ApplyLanguage))이 그 조회를 영어로 바꾸면, 한국어 문구를 단정하는
/// 다음 시험이 간헐로 영어 문구를 본다. 이 속성을 단 시험은 앞뒤에서 새 한국어 조회로 다시 묶어 그 영향을 끊는다.
/// xUnit 2 는 어셈블리 수준의 이 속성을 돌리지 않으므로 시험 클래스에 직접 단다.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class ResetUiLanguageAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest) => Reset();

    public override void After(MethodInfo methodUnderTest) => Reset();

    private static void Reset() => LiteralTranslator.Install(new ShellText(language: "ko"));
}
