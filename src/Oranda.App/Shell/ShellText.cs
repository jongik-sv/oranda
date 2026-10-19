using System;
using System.Collections.Generic;
using Oranda.Core.Shell;

namespace Oranda.App.Shell;

/// <summary>
/// 리소스 키(<c>Cmd.&lt;ID&gt;</c>, <c>Panel.&lt;ID&gt;</c>) → 화면 글자. 한/영 리소스 파일은 TSK-10-02 가 만든다.
/// 그 전까지 셸이 소유한 문자열만 한국어 표를 갖고, 나머지는 키에서 사람이 읽을 수 있는 이름을 만들어 쓴다
/// (명령 ID 마지막 마디를 띄어 쓴 것).
/// </summary>
public interface IShellText
{
    /// <summary>리소스 키의 글자. 모르면 <paramref name="key"/> 에서 만든 대체 글자.</summary>
    string Lookup(string key);

    string Title(CommandDefinition command);

    string Title(ToolPanelDefinition panel);

    string CategoryName(CommandCategory category);
}

/// <inheritdoc />
public sealed class ShellText : IShellText
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["Cmd.oranda.file.close"] = "탭 닫기",
        ["Cmd.oranda.file.closeAll"] = "모든 탭 닫기",
        ["Cmd.oranda.file.exit"] = "끝내기",
        ["Cmd.oranda.view.commandPalette"] = "명령 팔레트",
        ["Cmd.oranda.view.resetLayout"] = "레이아웃 초기화",
        ["Cmd.oranda.view.themeLight"] = "밝은 테마",
        ["Cmd.oranda.view.themeDark"] = "어두운 테마",
        ["Cmd.oranda.view.themeSystem"] = "OS 테마 따르기",
        ["Cmd.oranda.view.zoomIn"] = "글자 크게",
        ["Cmd.oranda.view.zoomOut"] = "글자 작게",
        ["Cmd.oranda.view.zoomReset"] = "글자 크기 원래대로",
        ["Cmd.oranda.tools.settings"] = "설정",
        ["Cmd.oranda.window.nextDocument"] = "다음 탭",
        ["Cmd.oranda.window.previousDocument"] = "이전 탭",
        ["Cmd.oranda.window.splitRight"] = "오른쪽으로 분할",
        ["Cmd.oranda.window.splitDown"] = "아래로 분할",
        ["Cmd.oranda.window.focusDocument"] = "문서로 포커스",
        ["Cmd.oranda.help.about"] = "Oranda 정보",
        ["Panel.oranda.panel.sqlEditor"] = "SQL 편집기",
        ["Panel.oranda.panel.plsqlEditor"] = "PL/SQL 편집기",
        ["Panel.oranda.panel.objectDetail"] = "객체 상세",
        ["Panel.oranda.panel.lobEditor"] = "LOB 편집기",
        ["Panel.oranda.panel.sessionMonitor"] = "세션 모니터",
        ["Panel.oranda.panel.sqlMonitor"] = "SQL 모니터",
        ["Panel.oranda.panel.dbInfo"] = "DB 정보",
        ["Panel.oranda.panel.objectSearch"] = "객체 검색",
        ["Panel.oranda.panel.ddlExtract"] = "DDL 일괄 추출",
        ["Panel.oranda.panel.formatterProfiles"] = "포매터 프로필",
        ["Panel.oranda.panel.schemaBrowser"] = "스키마 브라우저",
        ["Panel.oranda.panel.scripts"] = "스크립트",
        ["Panel.oranda.panel.output"] = "DBMS 출력",
        ["Panel.oranda.panel.messages"] = "메시지",
        ["Panel.oranda.panel.plan"] = "실행 계획",
        ["Panel.oranda.panel.history"] = "실행 이력",
        ["Panel.oranda.panel.compileErrors"] = "컴파일 오류",
        ["Panel.oranda.panel.outline"] = "문서 개요",
        ["Panel.oranda.panel.templates"] = "템플릿",
        ["Panel.oranda.panel.rowView"] = "행 보기",
        ["Panel.oranda.panel.extensionTest"] = "확장 시험",
        ["Menu.file"] = "파일",
        ["Menu.edit"] = "편집",
        ["Menu.view"] = "보기",
        ["Menu.sql"] = "SQL",
        ["Menu.result"] = "결과",
        ["Menu.database"] = "데이터베이스",
        ["Menu.monitor"] = "모니터",
        ["Menu.tools"] = "도구",
        ["Menu.window"] = "창",
        ["Menu.help"] = "도움말",
        ["Menu.view.panels"] = "도구 창",
        ["Menu.view.theme"] = "테마",
        ["Shell.palette.watermark"] = "명령 이름을 입력하세요",
        ["Shell.palette.none"] = "일치하는 명령이 없다",
        ["Shell.palette.disabled"] = "지금은 실행할 수 없다",
        ["Shell.palette.automation"] = "명령 팔레트",
        ["Shell.settings.title"] = "설정",
        ["Shell.settings.theme"] = "테마",
        ["Shell.settings.theme.system"] = "OS 설정 따르기",
        ["Shell.settings.theme.light"] = "밝게",
        ["Shell.settings.theme.dark"] = "어둡게",
        ["Shell.settings.editorFont"] = "에디터 글꼴",
        ["Shell.settings.editorFontDefault"] = "(기본 고정폭 글꼴)",
        ["Shell.settings.editorFontSize"] = "에디터 글자 크기",
        ["Shell.settings.tabSize"] = "탭 너비",
        ["Shell.settings.readOnlyNotice"] = "새 버전 Oranda 가 만든 설정 파일이라 저장하지 않는다.",
        ["Shell.settings.save"] = "저장",
        ["Shell.settings.close"] = "닫기",
        ["Shell.about.title"] = "Oranda 정보",
        ["Shell.window.title"] = "Oranda",
        ["Shell.toolbar.more"] = "…",
    };

    private static readonly Dictionary<CommandCategory, string> Categories = new()
    {
        [CommandCategory.File] = "파일",
        [CommandCategory.Connection] = "접속",
        [CommandCategory.Edit] = "편집",
        [CommandCategory.Navigate] = "탐색",
        [CommandCategory.Format] = "포맷",
        [CommandCategory.Sql] = "SQL",
        [CommandCategory.Result] = "결과",
        [CommandCategory.View] = "보기",
        [CommandCategory.Database] = "데이터베이스",
        [CommandCategory.Monitor] = "모니터",
        [CommandCategory.Tools] = "도구",
        [CommandCategory.Window] = "창",
        [CommandCategory.Help] = "도움말",
    };

    private readonly Func<string, string?>? _resources;

    /// <param name="resources">나중에 붙는 다국어 리소스 조회(TSK-10-02). 없거나 null 을 돌려주면 내장 표를 쓴다.</param>
    public ShellText(Func<string, string?>? resources = null) => _resources = resources;

    public string Lookup(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return _resources?.Invoke(key) is { } found ? found
            : Table.TryGetValue(key, out var text) ? text
            : Fallback(key);
    }

    public string Title(CommandDefinition command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Lookup(command.Title);
    }

    public string Title(ToolPanelDefinition panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        return Lookup(panel.Title);
    }

    public string CategoryName(CommandCategory category) =>
        Categories.TryGetValue(category, out var name) ? name : category.ToString();

    /// <summary><c>Cmd.oranda.exec.runStatement</c> → <c>exec run Statement</c> 처럼 읽을 수 있는 이름.</summary>
    internal static string Fallback(string key)
    {
        var id = key;
        foreach (var prefix in new[] { "Cmd.", "Panel.", "Menu." })
        {
            if (id.StartsWith(prefix, StringComparison.Ordinal))
            {
                id = id[prefix.Length..];
                break;
            }
        }

        if (id.StartsWith("oranda.", StringComparison.Ordinal))
        {
            id = id["oranda.".Length..];
        }

        var parts = id.Split('.');
        var last = parts[^1];
        var words = new System.Text.StringBuilder();
        foreach (var ch in last)
        {
            if (char.IsUpper(ch) && words.Length > 0)
            {
                words.Append(' ');
            }

            words.Append(char.ToLowerInvariant(ch));
        }

        return parts.Length > 1 ? $"{parts[^2]} {words}" : words.ToString();
    }
}
