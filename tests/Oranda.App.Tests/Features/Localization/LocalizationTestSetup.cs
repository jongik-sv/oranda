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

/// <summary>
/// 화면 글자의 영어 번역을 단정하는 시험 묶음. <see cref="LiteralTranslator"/> 는 프로세스에 하나이고 셸이 만들어질 때마다
/// 그 셸의 언어(기본 한국어)로 다시 묶이므로, 다른 시험이 병렬로 셸을 만들면 영어로 바꾼 창이 한국어로 되돌아간다.
/// 병렬 시험이 모두 끝난 뒤 이 묶음만 따로 돈다.
/// </summary>
[CollectionDefinition("UiLanguage", DisableParallelization = true)]
public sealed class UiLanguageGroup
{
}
