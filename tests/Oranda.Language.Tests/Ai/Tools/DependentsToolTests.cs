using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Tools;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Tools;

// 소유: TSK-15-09. get_dependents: 갈래별 나눔·수·줄 상한·처리자 오류.
public class DependentsToolTests
{
    private static readonly AgentObjectRef Order = new("APP", "TB_ORDER", "TABLE");

    private sealed class Tuning(Func<IReadOnlyList<ObjectDependentRow>> rows) : ITuningService
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ObjectDependentRow>> GetObjectDependentsAsync(ISession s, string owner, string name, CancellationToken ct)
        {
            Calls++;
            Assert.Equal(("APP", "TB_ORDER"), (owner, name));
            return Task.FromResult(rows());
        }
    }

    private sealed class NoKeys : ITableKeyDetailProvider
    {
        public Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct) => throw new NotSupportedException();

        public Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoPlans : IExecutionPlanService
    {
        public Task<ExecutionPlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct) => throw new NotSupportedException();

        public Task<ExecutionPlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ObjectDependentRow Row(ObjectDependentKind kind, string owner, string name, string? detail = null, string? status = null) =>
        new(kind, owner, name, detail, status);

    private static AgentToolInvocation Invoke(AiAccessLevel level, params (string Arg, AgentObjectRef Obj)[] resolved)
    {
        var profile = TestData.Profile(ConnectionEnvironment.Development);
        var run = new AgentRunScope("run1", profile, new AiContext("test", ConnectionEnvironment.Development, level));
        return new AgentToolInvocation(
            new AgentSession(run), new FakeSession(profile), level, new JsonObject { ["object"] = "TB_ORDER" }, resolved.ToDictionary(r => r.Arg, r => r.Obj),
            ExplainPlanAllowed: false, MayExecuteSql: false);
    }

    private static GetDependentsHandler Handler(Tuning tuning) =>
        new(new AgentToolboxProvider(tuning, new FakeMetadataProvider(), new NoKeys(), new NoPlans(), new LanguageService()));

    [Fact]
    public async Task Handler_groups_rows_by_kind_and_counts_public_synonyms()
    {
        var tuning = new Tuning(() =>
        [
            Row(ObjectDependentKind.Dependent, "APP", "V_ORDER", "VIEW", "VALID"),
            Row(ObjectDependentKind.Dependent, "APP", "PKG_ORDER", "PACKAGE BODY", "INVALID"),
            Row(ObjectDependentKind.ForeignKey, "APP", "TB_ORDER_ITEM", "FK_ITEM_ORDER", "ENABLED"),
            Row(ObjectDependentKind.Trigger, "APP", "TRG_ORDER_AUD", "INSERT OR UPDATE", "ENABLED"),
            Row(ObjectDependentKind.Synonym, "PUBLIC", "TB_ORDER"),
            Row(ObjectDependentKind.Synonym, "RPT", "ORDERS"),
        ]);

        var output = await Handler(tuning).ExecuteAsync(Invoke(AiAccessLevel.Metadata, ("object", Order)), CancellationToken.None);

        Assert.False(output.IsError, output.ErrorMessage);
        var r = output.Result!;
        Assert.Equal("APP.TB_ORDER", r["target"]!.GetValue<string>());
        Assert.Equal(2, r["invalidated_count"]!.GetValue<int>());
        Assert.Equal(["APP.V_ORDER", "APP.PKG_ORDER"], r["invalidated"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()));
        Assert.Equal("INVALID", r["invalidated"]![1]!["status"]!.GetValue<string>());
        Assert.Equal("FK_ITEM_ORDER", r["foreign_keys"]![0]!["constraint"]!.GetValue<string>());
        Assert.Equal("APP.TB_ORDER_ITEM", r["foreign_keys"]![0]!["table"]!.GetValue<string>());
        Assert.Equal(1, r["trigger_count"]!.GetValue<int>());
        Assert.Equal("INSERT OR UPDATE", r["triggers"]![0]!["event"]!.GetValue<string>());
        Assert.Equal(2, r["synonym_count"]!.GetValue<int>());
        Assert.Equal(1, r["public_synonym_count"]!.GetValue<int>());
        Assert.False(r["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Lists_are_capped_but_counts_are_complete_and_duplicates_count_once()
    {
        var rows = Enumerable.Range(1, TuningToolbox.MaxDependentItems + 5)
            .Select(i => Row(ObjectDependentKind.Dependent, "APP", "V_" + i, "VIEW", "VALID"))
            .Append(Row(ObjectDependentKind.Dependent, "APP", "V_1", "VIEW", "VALID"))
            .ToList();

        var d = TuningToolbox.DescribeDependents("APP.TB_ORDER", rows);

        Assert.Equal(TuningToolbox.MaxDependentItems + 5, d.InvalidatedCount);
        Assert.Equal(TuningToolbox.MaxDependentItems, d.Invalidated.Count);
        Assert.True(d.Truncated);
        Assert.Equal(0, d.ForeignKeyCount);
        Assert.Empty(d.Triggers);
    }

    [Fact]
    public async Task Nothing_depends_gives_zero_counts_and_the_answer_is_cached_within_a_run()
    {
        var tuning = new Tuning(() => []);
        var handler = Handler(tuning);
        var invocation = Invoke(AiAccessLevel.Metadata, ("object", Order));

        var first = await handler.ExecuteAsync(invocation, CancellationToken.None);
        var second = await handler.ExecuteAsync(invocation, CancellationToken.None);

        Assert.Equal(0, first.Result!["invalidated_count"]!.GetValue<int>());
        Assert.Equal(0, first.Result!["synonym_count"]!.GetValue<int>());
        Assert.Equal(1, tuning.Calls); // 같은 실행 안에서는 다시 읽지 않는다
        Assert.False(second.IsError);
    }

    [Fact]
    public async Task Unresolved_object_or_failing_query_is_an_error_not_an_exception()
    {
        var unresolved = await Handler(new Tuning(() => [])).ExecuteAsync(Invoke(AiAccessLevel.Metadata), CancellationToken.None);
        Assert.True(unresolved.IsError);

        var failing = await Handler(new Tuning(() => throw new InvalidOperationException("ORA-00942: secret detail"))).ExecuteAsync(Invoke(AiAccessLevel.Metadata, ("object", Order)), CancellationToken.None);
        Assert.True(failing.IsError);
        Assert.DoesNotContain("secret", failing.ErrorMessage, StringComparison.Ordinal);
    }
}
