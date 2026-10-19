using System.Runtime.CompilerServices;
using Oranda.App.Features.Localization;

namespace Oranda.App.Tests.Features.Localization;

/// <summary>
/// 시험 프로세스의 「OS 언어」를 한국어로 고정한다. 기존 셸 시험이 한국어 글자를 단정하므로,
/// 설정 언어가 <c>system</c> 인 채로 영어 OS(CI)에서 돌아도 같은 결과가 나오게 한다.
/// </summary>
internal static class LocalizationTestSetup
{
#pragma warning disable CA2255 // 시험 어셈블리 전체에 필요한 한 번짜리 초기화
    [ModuleInitializer]
    internal static void Init() => UiLanguage.SystemLanguageProvider = () => "ko";
#pragma warning restore CA2255
}
