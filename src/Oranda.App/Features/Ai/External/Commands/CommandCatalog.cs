using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.External.Commands;

/// <summary><c>list_commands</c> 의 한 줄.</summary>
/// <param name="Id">정식 명령 id.</param>
/// <param name="Title">지금 화면 언어의 제목(메뉴·팔레트와 같은 글).</param>
/// <param name="Category">명령 범주.</param>
/// <param name="MenuPath">메뉴 막대 경로(「SQL &gt; 실행 &gt; 문장 실행」). 메뉴 막대에 직접 놓이지 않은 명령은 null.</param>
/// <param name="Grade">안전 등급.</param>
/// <param name="Enabled">지금 문맥에서 실행할 수 있나(활성 조건식).</param>
public sealed record CommandEntry(string Id, string Title, CommandCategory Category, string? MenuPath, CommandGrade Grade, bool Enabled);

/// <summary>
/// 레지스트리의 명령 목록을 밖의 에이전트에게 보일 모양으로 만든다(<c>list_commands</c>). UI 스레드에서 부른다(문맥 키가 UI 상태다).
/// 레지스트리 목록은 기능 게이트가 거른 것이라 꺼진 기능의 명령은 나오지 않는다.
/// </summary>
public static class CommandCatalog
{
    /// <summary>메뉴 경로 마디 사이 글.</summary>
    public const string PathSeparator = " > ";

    public static IReadOnlyList<CommandEntry> List(ICommandRegistry registry, IShellText text, IContextKeys keys, CommandCategory? category = null, CommandGrade? grade = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(keys);
        var entries = new List<CommandEntry>();
        foreach (var command in registry.Commands)
        {
            if (category is { } c && command.Category != c)
            {
                continue;
            }

            var g = CommandGradeTable.Grade(command.Id);
            if (grade is { } want && g != want)
            {
                continue;
            }

            entries.Add(new CommandEntry(command.Id, text.Title(command), command.Category, MenuPath(command, text), g, registry.CanExecute(command.Id, keys)));
        }

        return entries;
    }

    /// <summary>
    /// 첫 <c>menu:</c> 배치의 메뉴 막대 경로: 최상위 메뉴 제목(<c>Menu.&lt;id&gt;</c>) → 하위 메뉴 제목(<c>Menu.&lt;id&gt;.&lt;하위&gt;</c>) → 명령 제목.
    /// 오른쪽 클릭에만 놓여 결과·데이터베이스 메뉴에 거울처럼 비치는 명령, 최상위 메뉴가 아닌 곳의 배치는 null.
    /// </summary>
    public static string? MenuPath(CommandDefinition command, IShellText text)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(text);
        foreach (var raw in command.Placements)
        {
            if (!Placement.TryParse(raw, out var p) || p.Surface != "menu" || !MenuModelBuilder.TopMenus.Contains(p.Path[0], StringComparer.Ordinal))
            {
                continue;
            }

            var parts = new List<string>();
            var key = "Menu." + p.Path[0];
            parts.Add(text.Lookup(key));
            for (var i = 1; i < p.Path.Count - 1; i++)
            {
                key += "." + p.Path[i];
                parts.Add(text.Lookup(key));
            }

            parts.Add(text.Title(command));
            return string.Join(PathSeparator, parts);
        }

        return null;
    }

    /// <summary>범주 → 계약 글(enum 이름의 첫 글자만 소문자: <c>file</c>·<c>sql</c>·<c>result</c>…).</summary>
    public static string CategoryName(CommandCategory category)
    {
        var name = category.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>계약 글 → 범주(대소문자 무시). 모르는 글이면 null.</summary>
    public static CommandCategory? ParseCategory(string? text) =>
        Enum.TryParse<CommandCategory>(text?.Trim(), ignoreCase: true, out var c) && Enum.IsDefined(c) && !int.TryParse(text, out _) ? c : null;

    /// <summary>범주 계약 글 전체(인자 오류 안내용).</summary>
    public static IReadOnlyList<string> CategoryNames { get; } = [.. Enum.GetValues<CommandCategory>().Select(CategoryName)];
}
