using Oranda.App.Features.Binds;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.DataTransfer;

/// <summary>
/// 서버에서 다시 읽는 내보내기(·행 수 세기·거르기)가 결과를 만들 때 쓴 바인드 값을 그대로 넘기는지(TSK-12-03).
/// 예전에는 내보내기가 빈 바인드로 질의를 다시 실행해 「바인드 변수 :ID 의 값이 없습니다」 로 실패했다.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ExportBindsTests : IDisposable
{
    public ExportBindsTests() => Reset();

    public void Dispose() => Reset();

    private static void Reset()
    {
        ResultGridCommands.Active = null;
        ResultGridCommands.Filter = null;
        ResultGridCommands.CountFiltered = null;
        ResultGridCommands.CountRows = null;
    }

    private static SqlStatement Stmt(string text, params string[] names) =>
        new(text, default, StatementKind.Query) { Verb = "SELECT", BindNames = names };

    private static BindValue Num(decimal v) => new(BindDataType.Number, v);

    // ---- 값 정하기 --------------------------------------------------------------------------

    [Fact]
    public void Resolve_prefers_the_values_the_result_was_made_with_then_the_bind_list_and_skips_names_not_in_the_statement()
    {
        var list = new BindVariableStore();
        list.Add("ID").Text = "99"; // 목록 값은 실행 뒤 바뀌었다
        list.Add("NM").Text = "B%";
        var ran = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = Num(7),
            ["oranda_f1"] = new(BindDataType.Varchar2, "SMITH"), // 셀 값 거르기 바인드: 원래 문장에는 없다
        };

        var r = ExportBinds.Resolve(Stmt("select * from t where id = :id and nm like :nm and id2 = :ID", "id", "nm", "ID"), ran, list);

        Assert.Null(r.Problem);
        Assert.Equal(2, r.Binds.Count); // 같은 이름 반복은 하나
        Assert.Equal(7m, r.Binds["ID"].Value); // 실행 때 값
        Assert.Equal("B%", r.Binds["nm"].Value); // 스냅숏에 없는 이름은 목록 값
        Assert.DoesNotContain("oranda_f1", r.Binds.Keys);
    }

    [Fact]
    public void Resolve_without_a_value_gives_an_actionable_reason_instead_of_a_server_error()
    {
        var none = ExportBinds.Resolve(Stmt("select 1 from t where a = :A and b = :b", "A", "b"), null, new BindVariableStore());
        Assert.Contains(":A", none.Problem, StringComparison.Ordinal);
        Assert.Contains(":b", none.Problem, StringComparison.Ordinal);
        Assert.Contains("바인드 변수 창에서 값을 넣고 다시 실행하세요", none.Problem, StringComparison.Ordinal);

        var list = new BindVariableStore();
        var row = list.Add("N");
        row.Type = BindDataType.Number;
        row.Text = "abc"; // 형식에 맞지 않는 값
        var bad = ExportBinds.Resolve(Stmt("select 1 from t where n = :N", "N"), null, list);
        Assert.Contains(":N", bad.Problem, StringComparison.Ordinal);
        Assert.Contains("읽을 수 없습니다", bad.Problem, StringComparison.Ordinal);

        Assert.Null(ExportBinds.Resolve(Stmt("select 1 from dual"), null, null).Problem); // 바인드 없는 문장
    }

    // ---- 실행이 결과 탭에 값을 남긴다 ------------------------------------------------------------

    [Fact]
    public Task A_run_leaves_its_bind_values_on_the_result_and_the_export_source_uses_them_even_after_the_list_changes() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        var tab = rig.Host.NewDocument("select * from t where id = :id and nm like :nm and d = :d and (z is null or z = :z)");
        tab.Select(0, 0);
        var id = tab.Binds.Add("ID");
        id.Type = BindDataType.Number;
        id.Text = "7";
        tab.Binds.Add("NM").Text = "A%";
        var d = tab.Binds.Add("D");
        d.Type = BindDataType.Date;
        d.Text = "2026-01-02";
        tab.Binds.Add("Z"); // 빈 값 = NULL

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));

        var pane = ((ResultTabsViewModel)tab.ResultPane!).Selected!.Pane!;
        Assert.Equal(4, pane.RunBinds!.Count);

        // 실행 뒤 목록을 바꾸고 지워도 내보내기는 결과를 만든 값을 쓴다.
        tab.Binds.Find("ID")!.Text = "99";
        tab.Binds.Clear();
        var source = DataTransferCommandHandler.Build(tab, null, rig.Session)!;

        Assert.Null(source.BindProblem);
        Assert.Equal(7m, source.Binds["id"].Value);
        Assert.Equal(BindDataType.Number, source.Binds["id"].Type);
        Assert.Equal("A%", source.Binds["nm"].Value);
        Assert.Equal(BindDataType.Date, source.Binds["d"].Type);
        Assert.Equal(new DateTime(2026, 1, 2), source.Binds["d"].Value);
        Assert.Null(source.Binds["z"].Value); // NULL 은 NULL 로
        Assert.Equal(rig.Exec.Statements[0].Text, source.Query.Text);
    });

    [Fact]
    public Task Positional_binds_and_a_name_used_twice_are_kept() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        var tab = rig.Host.NewDocument("select * from t where a = :1 and b = :2 and c = :1");
        tab.Select(0, 0);
        var one = tab.Binds.Add("1");
        one.Type = BindDataType.Number;
        one.Text = "5";
        tab.Binds.Add("2").Text = "x";

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));
        tab.Binds.Clear();
        var source = DataTransferCommandHandler.Build(tab, null, rig.Session)!;

        Assert.Null(source.BindProblem);
        Assert.Equal(2, source.Binds.Count);
        Assert.Equal(5m, source.Binds["1"].Value);
        Assert.Equal("x", source.Binds["2"].Value);
    });

    [Fact]
    public Task A_script_gives_each_result_only_its_own_statement_binds() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        var tab = rig.Host.NewDocument("select :a from dual;\nselect :b from dual;");
        tab.Binds.Add("A").Text = "1";
        tab.Binds.Add("B").Text = "2";

        Assert.True(await rig.Controller.RunAsync(RunMode.Script));

        var tabs = (ResultTabsViewModel)tab.ResultPane!;
        Assert.Equal(["A"], tabs.Tabs[0].Pane!.RunBinds!.Keys.Select(k => k.ToUpperInvariant()).ToArray());
        Assert.Equal(["B"], tabs.Tabs[1].Pane!.RunBinds!.Keys.Select(k => k.ToUpperInvariant()).ToArray());
    });

    [Fact]
    public Task A_statement_without_binds_keeps_no_snapshot() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        rig.Exec.Handler = (s, _) => Task.FromResult(Rig.Query(s));
        var tab = rig.Host.NewDocument("select 1 from dual");
        tab.Select(0, 0);

        Assert.True(await rig.Controller.RunAsync(RunMode.Statement));

        Assert.Null(((ResultTabsViewModel)tab.ResultPane!).Selected!.Pane!.RunBinds);
        var source = DataTransferCommandHandler.Build(tab, null, rig.Session)!;
        Assert.Empty(source.Binds);
        Assert.Null(source.BindProblem);
    });

    [Fact]
    public Task Without_a_result_the_statement_at_the_caret_uses_the_bind_list_or_names_what_is_missing() => Ui.Run(() =>
    {
        using var rig = new Rig();
        var tab = rig.Host.NewDocument("select * from emp where id = :id");
        var caret = Stmt("select * from emp where id = :id", "id");

        var missing = DataTransferCommandHandler.Build(tab, caret, rig.Session)!;
        Assert.Contains(":id", missing.BindProblem, StringComparison.Ordinal);

        var row = tab.Binds.Add("ID");
        row.Type = BindDataType.Number;
        row.Text = "5";
        var ok = DataTransferCommandHandler.Build(tab, caret, rig.Session)!;
        Assert.Null(ok.BindProblem);
        Assert.Equal(5m, ok.Binds["id"].Value);

        Assert.Null(DataTransferCommandHandler.Build(tab, null, rig.Session)); // 문장을 모르면 없다
        Assert.Null(DataTransferCommandHandler.Build(tab, caret, null)); // 세션을 모르면 없다
    });

    // ---- 같은 종류의 서버 재조회: 행 수 세기·거르기·서버 정렬 -----------------------------------------

    [Fact]
    public void Count_and_filter_use_the_run_values_only_for_names_missing_from_the_bind_list()
    {
        var store = new BindVariableStore();
        var d = store.Add("D");
        d.Type = BindDataType.Number;
        d.Text = "10";
        var ran = new Dictionary<string, BindValue> { ["d"] = Num(1), ["x"] = Num(2) };
        var cell = new Dictionary<string, BindValue> { ["oranda_f1"] = new(BindDataType.Varchar2, "S") };

        var v = GridRowCounter.BuildBinds(store, ["d", "x", "oranda_f1"], cell, ran);

        Assert.Equal(10m, v["d"].Value); // 목록에 있으면 지금 값(기존 동작)
        Assert.Equal(2m, v["x"].Value); // 목록에 없는 변수는 실행 때 값(MyBatis 시나리오 값·지운 변수)
        Assert.Equal("S", v["oranda_f1"].Value);
        var gone = Assert.Throws<InvalidOperationException>(() => GridRowCounter.BuildBinds(store, ["nope"], null, ran));
        Assert.Contains(":nope", gone.Message, StringComparison.Ordinal);
    }

    [Fact]
    public Task Count_and_filter_requests_carry_the_run_values() => Ui.Run(async () =>
    {
        var query = Stmt("select empno from emp where deptno = :d", "d");
        var tabs = new ResultTabsViewModel();
        var pane = tabs.AddQuery(new OneColumnResult("1"), "결과 1", query, runBinds: new Dictionary<string, BindValue> { ["d"] = Num(20) }).Pane!;
        await pane.Model!.LoadFirstPageAsync();
        var asked = new List<ResultFilterRequest>();
        ResultGridCommands.CountFiltered = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<long?>(3);
        };
        ResultGridCommands.CountRows = (_, _) => throw new InvalidOperationException("목록으로 세면 d 가 없다");

        await pane.CountAllRowsAsync();

        var counted = Assert.Single(asked);
        Assert.Equal(20m, counted.RunBinds!["d"].Value);
        Assert.Equal(query, counted.Query);

        ResultGridCommands.Filter = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult<IResultSet>(new OneColumnResult("1"));
        };
        pane.WhereText = "1 = 1";
        await pane.ApplyFilterAsync();

        Assert.Equal(20m, asked[^1].RunBinds!["d"].Value);
        Assert.Equal("1 = 1", asked[^1].Where);
        tabs.Clear();
    });
}
