using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Shell;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Localization;

/// <summary>
/// TSK-10-02 다국어·접근성 — 명령 처리기. 처리기 클래스에 <c>[OrandaCommand]</c> 를 달면 셸이 자동 수집한다(docs/design/ui-contract.md).
/// 명령 ID 는 commands.md 7장 「소유 Task」 열을 따른다.
/// </summary>
public static class LocalizationCommands
{
    /// <summary>이 파일이 등록하는 명령 ID 전체(시험이 등록 누락을 잡는다).</summary>
    public static readonly string[] Ids = ["oranda.tools.language"];
}

/// <summary>
/// 도구 &gt; UI 언어. 언어 선택은 설정 창의 「언어」 항목이므로 설정 창을 연다(진입점: 설정 &gt; 언어).
/// 저장하면 메뉴·도구 창 제목이 재시작 없이 바뀐다.
/// </summary>
[OrandaCommand("oranda.tools.language", CommandCategory.Tools, Placements = ["menu:tools/language@12"])]
public sealed class LanguageCommandHandler(IShellUi ui) : ICommandHandler
{
    public Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ui.ShowSettings();
        return Task.CompletedTask;
    }
}
