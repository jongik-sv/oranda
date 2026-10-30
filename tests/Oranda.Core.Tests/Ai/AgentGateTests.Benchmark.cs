using System.Diagnostics;
using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Execution;

namespace Oranda.Core.Tests.Ai;

// benchmark_queries 거부·실패(2026-10-30): 변형 sql 생략 = 요청 문장, 앱이 쓴 실패 글을 Query 수준에서도 보이기, 실패 원문 로그.
public partial class AgentGateTests
{
    [Fact]
    public async Task A_variant_without_sql_measures_the_request_statement_even_when_it_is_longer_than_the_argument_limit()
    {
        var fx = new GateFixture();
        var statement = "SELECT * FROM TB_ORDER o WHERE o.stat_cd = 'A'" + new string(' ', 25_000) + "AND 1 = 1";
        var session = Session(statement: statement);

        var response = await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"original"},{"label":"rewrite","sql":"SELECT * FROM TB_ORDER"}]}""");

        Assert.False(response.Result.IsError, response.Result.Text);
        var call = Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
        Assert.Equal(statement, (string?)call.Arguments["variants"]![0]!["sql"]);
        Assert.Contains(statement, fx.Extractor.AnalyzedTexts); // 채운 원문도 다른 변형과 같은 검사를 거쳤다
        Assert.True(call.MayExecuteSql);
    }

    [Fact]
    public async Task The_filled_request_statement_goes_through_the_same_statement_checks_as_other_variants()
    {
        var fx = new GateFixture();

        // DB 링크가 든 원문은 다른 변형처럼 실행하지 않고 커서 실측으로 대체하게 표시한다.
        var linked = await Call(fx, Session(statement: "SELECT * FROM TB_ORDER@remote"), AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"original"}]}""");
        Assert.False(linked.Result.IsError, linked.Result.Text);
        Assert.Equal(["db_link"], Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls).Substitutions!);

        // SELECT 가 아닌 원문·문장이 없는 요청은 거부한다.
        AssertDenied(await Call(fx, Session(statement: "UPDATE TB_ORDER SET stat_cd = 'B'"), AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"original"}]}"""), "sql_not_single_query");
        AssertDenied(await Call(fx, Session(statement: null), AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"original"}]}"""), "no_statement");
        Assert.Single(fx.Handlers[AgentToolNames.BenchmarkQueries].Calls);
    }

    [Fact]
    public async Task App_written_tool_errors_reach_the_model_at_query_level_while_other_errors_stay_generic()
    {
        var fx = new GateFixture();
        var session = Session(Dev(AiAccessLevel.Query), AiAccessLevel.Query);
        const string variants = """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""";

        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = _ =>
            AgentToolOutput.Error("'a' 문장을 실행할 수 없습니다: 바인드 :plant_cd 값이 없어 실행하지 않습니다.") with { AppWritten = true };
        var app = await Call(fx, session, AgentToolNames.BenchmarkQueries, variants);
        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = _ => AgentToolOutput.Error("조회에 실패했습니다 (ORA-00942). host db01:1521");
        var other = await Call(fx, session, AgentToolNames.BenchmarkQueries, variants);

        Assert.Contains(":plant_cd", (string?)JsonNode.Parse(app.Result.Text)!["error"], StringComparison.Ordinal);
        Assert.Equal("ORA-00942: The tool failed; details are available in the Oranda log.", (string?)JsonNode.Parse(other.Result.Text)!["error"]);
    }

    [Fact]
    public async Task Tool_failures_are_written_to_the_log_with_literals_and_bind_values_redacted()
    {
        var vault = new LiteralVault();
        vault.Add("'SECRET-LIT'");
        var line = await LoggedFailureAsync(
            vault,
            new BindValue(BindDataType.Varchar2, "ABC"),
            "OracleException: ORA-01722: invalid number near 'SECRET-LIT' / ORA-20001: 잘못된 값: ABC입니다 / ABCD");

        Assert.Contains("ORA-01722", line, StringComparison.Ordinal);
        Assert.Contains("invalid number", line, StringComparison.Ordinal);
        Assert.Contains("ABCD", line, StringComparison.Ordinal); // 값과 이어진 영문은 다른 낱말이다
        Assert.DoesNotContain("SECRET-LIT", line, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC입니다", line, StringComparison.Ordinal); // 한글이 바로 붙은 값도 가린다
    }

    [Fact]
    public async Task Without_a_vault_quoted_text_is_dropped_and_unmatchable_bind_values_leave_only_error_numbers()
    {
        var quoted = await LoggedFailureAsync(null, new BindValue(BindDataType.Number, 0.5m), "ORA-01722: invalid number 'LIT-2' near .5 and 0.5");
        var dated = await LoggedFailureAsync(null, new BindValue(BindDataType.Date, new DateTime(2026, 10, 30)), "ORA-01861: literal does not match: 30-OCT-26");

        Assert.DoesNotContain("LIT-2", quoted, StringComparison.Ordinal);
        Assert.DoesNotContain(".5", quoted, StringComparison.Ordinal);
        Assert.Contains("ORA-01722", quoted, StringComparison.Ordinal);
        Assert.Contains("ORA-01861", dated, StringComparison.Ordinal);
        Assert.DoesNotContain("30-OCT-26", dated, StringComparison.Ordinal);
    }

    /// <summary>처리자가 원인 원문(<paramref name="detail"/>)과 함께 실패하게 하고, 그 실행 id 로 남은 로그 한 줄을 돌려준다.</summary>
    private static async Task<string> LoggedFailureAsync(LiteralVault? vault, BindValue bind, string detail)
    {
        var fx = new GateFixture();
        var profile = Dev(AiAccessLevel.Query);
        var runId = "run-log-" + Guid.NewGuid().ToString("N");
        var ctx = new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query, StatementText: Statement);
        var session = new AgentSession(new AgentRunScope(runId, profile, ctx)
        {
            Literals = vault,
            Binds = new Dictionary<string, BindValue> { ["WORD_DH"] = bind },
        });
        fx.Handlers[AgentToolNames.BenchmarkQueries].Respond = _ =>
            AgentToolOutput.Error("'a' 문장 실행이 실패했습니다.") with { AppWritten = true, LogDetail = detail };
        using var listener = new CapturingListener();
        Trace.Listeners.Add(listener);
        try
        {
            await Call(fx, session, AgentToolNames.BenchmarkQueries, """{"variants":[{"label":"a","sql":"SELECT * FROM TB_ORDER"}]}""");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        return Assert.Single(listener.Lines, l => l.Contains(runId, StringComparison.Ordinal));
    }

    [Fact]
    public void Run_scope_binds_are_a_case_insensitive_copy_of_in_binds()
    {
        var given = new Dictionary<string, BindValue>
        {
            ["WORD_DH"] = new BindValue(BindDataType.Varchar2, "1"),
            ["OUT_X"] = new BindValue(BindDataType.Varchar2, null, BindDirection.Out),
        };
        var profile = Dev();
        var scope = new AgentRunScope("r", profile, new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query)) { Binds = given };
        given["LATE"] = new BindValue(BindDataType.Varchar2, "2");

        Assert.True(scope.Binds.ContainsKey("word_dh"));
        Assert.False(scope.Binds.ContainsKey("OUT_X"));
        Assert.False(scope.Binds.ContainsKey("LATE"));
        Assert.Empty(new AgentRunScope("r", profile, new AiContext(profile.Name, profile.Environment, AiAccessLevel.Query)).Binds);
    }

    private sealed class CapturingListener : TraceListener
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
        }

        public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        {
            lock (_lines)
            {
                _lines.Add(args is null || format is null ? format ?? string.Empty : string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
            }
        }
    }
}
