using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Menus;

/// <summary>명령 <c>Placements</c> 한 줄(commands.md 2장): <c>menu:sql/run@10</c>, <c>toolbar:main/run@10</c>, <c>context:grid/copy@30</c>.</summary>
/// <param name="Surface"><c>menu</c> | <c>toolbar</c> | <c>context</c>.</param>
/// <param name="Path">표면 안 경로. 메뉴는 <c>[최상위 메뉴 ID, 하위 메뉴…]</c>, 툴바·오른쪽 클릭은 <c>[묶음 ID]</c>.</param>
/// <param name="Order"><c>@n</c>. 없으면 <see cref="DefaultOrder"/>.</param>
public sealed record Placement(string Surface, IReadOnlyList<string> Path, int Order)
{
    public const int DefaultOrder = 1000;

    /// <summary>같은 묶음 크기: 순서 값을 이 수로 나눈 몫이 다르면 묶음 사이 구분선이 들어간다.</summary>
    public const int GroupSize = 100;

    public int Group => Order / GroupSize;

    public static bool TryParse(string text, out Placement placement)
    {
        placement = null!;
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return false;
        }

        var surface = text[..colon];
        var rest = text[(colon + 1)..];
        var order = DefaultOrder;
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            if (!int.TryParse(rest[(at + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out order))
            {
                return false;
            }

            rest = rest[..at];
        }

        var parts = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || surface is not ("menu" or "toolbar" or "context"))
        {
            return false;
        }

        placement = new Placement(surface, parts, order);
        return true;
    }
}

public enum MenuNodeKind
{
    Command,
    Submenu,
    Separator,

    /// <summary>보기 &gt; 도구 창 목록의 한 줄(도구 창 보이기/숨기기).</summary>
    PanelToggle,
}

/// <summary>메뉴·툴바를 그리기 전의 순수 모델(Avalonia 를 모른다).</summary>
public sealed record MenuNode(MenuNodeKind Kind, string Title, int Order)
{
    public string? CommandId { get; init; }

    public string? PanelId { get; init; }

    public string? Shortcut { get; init; }

    public string? Icon { get; init; }

    public IReadOnlyList<MenuNode> Children { get; init; } = [];

    public static MenuNode Separator(int order) => new(MenuNodeKind.Separator, string.Empty, order);
}

/// <summary>레지스트리의 명령 목록으로 메뉴 막대·툴바 모델을 만든다. 메뉴 항목을 셸 파일에 직접 쓰지 않는다.</summary>
public static class MenuModelBuilder
{
    /// <summary>최상위 메뉴 10개와 순서(ui-shell.md 1.1).</summary>
    public static readonly IReadOnlyList<string> TopMenus =
        ["file", "edit", "view", "sql", "result", "database", "monitor", "tools", "window", "help"];

    public const string PanelsSubmenuId = "view.panels";

    public static IReadOnlyList<MenuNode> BuildMenuBar(
        IEnumerable<CommandDefinition> commands,
        IShellText text,
        Func<string, string?> shortcut,
        IEnumerable<ToolPanelDefinition> panels)
    {
        var roots = TopMenus.ToDictionary(id => id, id => new List<(Placement P, MenuNode N)>(), StringComparer.Ordinal);
        foreach (var c in commands)
        {
            foreach (var raw in c.Placements)
            {
                if (!Placement.TryParse(raw, out var p) || p.Surface != "menu" || !roots.TryGetValue(p.Path[0], out var list))
                {
                    continue;
                }

                list.Add((p, new MenuNode(MenuNodeKind.Command, text.Title(c), p.Order)
                {
                    CommandId = c.Id,
                    Shortcut = shortcut(c.Id),
                    Icon = c.Icon,
                }));
            }
        }

        var bar = new List<MenuNode>();
        foreach (var id in TopMenus)
        {
            var children = BuildLevel(roots[id], depth: 1, text, $"Menu.{id}");
            if (id == "view")
            {
                children = [.. children, .. PanelToggles(panels, text)];
            }

            bar.Add(new MenuNode(MenuNodeKind.Submenu, text.Lookup($"Menu.{id}"), 0) { Children = children });
        }

        return bar;
    }

    public static IReadOnlyList<MenuNode> BuildToolbar(IEnumerable<CommandDefinition> commands, IShellText text, Func<string, string?> shortcut)
    {
        var items = new List<(Placement P, MenuNode N)>();
        foreach (var c in commands)
        {
            foreach (var raw in c.Placements)
            {
                if (Placement.TryParse(raw, out var p) && p is { Surface: "toolbar" } && p.Path[0] == "main")
                {
                    items.Add((p, new MenuNode(MenuNodeKind.Command, text.Title(c), p.Order)
                    {
                        CommandId = c.Id,
                        Shortcut = shortcut(c.Id),
                        Icon = c.Icon,
                    }));
                }
            }
        }

        return WithSeparators(items);
    }

    /// <summary>
    /// 오른쪽 클릭 메뉴(<c>context:&lt;surface&gt;/…</c> 배치). 표면은 <c>editor</c>·<c>grid</c>·<c>schema</c>·<c>monitor</c> 등.
    /// 하위 메뉴·구분선 규칙은 메뉴 막대와 같다.
    /// </summary>
    public static IReadOnlyList<MenuNode> BuildContext(IEnumerable<CommandDefinition> commands, IShellText text, Func<string, string?> shortcut, string surface)
    {
        var items = new List<(Placement P, MenuNode N)>();
        foreach (var c in commands)
        {
            foreach (var raw in c.Placements)
            {
                if (Placement.TryParse(raw, out var p) && p.Surface == "context" && p.Path[0] == surface)
                {
                    items.Add((p, new MenuNode(MenuNodeKind.Command, text.Title(c), p.Order)
                    {
                        CommandId = c.Id,
                        Shortcut = shortcut(c.Id),
                        Icon = c.Icon,
                    }));
                }
            }
        }

        return ContextSections(items);
    }

    /// <summary>오른쪽 클릭 메뉴에서 묶음 이름의 순서가 같을 때(그리드처럼 묶음마다 10 부터 센다) 쓰는 차례.</summary>
    private static readonly string[] ContextSectionOrder = ["run", "refresh", "copy", "view", "columns", "edit", "insert", "favorite", "detail", "export"];

    /// <summary>
    /// 오른쪽 클릭 배치는 두 관례가 섞여 있다. <c>context:grid/copy@10</c> 처럼 둘째 마디를 여러 명령이 같이 쓰면 그것이 묶음 이름이고,
    /// 편집기처럼 명령마다 다르면 순서 번호로 묶는다(100 단위, 순서가 모두 100 미만이면 10 단위).
    /// </summary>
    private static List<MenuNode> ContextSections(List<(Placement P, MenuNode N)> items)
    {
        var keyOf = items.Select(i => i.P.Path.Count >= 2 ? i.P.Path[1] : string.Empty).ToList();
        var named = keyOf.GroupBy(k => k, StringComparer.Ordinal).Any(g => g.Count() > 1);
        var step = items.All(i => i.P.Order < Placement.GroupSize) ? 10 : Placement.GroupSize;
        var sections = items
            .Select((item, i) => (Item: item, Section: named ? keyOf[i] : (item.P.Order / step).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)))
            .GroupBy(x => x.Section, StringComparer.Ordinal)
            .OrderBy(g => g.Min(x => x.Item.P.Order))
            .ThenBy(g => Array.IndexOf(ContextSectionOrder, g.Key) is var r && r >= 0 ? r : int.MaxValue)
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        var result = new List<MenuNode>();
        foreach (var section in sections)
        {
            if (result.Count > 0)
            {
                result.Add(MenuNode.Separator(0));
            }

            result.AddRange(section.OrderBy(x => x.Item.P.Order).ThenBy(x => x.Item.N.Title, StringComparer.Ordinal).Select(x => x.Item.N));
        }

        return result;
    }

    private static IReadOnlyList<MenuNode> PanelToggles(IEnumerable<ToolPanelDefinition> panels, IShellText text)
    {
        var nodes = panels
            .Where(p => p.Kind == ToolPanelKind.Tool)
            .OrderBy(p => p.DefaultLocation)
            .ThenBy(p => p.Order)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new MenuNode(MenuNodeKind.PanelToggle, text.Title(p), p.Order) { PanelId = p.Id })
            .ToList();
        if (nodes.Count == 0)
        {
            return [];
        }

        return
        [
            MenuNode.Separator(0),
            new MenuNode(MenuNodeKind.Submenu, text.Lookup("Menu.view.panels"), 0) { Children = nodes },
        ];
    }

    /// <summary>
    /// 경로 <c>[메뉴, (하위 메뉴…), 항목 키]</c> 에서 <paramref name="depth"/> 번째 마디부터를 한 단계로 만든다.
    /// 마지막 마디는 항목 키이고 그 앞 마디는 하위 메뉴 이름이다.
    /// </summary>
    private static List<MenuNode> BuildLevel(List<(Placement P, MenuNode N)> entries, int depth, IShellText text, string key)
    {
        var level = new List<(Placement P, MenuNode N)>();
        foreach (var group in entries.GroupBy(e => e.P.Path.Count - depth <= 1 ? null : e.P.Path[depth]))
        {
            if (group.Key is null)
            {
                level.AddRange(group);
                continue;
            }

            var subKey = key + "." + group.Key;
            var order = group.Min(g => g.P.Order);
            var sub = new MenuNode(MenuNodeKind.Submenu, text.Lookup(subKey), order)
            {
                Children = BuildLevel([.. group], depth + 1, text, subKey),
            };
            level.Add((group.First().P with { Order = order }, sub));
        }

        return WithSeparators(level);
    }

    private static List<MenuNode> WithSeparators(List<(Placement P, MenuNode N)> entries)
    {
        var result = new List<MenuNode>();
        int? lastGroup = null;
        foreach (var (p, n) in entries.OrderBy(e => e.P.Order).ThenBy(e => e.N.Title, StringComparer.Ordinal))
        {
            if (lastGroup is { } g && g != p.Group)
            {
                result.Add(MenuNode.Separator(p.Order));
            }

            lastGroup = p.Group;
            result.Add(n);
        }

        return result;
    }
}
