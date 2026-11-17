using System.Text;

namespace Oranda.Core.Schema.TableEdit;

/// <summary>테이블 이름을 따라 바꿀 규칙 이름 하나(<see cref="TableEditPage.Constraints"/> 또는 <see cref="TableEditPage.Indexes"/>).</summary>
internal sealed record RuleRename(string RowKey, TableEditPage Page, string OldName, string NewName);

/// <summary>
/// 테이블 이름 바꾸기의 「규칙 이름도 바꾸기」(설계 18.1, TE-38). 문장 빌더와 검사 계획이 같은 목록을 쓴다.
/// 대상 = <c>PK_&lt;옛 이름&gt;</c>·<c>UK|FK|CK|IX_&lt;옛 이름&gt;_NN</c> 꼴 이름 가운데 사용자가 이름을 고치지 않았고 지우지 않는 줄.
/// 제약이 만든 인덱스는 그 제약이 남고 정의가 그대로일 때만(제약을 지우거나 다시 만들면 인덱스도 사라진다).
/// </summary>
internal static class TableEditRuleNames
{
    public static IReadOnlyList<RuleRename> Plan(TableDraft original, TableDraft edited, TableDraftChanges changes, int maxIdentifierBytes)
    {
        if (!changes.Renamed || !edited.RenameRuleNames)
        {
            return [];
        }

        var result = new List<RuleRename>();
        foreach (var c in edited.Constraints.Where(c => !c.IsDropped && changes.StateOf(c.RowKey) != DraftRowState.Dropped))
        {
            if (original.Constraint(c.RowKey) is { } o && o.Name == c.Name && Rename(c.Name, original.Name, edited.Name, maxIdentifierBytes) is { } renamed)
            {
                result.Add(new RuleRename(c.RowKey, TableEditPage.Constraints, c.Name, renamed));
            }
        }

        foreach (var i in edited.Indexes.Where(i => !i.IsDropped && changes.StateOf(i.RowKey) != DraftRowState.Dropped))
        {
            if (original.Index(i.RowKey) is not { } o || o.Name != i.Name || TableDraftDiff.GoesWithDroppedColumn(o, changes))
            {
                continue;
            }

            if (o.ConstraintName is { } owner && !ConstraintIndexKept(original, changes, owner))
            {
                continue;
            }

            if (Rename(i.Name, original.Name, edited.Name, maxIdentifierBytes) is { } renamed)
            {
                result.Add(new RuleRename(i.RowKey, TableEditPage.Indexes, i.Name, renamed));
            }
        }

        return result;
    }

    /// <summary>제약 인덱스가 실행 뒤에도 같은 이름으로 남는가: 그 제약이 원본에 있고, 지우지 않고, 정의를 바꾸지 않는다(DROP + ADD 면 인덱스가 사라진다).</summary>
    private static bool ConstraintIndexKept(TableDraft original, TableDraftChanges changes, string constraintName)
    {
        var owner = original.Constraints.FirstOrDefault(c => string.Equals(c.Name, constraintName, StringComparison.Ordinal));
        if (owner is null)
        {
            return false;
        }

        var diff = changes.Constraints.FirstOrDefault(d => d.RowKey == owner.RowKey);
        return diff is null || (diff.State != DraftRowState.Dropped && !diff.Changes.HasFlag(ConstraintChange.Definition));
    }

    /// <summary>규칙 이름이면 새 테이블 이름으로 바꾼 이름(바이트 한도에 맞춰 테이블 이름 쪽을 줄임), 아니면 null.</summary>
    internal static string? Rename(string name, string oldTable, string newTable, int maxBytes)
    {
        if (name == "PK_" + oldTable)
        {
            return "PK_" + Fit(newTable, maxBytes - 3);
        }

        foreach (var prefix in (string[])["UK_", "FK_", "CK_", "IX_"])
        {
            var stem = prefix + oldTable + "_";
            if (name.StartsWith(stem, StringComparison.Ordinal) && name.Length > stem.Length && name[stem.Length..].All(char.IsAsciiDigit))
            {
                var suffix = name[(stem.Length - 1)..];
                return prefix + Fit(newTable, maxBytes - prefix.Length - Encoding.UTF8.GetByteCount(suffix)) + suffix;
            }
        }

        return null;
    }

    private static string Fit(string name, int maxBytes)
    {
        var fit = name;
        while (fit.Length > 1 && Encoding.UTF8.GetByteCount(fit) > maxBytes)
        {
            fit = fit[..^1];
        }

        return fit;
    }
}
