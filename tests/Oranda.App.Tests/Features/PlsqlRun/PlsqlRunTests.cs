using Oranda.App.Features.PlsqlRun;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.PlsqlRun;

/// <summary>TSK-08-02 PL/SQL 실행 대화상자 시험(ViewModel·명령 처리기·등록). DB 없이 가짜 실행 엔진·메타데이터로 돈다.</summary>
public class PlsqlRunTests
{
    private static readonly DbObjectRef Proc = new("SCOTT", "RAISE_SAL", "PROCEDURE");
    private static readonly DbObjectRef Pkg = new("SCOTT", "EMP_PKG", "PACKAGE");

    private static ArgumentInfo Arg(string name, int pos, string type, ArgumentDirection dir = ArgumentDirection.In, bool def = false)
        => new(name, pos, type, dir) { HasDefault = def };

    private sealed class Rig
    {
        public FakeSession Session { get; } = new(new ConnectionProfile("t", "SCOTT", "localhost/FREEPDB1"));

        public Meta Meta { get; } = new();

        public FakeExecution Exec { get; } = new();

        public List<string> Sent { get; } = [];

        public PlsqlRunViewModel Create() => new(Deps());

        public PlsqlRunDeps Deps() => new()
        {
            Session = () => Session,
            Metadata = () => Meta,
            Execution = () => Exec,
            SendToEditor = Sent.Add,
        };
    }

    private static Rig WithProcedure()
    {
        var rig = new Rig();
        rig.Meta.Procedures[Proc] =
        [
            new ProcedureInfo("RAISE_SAL", false,
            [
                Arg("P_ID", 1, "NUMBER"),
                Arg("P_PCT", 2, "NUMBER", def: true),
                Arg("P_MSG", 3, "VARCHAR2", ArgumentDirection.Out),
                Arg("P_RC", 4, "REF CURSOR", ArgumentDirection.Out),
            ]),
        ];
        return rig;
    }

    [Fact]
    public void Commands_are_registered()
    {
        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in PlsqlRunCommands.Ids)
        {
            Assert.NotNull(reg.Find(id));
        }
    }

    [Fact]
    public async Task Open_builds_rows_and_preview_from_metadata()
    {
        var rig = WithProcedure();
        var vm = rig.Create();

        await vm.OpenAsync(Proc);

        Assert.Equal(["P_ID", "P_PCT", "P_MSG", "P_RC"], vm.Rows.Select(r => r.Name));
        Assert.Contains("SCOTT.RAISE_SAL(", vm.Preview, StringComparison.Ordinal);
        Assert.Contains("P_ID => :P_P_ID", vm.Preview, StringComparison.Ordinal);
        Assert.DoesNotContain("P_PCT =>", vm.Preview, StringComparison.Ordinal); // 기본값 있는 인자는 처음에 뺀다
        Assert.False(vm.HasMultipleCandidates);
        Assert.True(vm.CanRun);

        var pct = vm.Rows[1];
        Assert.True(pct.UseDefault);
        pct.UseDefault = false;
        pct.Text = "10";
        Assert.Contains("P_PCT => :P_P_PCT", vm.Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_sends_binds_and_shows_out_values_cursor_and_dbms_output()
    {
        var rig = WithProcedure();
        rig.Exec.Handler = (stmt, _) => Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded)
        {
            DbmsOutput = ["hello", "세계"],
            OutValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["P_P_MSG"] = "done",
                ["P_P_RC"] = new OneColumnResult("1", "2"),
            },
        });
        var vm = rig.Create();
        await vm.OpenAsync(Proc);
        vm.Rows[0].Text = "7369";

        var ok = await vm.RunAsync();

        Assert.True(ok);
        var stmt = Assert.Single(rig.Exec.Statements);
        var binds = rig.Exec.LastBinds!;
        Assert.Equal(StatementKind.PlSql, stmt.Kind);
        Assert.Equal(new BindValue(BindDataType.Number, 7369m, BindDirection.In), binds["P_P_ID"]);
        Assert.Equal(BindDirection.Out, binds["P_P_MSG"].Direction);
        Assert.Equal(BindDataType.RefCursor, binds["P_P_RC"].Type);
        Assert.Equal("done", vm.Rows[2].OutText);
        Assert.True(vm.HasOutValues);
        var tab = Assert.Single(vm.Results.Tabs);
        Assert.Equal(":P_RC", tab.Title);
        Assert.Equal("hello" + Environment.NewLine + "세계", vm.DbmsOutputText);
        Assert.False(vm.StatusIsError);
        Assert.StartsWith("완료", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Function_return_and_boolean_out_are_shown()
    {
        var rig = new Rig();
        rig.Meta.Procedures[new DbObjectRef("SCOTT", "IS_OK", "FUNCTION")] =
        [
            new ProcedureInfo("IS_OK", true, [Arg("P_FLAG", 1, "PL/SQL BOOLEAN", ArgumentDirection.InOut)]) { ReturnType = "PL/SQL BOOLEAN" },
        ];
        rig.Exec.Handler = (stmt, _) => Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded)
        {
            OutValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["RET"] = 1m, ["P_P_FLAG"] = 0m },
        });
        var vm = rig.Create();
        await vm.OpenAsync(new DbObjectRef("SCOTT", "IS_OK", "FUNCTION"));
        vm.Rows.Single(r => r.Name == "P_FLAG").Text = "true";

        Assert.True(await vm.RunAsync());

        Assert.Equal("TRUE", vm.Rows[0].OutText);
        Assert.Equal("FALSE", vm.Rows[1].OutText);
        Assert.StartsWith("DECLARE", Assert.Single(rig.Exec.Statements).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_and_denial_are_reported_without_out_values()
    {
        var rig = WithProcedure();
        rig.Exec.Handler = (stmt, _) => Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = new DbError("ORA-06550", "line 1") { Number = 6550 } });
        var vm = rig.Create();
        await vm.OpenAsync(Proc);
        vm.Rows[0].Text = "1";

        Assert.False(await vm.RunAsync());
        Assert.True(vm.StatusIsError);
        Assert.Contains("ORA-06550", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.HasOutValues);

        rig.Exec.Handler = (stmt, _) => Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Denied) { Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.ReadOnlyProfile, "읽기 전용") });
        Assert.False(await vm.RunAsync());
        Assert.Contains("읽기 전용", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreadable_value_blocks_run_and_never_reaches_engine()
    {
        var rig = WithProcedure();
        var vm = rig.Create();
        await vm.OpenAsync(Proc);
        vm.Rows[0].Text = "abc";

        Assert.False(vm.CanRun);
        Assert.Contains("P_ID", vm.ProblemText, StringComparison.Ordinal);
        Assert.False(await vm.RunAsync());
        Assert.Empty(rig.Exec.Statements);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Package_members_and_overloads_are_candidates()
    {
        var rig = new Rig();
        rig.Meta.Procedures[Pkg] =
        [
            new ProcedureInfo("HIRE", false, [Arg("P_NAME", 1, "VARCHAR2")]) { Overload = 1 },
            new ProcedureInfo("HIRE", false, [Arg("P_ID", 1, "NUMBER"), Arg("P_NAME", 2, "VARCHAR2")]) { Overload = 2 },
            new ProcedureInfo("FIRE", false, [Arg("P_ID", 1, "NUMBER")]),
        ];
        var vm = rig.Create();

        await vm.OpenAsync(Pkg);

        Assert.True(vm.HasMultipleCandidates);
        Assert.Equal(3, vm.Candidates.Count);
        Assert.Equal("FIRE", vm.SelectedCandidate!.Procedure.Name);
        vm.SelectedCandidate = vm.Candidates[2];
        Assert.Contains("SCOTT.EMP_PKG.HIRE(", vm.Preview, StringComparison.Ordinal);
        Assert.Equal(2, vm.Rows.Count);

        var one = rig.Create();
        await one.OpenAsync(Pkg, "fire");
        Assert.Single(one.Candidates);
    }

    [Fact]
    public async Task Missing_metadata_is_reported()
    {
        var rig = new Rig();
        var vm = rig.Create();

        await vm.OpenAsync(Proc);

        Assert.True(vm.StatusIsError);
        Assert.Empty(vm.Rows);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task Send_to_editor_keeps_binds_and_lists_values_as_comments()
    {
        var rig = WithProcedure();
        var vm = rig.Create();
        await vm.OpenAsync(Proc);
        vm.Rows[0].Text = "42";

        Assert.True(vm.SendToEditor());

        var text = Assert.Single(rig.Sent);
        Assert.Contains("-- :P_P_ID (NUMBER) = 42", text, StringComparison.Ordinal);
        Assert.Contains("BEGIN", text, StringComparison.Ordinal);
        Assert.EndsWith("END;" + Environment.NewLine + "/", text.ReplaceLineEndings(Environment.NewLine), StringComparison.Ordinal);
        Assert.Empty(rig.Exec.Statements);
    }

    [Fact]
    public async Task Handler_opens_dialog_for_runnable_targets_only()
    {
        var rig = WithProcedure();
        var opened = new List<PlsqlRunViewModel>();
        var handler = new PlsqlRunCommandHandler { Deps = rig.Deps(), Presenter = opened.Add };

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.runFromBrowser", FakeContextKeys.AllTrue, new DbObjectRef("SCOTT", "EMP", "TABLE")), default);
        Assert.Empty(opened);

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.runFromBrowser", FakeContextKeys.AllTrue, Proc), default);
        var vm = Assert.Single(opened);
        Assert.Equal(4, vm.Rows.Count);

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.run", FakeContextKeys.AllTrue, new PlsqlRunRequest(Proc)), default);
        Assert.Equal(2, opened.Count);

        await handler.ExecuteAsync(new CommandContext("oranda.plsql.run", FakeContextKeys.AllTrue), default);
        Assert.Equal(2, opened.Count); // 대상을 모르면 아무것도 하지 않는다
    }

    [Theory]
    [InlineData("PROCEDURE", true)]
    [InlineData("function", true)]
    [InlineData("PACKAGE", true)]
    [InlineData("TABLE", false)]
    [InlineData(null, false)]
    public void Only_callable_object_types_are_runnable(string? type, bool expected)
        => Assert.Equal(expected, PlsqlRunCommands.IsRunnableType(type));

    private sealed class Meta : IMetadataProvider
    {
        public Dictionary<DbObjectRef, IReadOnlyList<ProcedureInfo>> Procedures { get; } = [];

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult(Procedures.TryGetValue(obj, out var list) ? list : []);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
