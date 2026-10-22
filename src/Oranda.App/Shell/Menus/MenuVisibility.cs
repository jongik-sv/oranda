using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Menus;

/// <summary>주 메뉴 한 마디의 지금 상태. <see cref="Children"/> 은 <see cref="MenuNode.Children"/> 과 같은 차례.</summary>
/// <param name="Visible">보이는지(구분선 정리 포함).</param>
/// <param name="Reason">회색(활성 조건 거짓)인 명령의 이유 툴팁. 보이고 쓸 수 있거나 문구가 없으면 null.</param>
/// <param name="ShowEmpty">최상위 메뉴의 모든 항목이 숨었을 때 회색 안내 한 줄(<see cref="MenuNode.EmptyText"/>)을 보이는지.</param>
public sealed record MenuState(bool Visible, string? Reason, IReadOnlyList<MenuState> Children, bool ShowEmpty = false);

/// <summary>
/// 주 메뉴 막대의 숨김·회색 규칙(TSK-13-36, commands.md 2.4).
/// <list type="bullet">
/// <item>명령의 <c>When</c>(특정 탭·패널에서만 쓰는 명령)이 지금 맞지 않으면 숨긴다. 겹치거나 맨 앞·끝에 남는 구분선, 빈 하위 메뉴도 숨긴다.</item>
/// <item><c>When</c> 은 맞지만 <c>Enablement</c> 가 거짓이면 회색으로 두고 이유를 툴팁으로(<see cref="ContextKeyReasons"/>).</item>
/// </list>
/// 오른쪽 클릭 메뉴·툴바·명령 팔레트는 이 규칙을 쓰지 않는다.
/// </summary>
public static class MenuVisibility
{
    /// <summary>
    /// 포커스 키 → 주 메뉴에서 그 명령을 보일 탭·패널 조건(상태 키 식, null = 언제나).
    /// <c>When</c> 은 키맵의 기본 키 범위라 「마지막 포커스 자리」로 계산하면 시작 직후(자리 없음)에 편집 메뉴가 통째로 숨고,
    /// 스키마 트리를 누른 뒤 SQL 메뉴의 실행 명령이 숨는다. 메뉴 명령은 보이는 문서에 대고 실행되므로(MenuControls.AlignEditorTarget)
    /// 포커스 대신 「그 탭이 열려 있음」으로 본다. 스키마 브라우저는 늘 있는 도구 창이라 숨기지 않는다.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> TabScopes = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["sqlEditorFocus"] = "sqlDocActive",
        ["sqlDocFocus"] = "sqlDocActive",
        ["gridFocus"] = "sqlDocActive",
        ["editorFocus"] = "sqlDocActive || plsqlDocActive || plsqlEditorFocus",
        ["plsqlEditorFocus"] = "plsqlDocActive || plsqlEditorFocus",
        ["monitorFocus"] = "monitorSessionOpen || monitorSqlOpen",
        ["schemaFocus"] = null,
    };

    /// <summary>주 메뉴에서 이 <c>When</c> 의 명령을 보일지. 빈 식·읽을 수 없는 식은 보인다.</summary>
    public static bool InScope(string? when, IContextKeys keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return ContextKeyReasons.Evaluate(when, name => TabScopes.TryGetValue(name, out var scope)
            ? scope is null || ContextKeyReasons.Evaluate(scope, keys.Evaluate)
            : keys.Evaluate(name));
    }

    /// <summary>회색 명령의 이유 툴팁. 활성이면 null.</summary>
    public static string? Reason(string? enablement, IContextKeys keys, Func<string, string?> phrase)
    {
        ArgumentNullException.ThrowIfNull(keys);
        return ContextKeyReasons.Explain(enablement, name => keys.Evaluate(name), phrase);
    }

    /// <summary>최상위 메뉴 하나(<see cref="MenuNodeKind.Submenu"/>)의 지금 상태. 최상위 메뉴 자체는 늘 보인다.</summary>
    public static MenuState Evaluate(MenuNode top, IContextKeys keys)
    {
        ArgumentNullException.ThrowIfNull(top);
        ArgumentNullException.ThrowIfNull(keys);
        var phrase = top.ReasonText ?? (_ => null);
        var children = Level(top.Children, keys, phrase);
        return new MenuState(true, null, children, ShowEmpty: !children.Any(c => c.Visible));
    }

    private static List<MenuState> Level(IReadOnlyList<MenuNode> nodes, IContextKeys keys, Func<string, string?> phrase)
    {
        var states = new List<MenuState>(nodes.Count);
        foreach (var n in nodes)
        {
            states.Add(n.Kind switch
            {
                MenuNodeKind.Command when InScope(n.When, keys) => new MenuState(true, Reason(n.Enablement, keys, phrase), []),
                MenuNodeKind.Command => new MenuState(false, null, []),
                MenuNodeKind.Submenu => Sub(Level(n.Children, keys, phrase)),
                MenuNodeKind.Separator => new MenuState(false, null, []), // 아래에서 정한다
                _ => new MenuState(true, null, []),
            });
        }

        // 구분선은 앞뒤에 보이는 항목이 있고, 바로 앞에 보이는 구분선이 없을 때만 보인다.
        var seenItem = false;
        int? pending = null;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind == MenuNodeKind.Separator)
            {
                if (seenItem && pending is null)
                {
                    pending = i;
                }

                continue;
            }

            if (!states[i].Visible)
            {
                continue;
            }

            if (pending is { } p)
            {
                states[p] = states[p] with { Visible = true };
                pending = null;
            }

            seenItem = true;
        }

        return states;
    }

    private static MenuState Sub(List<MenuState> children) => new(children.Any(c => c.Visible), null, children);
}

/// <summary>
/// 주 메뉴 계산용 문맥 키: 앱이 따로 켜지 않는 <c>plsqlDocActive</c> 를 보이는 문서가 PL/SQL 편집기인지로 채운다
/// (그 밖의 키는 그대로). 실행 가능 여부(<see cref="ICommandRegistry.CanExecute"/>)에는 쓰지 않는다.
/// </summary>
internal sealed class MenuScopeKeys(IContextKeys inner, Func<bool> plsqlDocActive) : IContextKeys
{
    public bool Evaluate(string? expression) => ContextKeyReasons.Evaluate(expression, Get, malformed: false);

    private bool Get(string name) => inner.Evaluate(name) || (name == "plsqlDocActive" && plsqlDocActive());
}
