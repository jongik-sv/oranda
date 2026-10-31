using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>AI 인덱스 후보 하나와 받은 뒤 확인(<see cref="AiIndexReview"/>)이 붙인 표시 줄.</summary>
/// <param name="Suggestion">AI 가 낸 후보(문안은 그대로).</param>
/// <param name="Notes">후보 머리에 보일 확인 결과 줄(없을 수 있다).</param>
public sealed record ReviewedIndex(TuningIndexSuggestion Suggestion, IReadOnlyList<string> Notes);

/// <summary>확인 결과: 남은 후보와, 기존 인덱스가 이미 덮어서 뺀 후보의 안내 줄.</summary>
public sealed record IndexReviewResult(IReadOnlyList<ReviewedIndex> Kept, IReadOnlyList<string> Dropped);

/// <summary>
/// AI 인덱스 후보를 그 표의 실제 기존 인덱스와 대조하는 결정론 확인(받은 뒤). 겹침 판정은 <see cref="IndexAdvisor.Overlap"/> 을 쓴다.
/// 기존 인덱스를 읽지 못했으면 대조를 건너뛴다(「확인 못함」). 후보의 문안은 바꾸지 않는다.
/// </summary>
public static class AiIndexReview
{
    /// <summary>후보 한 개의 열 수가 이 값을 넘으면 경고한다(<see cref="IndexAdvisor.MaxColumns"/>).</summary>
    public const int WarnColumns = IndexAdvisor.MaxColumns + 1;

    /// <summary>
    /// 후보마다 표의 기존 인덱스를 <paramref name="read"/>(소유자 — 모르면 빈 글, 표) 로 읽어 대조한다.
    /// <paramref name="read"/> 가 null 이거나 null 을 돌려주거나 던지면 그 표는 「확인 못함」 이다.
    /// </summary>
    public static async Task<IndexReviewResult> ReviewAsync(
        IReadOnlyList<TuningIndexSuggestion> suggestions,
        Func<string, string, CancellationToken, Task<IReadOnlyList<IndexColumnsRow>?>>? read,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        var cache = new Dictionary<string, IReadOnlyList<IndexColumnsRow>?>(StringComparer.OrdinalIgnoreCase);
        List<ReviewedIndex> kept = [];
        List<string> dropped = [];
        foreach (var suggestion in suggestions)
        {
            var (owner, table) = SplitTable(suggestion.Table);
            IReadOnlyList<IndexColumnsRow>? existing = null;
            if (read is not null)
            {
                var key = owner + "." + table;
                if (!cache.TryGetValue(key, out existing))
                {
                    try
                    {
                        existing = await read(owner, table, ct).ConfigureAwait(true);
                    }
#pragma warning disable CA1031 // 기존 인덱스를 못 읽는 것은 대조만 건너뛰게 한다: 후보를 막지 않는다.
                    catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        existing = null;
                    }

                    cache[key] = existing;
                }
            }

            var verdict = Review(suggestion, existing);
            if (verdict.Dropped is { } line)
            {
                dropped.Add(line);
            }
            else
            {
                kept.Add(new ReviewedIndex(suggestion, verdict.Notes));
            }
        }

        return new IndexReviewResult(kept, dropped);
    }

    /// <summary>
    /// 순수 판정. <paramref name="existing"/> 은 그 표의 기존 인덱스 열 구성(읽지 못했으면 null).
    /// 뺀다면 <c>Dropped</c> 에 「뺀 인덱스 후보」 한 줄이 있다.
    /// </summary>
    public static (string? Dropped, IReadOnlyList<string> Notes) Review(TuningIndexSuggestion suggestion, IReadOnlyList<IndexColumnsRow>? existing)
    {
        ArgumentNullException.ThrowIfNull(suggestion);
        List<string> notes = [];
        var columns = suggestion.Columns;
        if (columns.Count >= WarnColumns)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"열이 {columns.Count}개입니다. 조건·조인 열만 {IndexAdvisor.MaxColumns}개 이하로 두는 것이 좋습니다. 조회 열까지 담는 커버링은 측정으로 확인한 경우에만 쓰세요."));
        }

        if (existing is null)
        {
            if (!suggestion.IsModify)
            {
                notes.Add("기존 인덱스를 확인하지 못했습니다. 이 표에 이미 쓸 만한 인덱스가 있는지 먼저 보세요.");
            }

            return (null, notes);
        }

        if (suggestion.IsModify)
        {
            if (!string.IsNullOrWhiteSpace(suggestion.ExistingIndex) && !existing.Any(ix => IsIndex(ix, suggestion.ExistingIndex)))
            {
                notes.Add($"AI 가 말한 기존 인덱스 {suggestion.ExistingIndex} 를 이 표에서 찾지 못했습니다. 이름을 확인하세요.");
            }

            // 바꿀 인덱스 자신과는 견주지 않는다. 다른 인덱스가 이미 덮으면 변형할 이유가 없다.
            existing = existing.Where(ix => !IsIndex(ix, suggestion.ExistingIndex)).ToList();
        }

        var (kind, index) = IndexAdvisor.Overlap(columns, allEquality: false, existing);
        if (index is null)
        {
            return (null, notes);
        }

        var shown = $"{index.IndexName} ({string.Join(", ", index.Columns)})";
        switch (kind)
        {
            case IndexOverlapKind.Covered:
                return ($"뺀 인덱스 후보: {suggestion.Table} ({string.Join(", ", columns)}) — 기존 {shown} 가 이미 덮음", notes);
            case IndexOverlapKind.Extends or IndexOverlapKind.SharesLeading when !suggestion.IsModify:
                notes.Add($"기존 {shown} 를 넓히는 안과 같음. 새로 만들기 전에 그 인덱스를 바꾸는 쪽을 먼저 보세요.");
                break;
        }

        return (null, notes);
    }

    private static bool IsIndex(IndexColumnsRow row, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var bare = name.Trim().Trim('"');
        var dot = bare.LastIndexOf('.');
        return string.Equals(row.IndexName, dot < 0 ? bare : bare[(dot + 1)..], StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.IndexName, bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary><c>OWNER.TABLE</c> 을 가른다(소유자가 없으면 빈 글, 따옴표는 벗기고 대문자로).</summary>
    internal static (string Owner, string Table) SplitTable(string text)
    {
        var parts = (text ?? string.Empty).Trim().Split('.', 2);
        static string Clean(string value) => value.Trim().Trim('"').ToUpperInvariant();
        return parts.Length == 2 ? (Clean(parts[0]), Clean(parts[1])) : (string.Empty, Clean(parts[0]));
    }
}
