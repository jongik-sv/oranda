using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oranda.Language.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.Language.Tests.MyBatis;

/// <summary>
/// 계산기 결과를 MyBatis 3.5.19 실제 출력(Golden/*.golden.json)과 비교한다(TSK-02-02 acceptance 1).
/// 비교 = Oranda 의 :이름 을 ? 로 바꾸고 공백 덩어리를 하나로 줄인 SQL, 그리고 바인드 값 순서. MyBatis 가 예외를 낸 값 묶음은 계산 문제가 있어야 한다.
/// </summary>
public partial class GoldenEvaluatorTests
{
    private static readonly MyBatisEvaluator Evaluator = new();

    public static TheoryData<string> GoldenFiles() => new()
    {
        "EmpMapper", "TagSamples", "WithBom", "Crlf", "WithCData", "WithComments", "WithEntities", "Korean", "WithDoctype", "NoTrailingNewLine",
    };

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Statements_expand_like_MyBatis_3_5_19(string name)
    {
        using var golden = GoldenData.Load(name + ".golden.json");
        var root = golden.RootElement;
        var xml = MapperText(name);
        Assert.Equal(root.GetProperty("mapperSha256").GetString(), Sha256(xml)); // 표본이 바뀌면 골든을 다시 만든다(Golden/README.md)
        var mapper = GoldenMapper.Of(xml);

        var failures = new List<string>();
        int checkedCases = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            var caseName = c.GetProperty("name").GetString()!;
            var values = (Dictionary<string, object?>)GoldenData.Decode(c.GetProperty("values"))!;
            var result = Evaluator.Evaluate(mapper[c.GetProperty("statement").GetString()!], mapper, values, null);
            checkedCases++;

            if (c.GetProperty("error").GetString() is { } error)
            {
                // MyBatis 도 실패한 값: foreach null(BuilderException 「evaluated to a null value」) = Assumed(바인드 자리 하나로 펼친 검사용 가정,
                // 2026-10-24 사용자 결정), 그 밖의 BuilderException(목록 아님) = InvalidTag, bind 의 null 더하기(NPE) = Unevaluable.
                var expectedKind = error.Contains("evaluated to a null value", StringComparison.Ordinal) ? MyBatisEvalIssueKind.Assumed
                    : error.Contains("BuilderException", StringComparison.Ordinal) ? MyBatisEvalIssueKind.InvalidTag
                    : MyBatisEvalIssueKind.Unevaluable;
                if (!result.Issues.Any(i => i.Kind == expectedKind))
                {
                    failures.Add($"{caseName}: MyBatis 오류({error})인데 {expectedKind} 문제가 없다");
                }

                continue;
            }

            var expectedSql = c.GetProperty("normalizedSql").GetString();
            var actualSql = Normalize(ToQuestionMarks(result));
            if (!string.Equals(expectedSql, actualSql, StringComparison.Ordinal))
            {
                failures.Add($"{caseName}:\n  기대 {expectedSql}\n  실제 {actualSql}");
                continue;
            }

            var expectedValues = c.GetProperty("params").EnumerateArray().Select(p => GoldenData.Decode(p.GetProperty("value"))).ToList();
            var actualValues = result.Binds.Select(b => b.Value).ToList();
            if (!expectedValues.SequenceEqual(actualValues))
            {
                failures.Add($"{caseName}: 바인드 값 기대 [{string.Join(", ", expectedValues)}] 실제 [{string.Join(", ", actualValues)}]");
            }
        }

        Assert.True(checkedCases > 0);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void Golden_mapper_errors_are_outside_the_evaluator()
    {
        // MyBatis 가 매퍼 자체를 읽지 못한 표본(namespace 없음·같은 id 둘)은 계산기 골든이 아니다. 오류가 기록돼 있는지만 본다.
        foreach (var name in new[] { "NoNamespace", "DuplicateIds" })
        {
            using var golden = GoldenData.Load(name + ".golden.json");
            Assert.NotNull(golden.RootElement.GetProperty("mapperError").GetString());
        }
    }

    internal static string MapperText(string name) => name == "TagSamples"
        ? File.ReadAllText(GoldenData.Path("TagSamples.xml")).Replace("\r\n", "\n", StringComparison.Ordinal)
        : MapperSamples.All[name];

    internal static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(text)));

    /// <summary>펼친 SQL 의 바인드 :이름 을 ? 로(바인드 목록에 있는 이름만).</summary>
    internal static string ToQuestionMarks(EvalResult result)
    {
        var names = result.Binds.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        return BindRegex().Replace(result.Sql, m => names.Contains(m.Value[1..]) ? "?" : m.Value);
    }

    internal static string Normalize(string sql) => WhitespaceRegex().Replace(sql, " ").Trim();

    [GeneratedRegex(@":[A-Za-z_][\w$#]*")]
    private static partial Regex BindRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
