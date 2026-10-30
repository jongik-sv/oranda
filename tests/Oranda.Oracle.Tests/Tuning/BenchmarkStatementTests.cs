using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Text;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.Tests.Tuning;

// 벤치마크 문장 검사: 바인드는 이름마다 값이 있을 때만 통과한다. 값 없이 부르는 사람 경로(튜닝 도우미)는 예전처럼 거부한다.
public class BenchmarkStatementTests
{
    /// <summary>분류를 고정한 언어 서비스(이 프로젝트는 Oranda.Language 를 참조하지 않는다).</summary>
    private sealed class OneQuery(params string[] binds) : ILanguageService
    {
        public IReadOnlyList<SqlStatement> Split(string text, SplitOptions o) =>
            [new SqlStatement(text, new TextRange(0, text.Length), StatementKind.Query) { Verb = "SELECT", BindNames = binds }];

        public SqlStatement? GetStatementAt(string text, int offset, SplitOptions o) => Split(text, o)[0];

        public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct) => Task.FromResult<IReadOnlyList<Diagnostic>>([]);

        public CompletionContext GetCompletionContext(string text, int offset) => CompletionContext.None;
    }

    private static readonly Dictionary<string, BindValue> WordDh =
        new(StringComparer.OrdinalIgnoreCase) { ["WORD_DH"] = new BindValue(BindDataType.Varchar2, "20261030") };

    [Fact]
    public void Statements_without_binds_pass_and_bind_statements_without_values_are_refused_as_before()
    {
        Assert.NotNull(OracleTuningService.BenchmarkStatement(new OneQuery(), "SELECT 1 FROM dual"));

        var refused = Assert.Throws<ArgumentException>(() => OracleTuningService.BenchmarkStatement(new OneQuery("WORD_DH"), "SELECT :word_dh FROM dual"));
        Assert.Contains("바인드·치환 변수가 있는 문장은 벤치마크하지 않습니다", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_statements_pass_only_when_every_bind_has_a_value_and_secret_names_never_pass()
    {
        Assert.NotNull(OracleTuningService.BenchmarkStatement(new OneQuery("word_dh"), "SELECT :word_dh FROM dual", WordDh));

        var missing = Assert.Throws<ArgumentException>(() =>
            OracleTuningService.BenchmarkStatement(new OneQuery("word_dh", "plant_cd"), "SELECT :word_dh, :plant_cd FROM dual", WordDh));
        Assert.Contains(":plant_cd", missing.Message, StringComparison.Ordinal);

        var secret = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase) { ["USER_PWD"] = new BindValue(BindDataType.Varchar2, "x") };
        var refused = Assert.Throws<ArgumentException>(() => OracleTuningService.BenchmarkStatement(new OneQuery("USER_PWD"), "SELECT :USER_PWD FROM dual", secret));
        Assert.Contains(":USER_PWD", refused.Message, StringComparison.Ordinal);
    }
}
