using System.Text.Json;
using System.Text.RegularExpressions;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// AI 응답 → <see cref="PartitionAdvice"/>(설계 PA-6). 읽는 순서: 구조 결과(객체, 또는 JSON 을 담은 글) → 응답 글의 <c>```json</c> 울타리 블록 →
/// 글의 첫 균형 잡힌 <c>{ … }</c>. 못 읽으면 null — 화면은 원문만 보이고 「편집기에 채우기」를 끈다. 빠진 목록은 빈 목록으로 채운다.
/// 방식·배치 같은 고르는 값이 스키마 밖 글이어도 버리지 않는다(앱 확인이 <see cref="AdviceCheckCodes.MethodUnknown"/> ✖ 로 거른다).
/// </summary>
public static partial class PartitionAdviceParser
{
    // 글 속 { 시작을 몇 개까지 시도하나(아주 긴 글에서 느려지지 않게).
    private const int MaxObjectTries = 16;

    private static readonly JsonDocumentOptions Lenient = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static PartitionAdvice? Parse(JsonElement? result, string? text)
    {
        if (result is { } r)
        {
            if (r.ValueKind == JsonValueKind.Object && TryRead(r) is { } structured)
            {
                return structured;
            }

            if (r.ValueKind == JsonValueKind.String && FromText(r.GetString()) is { } quoted)
            {
                return quoted;
            }
        }

        return FromText(text);
    }

    private static PartitionAdvice? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (Match fence in Fence().Matches(text))
        {
            if (TryParse(fence.Groups["body"].Value) is { } fenced)
            {
                return fenced;
            }
        }

        var tries = 0;
        var start = text.IndexOf('{', StringComparison.Ordinal);
        while (start >= 0 && tries++ < MaxObjectTries)
        {
            var end = MatchingBrace(text, start);
            if (end < 0)
            {
                return null; // 닫히지 않음 — 뒤에도 온전한 객체가 없다
            }

            if (TryParse(text[start..(end + 1)]) is { } advice)
            {
                return advice;
            }

            start = text.IndexOf('{', start + 1);
        }

        return null;
    }

    // start 의 { 와 짝인 } 위치. 따옴표 글 안의 괄호·escape 는 건너뛴다. 없으면 -1.
    private static int MatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (ch == '\\')
                {
                    i++;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}' when --depth == 0:
                    return i;
            }
        }

        return -1;
    }

    private static PartitionAdvice? TryParse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, Lenient);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? TryRead(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static PartitionAdvice? TryRead(JsonElement element)
    {
        try
        {
            var raw = element.Deserialize(PartitionAdviceJsonContext.Default.PartitionAdvice);
            return raw is null || string.IsNullOrWhiteSpace(raw.Verdict) ? null : Normalize(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 역직렬화는 빠진 생성자 인자를 null 로 넘긴다 — 목록 null 을 빈 목록으로 바꾼다.
    private static PartitionAdvice Normalize(PartitionAdvice a) => a with
    {
        Summary = a.Summary ?? string.Empty,
        Candidates = [.. (a.Candidates ?? []).Where(c => c is not null).Select(Normalize)],
        NotChosen = [.. (a.NotChosen ?? []).Where(n => n is not null).Select(n => n with { Column = n.Column ?? string.Empty, Reason = n.Reason ?? string.Empty })],
    };

    private static PartitionCandidate Normalize(PartitionCandidate c) => c with
    {
        Title = c.Title ?? string.Empty,
        Method = c.Method ?? string.Empty,
        KeyColumns = c.KeyColumns ?? [],
        Layout = c.Layout is { } layout ? layout with { Kind = layout.Kind ?? string.Empty, Groups = Normalize(layout.Groups) } : new PartitionLayout(string.Empty),
        LocalIndexes = c.LocalIndexes ?? [],
        Reasons = c.Reasons ?? [],
        Cautions = c.Cautions ?? [],
        Subpartition = c.Subpartition is { } sp ? sp with { Method = sp.Method ?? string.Empty, KeyColumns = sp.KeyColumns ?? [], Groups = Normalize(sp.Groups) } : null,
    };

    private static IReadOnlyList<ListGroup>? Normalize(IReadOnlyList<ListGroup>? groups) =>
        groups is null ? null : [.. groups.Where(g => g is not null).Select(g => g with { Name = g.Name ?? string.Empty, Values = g.Values ?? [] })];

    [GeneratedRegex(@"```[ \t]*(?:json)?[ \t]*\r?\n?(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Fence();
}
