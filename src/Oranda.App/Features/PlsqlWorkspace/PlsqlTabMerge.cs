using System;
using System.Collections.Generic;
using System.Linq;
using Oranda.App.Features.PlsqlEditor;

namespace Oranda.App.Features.PlsqlWorkspace;

/// <summary>
/// 패키지·타입의 명세 탭과 본문 탭을 한 탭(<see cref="PlsqlTabFile.Body"/>)으로 묶고 푼다(PKG1, D-067).
/// 예전 작업 공간 파일(1 판 JSON·Markdown)은 명세와 본문을 다른 탭으로 적었다: 읽을 때 같은 접속·소유자·이름의 짝을 하나로 묶는다.
/// Markdown 파일은 사람이 읽는 형식을 그대로 두려고 쓸 때 두 절로 푼다.
/// </summary>
public static class PlsqlTabMerge
{
    /// <summary>
    /// 같은 접속·소유자·이름의 명세 탭과 본문 탭을 앞에 나온 탭 자리에 하나로 묶는다. 파일에 묶인 탭·이미 묶인 탭·쌍이 없는 유형은 그대로 둔다.
    /// <paramref name="activeTab"/> 은 묶은 뒤의 차례로 옮기고, 보이던 탭이 본문 탭이었으면 묶은 탭이 본문 쪽을 보이게 한다.
    /// </summary>
    public static (IReadOnlyList<PlsqlTabFile> Tabs, int ActiveTab) Merge(IReadOnlyList<PlsqlTabFile> tabs, int activeTab)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        var result = new List<PlsqlTabFile>();
        var index = new Dictionary<(string?, string, string, string), int>();
        var active = -1;
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            if (KeyOf(tab) is not { } key)
            {
                if (i == activeTab)
                {
                    active = result.Count;
                }

                result.Add(tab);
                continue;
            }

            var isBody = PlsqlSourceText.IsBodyType(tab.Type);
            if (index.TryGetValue(key, out var at) && Fold(result[at], tab, isBody) is { } merged)
            {
                result[at] = merged;
                if (i == activeTab)
                {
                    active = at;
                }

                continue;
            }

            index.TryAdd(key, result.Count);
            if (i == activeTab)
            {
                active = result.Count;
            }

            result.Add(tab);
        }

        // 보이던 탭이 본문 탭이었으면 묶은 탭이 본문 쪽을 보인다(명세 탭이었으면 명세 쪽).
        if (activeTab >= 0 && activeTab < tabs.Count && active >= 0 && result[active].Body is not null && tabs[activeTab].Body is null)
        {
            result[active] = result[active] with { Side = PlsqlSourceText.IsBodyType(tabs[activeTab].Type) ? PlsqlTabFile.BodySide : null };
        }

        return (result, activeTab < 0 ? activeTab : active);
    }

    /// <summary>묶인 탭을 명세 탭·본문 탭 둘로 푼다(Markdown 쓰기). 보이던 쪽 표시는 버린다.</summary>
    public static IReadOnlyList<PlsqlTabFile> Split(IReadOnlyList<PlsqlTabFile> tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);
        return [.. tabs.SelectMany<PlsqlTabFile, PlsqlTabFile>(t => t.Body is { } body ? [t with { Body = null, Side = null }, body with { Body = null, Side = null }] : [t with { Side = null }])];
    }

    /// <summary>묶을 수 있는 탭의 열쇠(접속·소유자·이름·명세 유형). 서버 패키지·타입이 아니면 null.</summary>
    private static (string?, string, string, string)? KeyOf(PlsqlTabFile tab)
        => tab is { Owner: { } owner, Name: { } name, Type: { } type, FilePath: null } && PlsqlSourceText.SiblingType(type) is not null
            ? (tab.ConnectionProfileId?.ToString("n"), owner, name, PlsqlSourceText.SpecTypeOf(type))
            : null;

    /// <summary>앞 탭(<paramref name="first"/>)에 짝을 더한다. 이미 그 쪽이 있으면(같은 객체 탭이 둘) null — 따로 둔다.</summary>
    private static PlsqlTabFile? Fold(PlsqlTabFile first, PlsqlTabFile next, bool nextIsBody)
    {
        var firstIsBody = PlsqlSourceText.IsBodyType(first.Type);
        if (nextIsBody && !firstIsBody && first.Body is null && next.Body is null)
        {
            return first with { Body = next with { Side = null } };
        }

        if (!nextIsBody && firstIsBody && first.Body is null && next.Body is null)
        {
            return next with { Body = first with { Side = null }, Side = null };
        }

        return null;
    }
}
