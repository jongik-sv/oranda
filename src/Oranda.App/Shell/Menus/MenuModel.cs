using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Oranda.Core.Features;
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

    /// <summary>접근 키(<c>_</c> 앞 글자, Alt+글자)를 넣은 머리글(최상위 메뉴만). 없으면 <see cref="Title"/> 을 그대로 쓴다.</summary>
    public string? AccessHeader { get; init; }

    public IReadOnlyList<MenuNode> Children { get; init; } = [];

    /// <summary>하위 메뉴: 제목 키(<c>Menu.file.recentFiles</c> 등). 이 키로 등록된 <see cref="IMenuItemsSource"/> 가 있으면 열 때 앞쪽 줄을 채운다.</summary>
    public string? Key { get; init; }

    /// <summary>명령의 <c>When</c>(주 메뉴 숨김, <see cref="MenuVisibility"/>).</summary>
    public string? When { get; init; }

    /// <summary>명령의 <c>Enablement</c>(회색 이유 툴팁, <see cref="MenuVisibility"/>).</summary>
    public string? Enablement { get; init; }

    /// <summary>최상위 메뉴의 항목이 모두 숨었을 때 보일 회색 안내 한 줄.</summary>
    public string? EmptyText { get; init; }

    /// <summary>최상위 메뉴: 이유 문구 키(<see cref="ContextKeyReasons.PhrasePrefix"/> 로 시작) → 지금 언어 글자. 없으면 null.</summary>
    public Func<string, string?>? ReasonText { get; init; }

    public static MenuNode Separator(int order) => new(MenuNodeKind.Separator, string.Empty, order);
}

/// <summary>
/// 하위 메뉴를 열 때 채우는 줄 하나(최근 파일 등). <paramref name="Detail"/> 은 이름 뒤 회색 글(줄인 경로), <paramref name="ToolTip"/> 은 전체 글.
/// <paramref name="Run"/> 이 없으면 회색 안내 줄(「최근 파일이 없습니다」 등)이다.
/// </summary>
public sealed record MenuSourceItem(string Title, string? Detail = null, string? ToolTip = null, Func<Task>? Run = null);

/// <summary>하위 메뉴 하나의 바뀌는 줄 공급자. 메뉴를 열 때마다 불러 그 하위 메뉴의 명령 줄 앞에 넣는다(UI 스레드).</summary>
public interface IMenuItemsSource
{
    IReadOnlyList<MenuSourceItem> Items();
}

/// <summary>
/// 하위 메뉴 키(<see cref="MenuNode.Key"/>) → 줄 공급자. 메뉴를 열 때 찾으므로 메뉴를 지은 뒤에 등록해도 된다.
/// 하위 메뉴 자체는 그 안에 놓인 명령(「목록 지우기」 등)의 자리로 생긴다: 공급자는 줄만 더하고 메뉴 짓기 규칙은 그대로다.
/// </summary>
public static class MenuItemSources
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IMenuItemsSource> Sources = new(StringComparer.Ordinal);

    /// <summary>공급자를 건다. 돌려준 것을 Dispose 하면 그 공급자를 뗀다(그사이 다른 공급자로 바뀌었으면 그대로 둔다).</summary>
    public static IDisposable Register(string key, IMenuItemsSource source)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(source);
        Sources[key] = source;
        return new Unregister(key, source);
    }

    public static IMenuItemsSource? Find(string? key) => key is not null && Sources.TryGetValue(key, out var source) ? source : null;

    private sealed class Unregister(string key, IMenuItemsSource source) : IDisposable
    {
        public void Dispose() => Sources.TryRemove(new KeyValuePair<string, IMenuItemsSource>(key, source));
    }
}

/// <summary>레지스트리의 명령 목록으로 메뉴 막대·툴바 모델을 만든다. 메뉴 항목을 셸 파일에 직접 쓰지 않는다.</summary>
public static class MenuModelBuilder
{
    /// <summary>최상위 메뉴 11개와 순서(ui-shell.md 1.1).</summary>
    public static readonly IReadOnlyList<string> TopMenus =
        ["file", "edit", "view", "sql", "result", "database", "monitor", "tuning", "tools", "window", "help"];

    public const string PanelsSubmenuId = "view.panels";

    /// <summary>
    /// 기능에 매인 최상위 메뉴(TRD 10.7.1): 이 기능이 하나도 켜져 있지 않으면 공통 명령이 남아 있어도 메뉴를 그리지 않는다.
    /// 튜닝 메뉴에는 공통인 예상·실제 계획(D-004)이 같이 놓여 있어(SQL 메뉴에도 있다) 비지 않으므로, 조회 전용만 고르면 「튜닝」 이 남는 것을 막는다.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> TopMenuFeatures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["monitor"] = [FeatureIds.SessionMonitor, FeatureIds.LockMonitor, FeatureIds.SqlMonitor],
        ["tuning"] = [.. FeatureCatalog.Features.Where(f => f.Group == FeatureGroup.Tuning).Select(f => f.Id)],
    };

    /// <summary>오른쪽 클릭 표면 → 그 명령을 함께 보일 최상위 메뉴.</summary>
    private static readonly (string Surface, string Top)[] ContextMenuMirrors = [("grid", "result"), ("schema", "database")];

    public static IReadOnlyList<MenuNode> BuildMenuBar(
        IEnumerable<CommandDefinition> commands,
        IShellText text,
        Func<string, string?> shortcut,
        IEnumerable<ToolPanelDefinition> panels,
        Func<string, bool>? isFeatureEnabled = null)
    {
        var roots = TopMenus.ToDictionary(id => id, id => new List<(Placement P, MenuNode N)>(), StringComparer.Ordinal);
        var commandList = commands as IReadOnlyList<CommandDefinition> ?? [.. commands];
        foreach (var c in commandList)
        {
            foreach (var raw in c.Placements)
            {
                if (!Placement.TryParse(raw, out var p) || p.Surface != "menu")
                {
                    continue;
                }

                if (!roots.TryGetValue(p.Path[0], out var list))
                {
                    continue;
                }

                list.Add((p, new MenuNode(MenuNodeKind.Command, text.Title(c), p.Order)
                {
                    CommandId = c.Id,
                    Shortcut = shortcut(c.Id),
                    Icon = c.Icon,
                    When = c.When,
                    Enablement = c.Enablement,
                }));
            }
        }

        // 오른쪽 클릭에만 놓인 명령도 메뉴 막대에서 찾을 수 있게 한다(ui-shell 1.1 의 결과·데이터베이스 메뉴).
        // 오른쪽 클릭의 하위 메뉴는 그대로 하위 메뉴로 싣는다(제목 키 Menu.context.<표면>.<하위 메뉴>, TRD 11.2).
        foreach (var (surface, top) in ContextMenuMirrors)
        {
            var context = BuildContext(commandList.Where(c => !c.Placements.Any(r => r.StartsWith("menu:", StringComparison.Ordinal))), text, shortcut, surface);
            var group = 0;
            var order = 0;
            foreach (var node in context)
            {
                if (node.Kind == MenuNodeKind.Separator)
                {
                    group++;
                    order = 0;
                    continue;
                }

                var p = new Placement("menu", [top, node.CommandId ?? node.Key ?? "item"], (group * Placement.GroupSize) + (++order));
                roots[top].Add((p, node with { Order = p.Order }));
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

            // 기능 게이트를 받으면(주 창) 놓인 명령이 하나도 없는 최상위 메뉴, 매인 기능이 모두 꺼진 최상위 메뉴는 그리지 않는다(TRD 10.7.1).
            // When 으로 잠시 숨는 항목만 있는 메뉴는 남기고 EmptyText 를 보인다. 게이트가 없으면 11개를 늘 그린다(기존 동작).
            if (isFeatureEnabled is not null
                && (children.Count == 0 || (TopMenuFeatures.TryGetValue(id, out var features) && !features.Any(isFeatureEnabled))))
            {
                continue;
            }

            bar.Add(new MenuNode(MenuNodeKind.Submenu, text.Lookup($"Menu.{id}"), 0)
            {
                Children = children,
                AccessHeader = text.TryLookup($"Menu.{id}.access"),
                EmptyText = text.TryLookup($"Menu.{id}.empty") ?? text.Lookup("Menu.empty"),
                ReasonText = text.TryLookup,
            });
        }

        return bar;
    }

    /// <summary>
    /// 툴바 모델. <c>toolbar:main/&lt;이름&gt;@n</c> 은 단추 하나, <c>toolbar:main/&lt;부모 이름&gt;/&lt;자식 이름&gt;@n</c> 은 그 단추의 ▾ 자식 메뉴 줄(나눔 단추).
    /// 자식이 하나라도 붙은 단추는 <see cref="MenuNode.Children"/> 을 채운 나눔 단추가 되고, 부모 단추가 없는 자식은 버린다.
    /// </summary>
    public static IReadOnlyList<MenuNode> BuildToolbar(IEnumerable<CommandDefinition> commands, IShellText text, Func<string, string?> shortcut)
    {
        var items = new List<(Placement P, MenuNode N)>();
        var children = new List<(Placement P, MenuNode N)>();
        foreach (var c in commands)
        {
            foreach (var raw in c.Placements)
            {
                if (Placement.TryParse(raw, out var p) && p is { Surface: "toolbar" } && p.Path[0] == "main")
                {
                    var node = new MenuNode(MenuNodeKind.Command, text.Title(c), p.Order)
                    {
                        CommandId = c.Id,
                        Shortcut = shortcut(c.Id),
                        Icon = c.Icon,
                        When = c.When, // 툴바도 명령의 When 을 따른다(TRD 11.3, MenuVisibility.Toolbar)
                        Enablement = c.Enablement,
                    };
                    (p.Path.Count >= 3 ? children : items).Add((p, node));
                }
            }
        }

        if (children.Count > 0)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var (p, node) = items[i];
                var own = children.Where(ch => p.Path.Count >= 2 && string.Equals(ch.P.Path[1], p.Path[1], StringComparison.Ordinal)).ToList();
                if (own.Count > 0)
                {
                    items[i] = (p, node with { Children = WithSeparators(own) });
                }
            }
        }

        return WithSeparators(items);
    }

    /// <summary>
    /// 오른쪽 클릭 메뉴(<c>context:&lt;surface&gt;/…</c> 배치). 표면은 <c>editor</c>·<c>grid</c>·<c>schema</c>·<c>monitor</c> 등.
    /// <c>context:&lt;표면&gt;/&lt;묶음&gt;@n</c> 은 묶음(구분선 단위), <c>context:&lt;표면&gt;/&lt;묶음&gt;/&lt;하위 메뉴&gt;@n</c> 은 그 묶음 안의 하위 메뉴(TRD 11.2).
    /// 하위 메뉴 제목 키는 <c>Menu.context.&lt;표면&gt;.&lt;하위 메뉴&gt;</c>, 안쪽 순서·구분선 규칙은 메뉴 막대와 같다.
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
                        When = c.When, // 오른쪽 클릭 메뉴는 쓰지 않는다. 결과·데이터베이스 메뉴로 옮겨 실을 때 쓴다.
                        Enablement = c.Enablement,
                    }));
                }
            }
        }

        return ContextSections(items, text, $"Menu.context.{surface}");
    }

    /// <summary>오른쪽 클릭 메뉴에서 묶음 이름의 순서가 같을 때(그리드처럼 묶음마다 10 부터 센다) 쓰는 차례.</summary>
    private static readonly string[] ContextSectionOrder = ["run", "refresh", "copy", "view", "filter", "columns", "rows", "navigate", "edit", "insert", "favorite", "detail", "export"];

    /// <summary>
    /// 오른쪽 클릭 배치는 두 관례가 섞여 있다. <c>context:grid/copy@10</c> 처럼 둘째 마디를 여러 명령이 같이 쓰거나 셋째 마디(하위 메뉴)가 있으면 그것이 묶음 이름이고,
    /// 편집기처럼 명령마다 다르면 순서 번호로 묶는다(100 단위, 순서가 모두 100 미만이면 10 단위).
    /// </summary>
    private static List<MenuNode> ContextSections(List<(Placement P, MenuNode N)> items, IShellText text, string key)
    {
        var keyOf = items.Select(i => i.P.Path.Count >= 2 ? i.P.Path[1] : string.Empty).ToList();
        var named = items.Any(i => i.P.Path.Count >= 3) || keyOf.GroupBy(k => k, StringComparer.Ordinal).Any(g => g.Count() > 1);
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

            // 셋째 마디가 같은 항목은 하위 메뉴 하나로 모은다. 하위 메뉴 자리 = 안에 든 항목의 가장 작은 순서.
            var level = new List<(int Order, MenuNode N)>();
            foreach (var group in section.Select(x => x.Item).GroupBy(i => named && i.P.Path.Count >= 3 ? i.P.Path[2] : null))
            {
                if (group.Key is null)
                {
                    level.AddRange(group.Select(i => (i.P.Order, i.N)));
                    continue;
                }

                var subKey = key + "." + group.Key;
                var order = group.Min(i => i.P.Order);
                level.Add((order, new MenuNode(MenuNodeKind.Submenu, text.Lookup(subKey), order)
                {
                    Children = WithSeparators([.. group]),
                    Key = subKey,
                }));
            }

            result.AddRange(level.OrderBy(x => x.Order).ThenBy(x => x.N.Title, StringComparer.Ordinal).Select(x => x.N));
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
                Key = subKey,
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
