using System;
using System.Collections.Generic;

namespace Oranda.App.Shell.Keymaps;

/// <summary>
/// 키맵 <c>when</c> 포커스 키 → 포커스 자리 (commands.md 2.2). 한 순간에 포커스 자리는 하나만 참이다.
/// 같은 키 두 바인딩의 자리가 겹치면 충돌이다(2.3). 전역(null)은 모든 자리와 겹친다.
/// </summary>
public static class FocusScopes
{
    private static readonly string[] AllSlots = ["sqlText", "sqlGrid", "plsql", "mybatisText", "schema", "monitor", "lockMonitor"];

    private static readonly Dictionary<string, string[]> Slots = new(StringComparer.Ordinal)
    {
        ["sqlEditorFocus"] = ["sqlText"],
        ["gridFocus"] = ["sqlGrid"],
        ["sqlDocFocus"] = ["sqlText", "sqlGrid"],
        ["plsqlEditorFocus"] = ["plsql"],
        ["mybatisDocFocus"] = ["mybatisText"],
        ["editorFocus"] = ["sqlText", "plsql", "mybatisText"],
        ["schemaFocus"] = ["schema"],
        ["monitorFocus"] = ["monitor"],
        ["lockMonitorFocus"] = ["lockMonitor"],
    };

    /// <summary>키맵 <c>when</c> 에 쓸 수 있는 포커스 키.</summary>
    public static IReadOnlyCollection<string> Keys => Slots.Keys;

    /// <summary>포커스 자리 목록. 모르는 키는 <see cref="ArgumentException"/>.</summary>
    public static IReadOnlyList<string> SlotsOf(string? when) =>
        when is null ? AllSlots
        : Slots.TryGetValue(when, out var s) ? s
        : throw new ArgumentException($"모르는 포커스 키 '{when}'.", nameof(when));

    /// <summary>두 <c>when</c> 의 포커스 자리가 겹치는지.</summary>
    public static bool Overlaps(string? a, string? b)
    {
        foreach (var slot in SlotsOf(a))
        {
            if (((IList<string>)SlotsOf(b)).Contains(slot))
            {
                return true;
            }
        }

        return false;
    }
}
