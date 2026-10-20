using System.Text;
using Oranda.Core.Language;

namespace Oranda.Language.Tests.Parsing;

// 소유: TSK-04-09. L2 증분 분석: 앞서 분석한 서비스가 낸 결과가 처음 보는 서비스의 전체 분석 결과와 같은지,
// 바뀐 문장만 다시 파싱하는지(캐시가 문서보다 작아도 분석 도중 지우지 않는지). 시간은 단정하지 않고 파싱한 문장 수를 센다.
public class IncrementalAnalysisTests
{
    /// <summary>시험 문서의 문장 수. 6가지 모양을 돌아가며 쓰고 그중 절반이 오류다(편집 앞뒤 모두에 오류가 있다).</summary>
    private const int Statements = 36;

    /// <summary>캐시를 문서보다 작게 준 경우(퇴출이 매 분석마다 돈다).</summary>
    private const int TinyCache = 8;

    private static readonly string Base = Document(Statements);

    /// <summary>편집 이름 → 편집. 앞·중간·끝, 문장 추가·삭제, 문장 합치기, 블록 안 편집, 뒤쪽 전체에 영향을 주는 편집.</summary>
    private static readonly Dictionary<string, Func<string, string>> Edits = new()
    {
        ["앞에 문장 추가"] = d => "SELECT 0 FROM dual;\n" + d,
        ["앞 문장 삭제"] = d => d[(d.IndexOf(";\n", StringComparison.Ordinal) + 2)..],
        ["맨 앞 글자 하나 넣기"] = d => " " + d,
        ["중간에 오류 문장 추가"] = d => d.Insert(Middle(d), "SELECT * FROM WHERE y = 1;\n"),
        ["중간 줄 삭제"] = d => d.Remove(Middle(d), d.IndexOf('\n', Middle(d)) + 1 - Middle(d)),
        ["중간 오류 고치기"] = d => ReplaceAfter(d, Middle(d), "SELECT * FROM WHERE", "SELECT * FROM t WHERE"),
        ["세미콜론을 지워 두 문장 합치기"] = d => d.Remove(d.IndexOf(';', Middle(d)), 1),
        ["PL/SQL 블록 안 편집"] = d => ReplaceAfter(d, Middle(d), "y := fn(", "y := fn(("),
        ["중간에 블록 주석 열기"] = d => d.Insert(Middle(d), "/* "),
        ["중간에 작은따옴표 열기"] = d => d.Insert(Middle(d), "'"),
        ["끝에 문장 추가"] = d => d + "select (1 from dual;\n",
        ["끝 줄 삭제"] = d => d[..(d.LastIndexOf('\n', d.Length - 2) + 1)],
        ["끝 줄바꿈 삭제"] = d => d[..^1],
        ["문서 전체 바꾸기"] = _ => Document(10),
    };

    public static TheoryData<string, int> EditCases()
    {
        var data = new TheoryData<string, int>();
        foreach (var name in Edits.Keys)
        {
            data.Add(name, LanguageService.AnalysisCacheLimit);
            data.Add(name, TinyCache);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EditCases))]
    public async Task Incremental_result_equals_full_analysis_after_an_edit(string edit, int cacheLimit)
    {
        var service = new LanguageService { CacheLimit = cacheLimit };
        var before = await service.AnalyzeAsync(Base, CancellationToken.None);
        Assert.Equal(await Full(Base), before);
        Assert.True(before.Count(d => d.Severity == DiagnosticSeverity.Error) >= Statements / 2, "편집 앞뒤에 오류가 있어야 비교가 뜻이 있다");

        var edited = Edits[edit](Base);
        Assert.NotEqual(Base, edited);
        var after = await service.AnalyzeAsync(edited, CancellationToken.None);

        Assert.Equal(await Full(edited), after);
    }

    [Theory]
    [InlineData(LanguageService.AnalysisCacheLimit)]
    [InlineData(TinyCache)]
    public async Task Opening_and_closing_a_block_comment_hides_then_restores_the_errors_after_it(int cacheLimit)
    {
        // 블록 주석은 겹치지 않는다: 문서 안에 「*/」 가 있으면 연 주석이 거기서 닫힌다. 그래서 줄 주석만 쓴 문서로 연다.
        var doc = Base.Replace("/* 주석", "-- 주석", StringComparison.Ordinal).Replace("   이어짐 */", "-- 이어짐", StringComparison.Ordinal);
        var service = new LanguageService { CacheLimit = cacheLimit };
        await service.AnalyzeAsync(doc, CancellationToken.None);
        int open = Middle(doc);
        var opened = doc.Insert(open, "/* ");
        int close = opened.IndexOf('\n', opened.Length * 3 / 4) + 1;
        var closed = opened.Insert(close, "*/\n");

        var whileOpen = await service.AnalyzeAsync(opened, CancellationToken.None);
        Assert.Equal(await Full(opened), whileOpen);
        Assert.DoesNotContain(whileOpen, d => d.Range.Start > open);

        long parsed = service.ParsedStatementCount;
        var afterClose = await service.AnalyzeAsync(closed, CancellationToken.None);

        var expected = await Full(closed);
        Assert.Equal(expected, afterClose);
        Assert.Contains(afterClose, d => d.Range.Start > close);

        // 닫은 뒤 돌아온 문장은 직전 분석들의 캐시에서 위치만 옮겨 쓴다. 다시 파싱하는 것은 주석과 맞닿은 문장뿐이다.
        Assert.InRange(service.ParsedStatementCount - parsed, 0, 2);
    }

    [Fact]
    public async Task Typing_a_few_keys_in_a_row_parses_only_the_statement_being_typed()
    {
        var service = new LanguageService { CacheLimit = TinyCache };
        var text = Base;
        await service.AnalyzeAsync(text, CancellationToken.None);
        int at = Middle(text);

        foreach (var key in "SELECT 9")
        {
            text = text.Insert(at++, key.ToString());
            long parsed = service.ParsedStatementCount;
            var result = await service.AnalyzeAsync(text, CancellationToken.None);

            Assert.Equal(await Full(text), result);
            Assert.InRange(service.ParsedStatementCount - parsed, 0, 1);
        }
    }

    [Fact]
    public async Task Cache_stays_within_its_limit_across_many_documents()
    {
        const int limit = 64;
        var service = new LanguageService { CacheLimit = limit };
        for (var i = 0; i < 12; i++)
        {
            // 문서마다 문장 원문이 다르다(번호가 다르다). 두 분석이 쓴 문장(최대 40개)은 상한의 3/4 안이다.
            var doc = string.Concat(Enumerable.Range(0, 20).Select(n => $"SELECT {(i * 100) + n} FROM dual;\n"));
            await service.AnalyzeAsync(doc, CancellationToken.None);

            Assert.InRange(service.CachedStatementCount, 1, limit);
        }
    }

    /// <summary>
    /// TSK-04-09 회귀: 1만 줄 측정 문서(Perf.Ui diagnostics 와 같은 모양, 문장 7,500개)는 예전 캐시 상한 4,096 보다 문장이 많아
    /// 편집할 때마다 문서 전체를 다시 파싱했다. 기본 상한으로 끝·앞을 고쳐도 바뀐 문장만 파싱해야 한다.
    /// </summary>
    [Fact]
    public async Task Edit_in_a_10k_line_document_parses_only_the_changed_statement()
    {
        var service = new LanguageService();
        var doc = TenThousandLineDocument();
        await service.AnalyzeAsync(doc, CancellationToken.None);
        long cold = service.ParsedStatementCount;
        Assert.True(cold > 4096, $"문장 {cold}개: 예전 상한보다 많아야 회귀 시험이 된다");

        const string bad = "select (1 from dual;\n";
        var atEnd = doc + bad;
        var endResult = await service.AnalyzeAsync(atEnd, CancellationToken.None);
        Assert.Equal(1, service.ParsedStatementCount - cold);
        Assert.InRange(Assert.Single(endResult).Range.Start, atEnd.Length - bad.Length, atEnd.Length);

        var atFront = bad + doc;
        var frontResult = await service.AnalyzeAsync(atFront, CancellationToken.None);
        Assert.Equal(1, service.ParsedStatementCount - cold);
        Assert.InRange(Assert.Single(frontResult).Range.Start, 0, bad.Length);

        var removed = await service.AnalyzeAsync(doc, CancellationToken.None);
        Assert.Empty(removed);
        Assert.Equal(1, service.ParsedStatementCount - cold);
    }

    private static Task<IReadOnlyList<Diagnostic>> Full(string text)
        => new LanguageService().AnalyzeAsync(text, CancellationToken.None);

    /// <summary>문서 가운데쯤의 줄 시작.</summary>
    private static int Middle(string text) => text.IndexOf('\n', text.Length / 2) + 1;

    private static string ReplaceAfter(string text, int from, string oldValue, string newValue)
    {
        int at = text.IndexOf(oldValue, from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{oldValue}' 가 {from} 뒤에 없다");
        return string.Concat(text.AsSpan(0, at), newValue, text.AsSpan(at + oldValue.Length));
    }

    /// <summary>줄 주석·블록 주석·PL/SQL 블록·DML 이 섞인 문서. 문장 셋 중 하나꼴로 오류(SELECT·PL/SQL 모두)가 있다.</summary>
    private static string Document(int statements)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < statements; i++)
        {
            sb.Append((i % 6) switch
            {
                0 => $"-- 문장 {i}\nSELECT col{i}, :b{i} FROM tab_{i} WHERE id = {i};\n",
                1 => $"SELECT * FROM WHERE x = {i};\n",
                2 => $"/* 주석 {i}\n   이어짐 */\nUPDATE t SET a = {i} WHERE id = {i};\n",
                3 => $"BEGIN\n  IF x{i} > 1 THEN y := fn({i}); END IF;\nEND;\n",
                4 => $"BEGIN\n  x := ;\nEND;\n",
                _ => $"SELECT a, FROM t{i};\n",
            });
        }

        return sb.ToString();
    }

    /// <summary>tests/Oranda.Perf.Common 의 <c>SampleSql.EditorDocument(10000)</c> 과 같은 문서(오류 없음).</summary>
    private static string TenThousandLineDocument()
    {
        var sb = new StringBuilder(10_000 * 40);
        for (var i = 0; i < 10_000; i++)
        {
            sb.Append((i % 20) switch
            {
                0 => "-- section " + i,
                5 => "/* block comment " + i,
                6 => "   still comment */",
                10 => "BEGIN",
                11 => "  IF x" + i + " > 1 THEN y := fn(" + i + ", 'text " + i + "'); END IF;",
                12 => "END;",
                _ => "SELECT col" + i + ", :bind" + i + ", 'literal' FROM tab_" + i + " WHERE id = " + i + ";",
            }).Append('\n');
        }

        return sb.ToString();
    }
}
