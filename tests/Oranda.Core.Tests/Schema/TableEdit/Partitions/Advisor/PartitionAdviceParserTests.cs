using System.Text.Json;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.Core.Tests.Schema.TableEdit.Partitions.Advisor;

// 소유: pa-core-l1 PL1. 구조 결과 → 글 속 JSON 블록 → 첫 { … } → null(설계 PA-6).
public class PartitionAdviceParserTests
{
    private const string Body = """{ "verdict": "partition", "summary": "s", "candidates": [ { "rank": 1, "title": "t", "method": "interval-ish", "key_columns": ["ORDER_DT"], "layout": { "kind": "weekly", "groups": [ { "name": "P1", "is_default": false } ] }, "local_indexes": [], "reasons": ["r"], "cautions": [] } ], "not_chosen": [] }""";

    [Fact]
    public void Text_with_a_fenced_json_block_is_read_and_unknown_enum_text_is_kept()
    {
        var text = "다음과 같습니다.\n```json\n" + Body + "\n```\n끝.";

        var advice = PartitionAdviceParser.Parse(null, text);

        Assert.NotNull(advice);
        var c = Assert.Single(advice.Candidates);
        Assert.Equal("interval-ish", c.Method); // 버리지 않음 — 확인이 ✖
        Assert.Equal("weekly", c.Layout.Kind);
        Assert.Empty(Assert.Single(c.Layout.Groups!).Values); // 빠진 목록 = 빈 목록
    }

    [Fact]
    public void Text_without_a_fence_uses_the_first_balanced_object_even_with_braces_in_strings()
    {
        var text = "설명 {잘못된 괄호} 먼저.\n" + Body.Replace("\"s\"", "\"a } b { c\"", StringComparison.Ordinal) + "\n이후 {x}";

        var advice = PartitionAdviceParser.Parse(null, text);

        Assert.NotNull(advice);
        Assert.Equal("a } b { c", advice.Summary);
    }

    [Fact]
    public void Broken_json_or_no_json_returns_null()
    {
        Assert.Null(PartitionAdviceParser.Parse(null, "```json\n{ \"verdict\": \"partition\", \"candidates\": [ \n```"));
        Assert.Null(PartitionAdviceParser.Parse(null, "JSON 이 없습니다."));
        Assert.Null(PartitionAdviceParser.Parse(null, "{ \"summary\": \"verdict 없음\" }"));
        Assert.Null(PartitionAdviceParser.Parse(null, "{ \"verdict\": \"partition\", \"candidates\": [ { \"rank\": \"first\" } ] }")); // 형식이 틀림
        Assert.Null(PartitionAdviceParser.Parse(null, "   "));
    }

    [Fact]
    public void Structured_result_wins_but_a_useless_one_falls_back_to_the_text()
    {
        using var structured = JsonDocument.Parse("""{ "verdict": "no_partition", "summary": "구조" }""");
        using var useless = JsonDocument.Parse("""{ "summary": "verdict 없음" }""");
        const string text = "```\n{ \"verdict\": \"partition\", \"summary\": \"글\" }\n```";

        Assert.Equal("구조", PartitionAdviceParser.Parse(structured.RootElement, text)!.Summary);
        Assert.Equal("글", PartitionAdviceParser.Parse(useless.RootElement, text)!.Summary);
    }

    [Fact]
    public void Structured_result_given_as_a_json_string_is_read()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(Body));

        var advice = PartitionAdviceParser.Parse(doc.RootElement, null);

        Assert.Equal(AdviceVerdict.Partition, advice!.Verdict);
    }
}
