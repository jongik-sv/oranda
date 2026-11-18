using System.Text.Json;

namespace Oranda.Core.Schema.TableEdit.Partitions.Advisor;

/// <summary>
/// AI 응답 → <see cref="PartitionAdvice"/>(설계 PA-6). 구조 결과가 있으면 그것, 없으면 응답 글의 첫 JSON 블록(PC3).
/// 못 읽으면 null — 화면은 원문만 보이고 「편집기에 채우기」를 끈다. 빠진 목록은 빈 목록으로 채운다.
/// </summary>
public static class PartitionAdviceParser
{
    public static PartitionAdvice? Parse(JsonElement? result, string? text)
    {
        if (result is { ValueKind: JsonValueKind.Object } structured && TryRead(structured) is { } advice)
        {
            return advice;
        }

        // 글 속 JSON 블록 읽기 = PC3.
        _ = text;
        return null;
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
        NotChosen = [.. (a.NotChosen ?? []).Where(n => n is not null)],
    };

    private static PartitionCandidate Normalize(PartitionCandidate c) => c with
    {
        Title = c.Title ?? string.Empty,
        Method = c.Method ?? string.Empty,
        KeyColumns = c.KeyColumns ?? [],
        Layout = c.Layout ?? new PartitionLayout(string.Empty),
        LocalIndexes = c.LocalIndexes ?? [],
        Reasons = c.Reasons ?? [],
        Cautions = c.Cautions ?? [],
        Subpartition = c.Subpartition is { } sp ? sp with { KeyColumns = sp.KeyColumns ?? [] } : null,
    };
}
