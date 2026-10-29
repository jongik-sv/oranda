using System.Runtime.CompilerServices;
using Oranda.App.Features.DbTools;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.DbTools;

/// <summary>TSK-07-04 객체 관리·검색·DB 정보·DDL 일괄 추출 시험(ViewModel·명령 처리기·등록).</summary>
public class DbToolsTests
{
    private static readonly DbObjectRef BadProc = new("SCOTT", "BAD_P", "PROCEDURE");
    private static readonly DbObjectRef BadView = new("SCOTT", "BAD_V", "VIEW");

    [Fact]
    public void Frame_exists()
    {
        Assert.NotNull(new DbInfoViewModel());
    }

    [Fact]
    public Task Parameter_ddl_preview_is_a_colored_read_only_code_view() => Oranda.App.Tests.Shell.Headless.HeadlessHost.Run(() =>
    {
        var vm = new DbInfoViewModel();
        vm.PickParameter("processes", "300");
        vm.ParameterValue = "400";
        var view = new DbInfoView { DataContext = vm };
        var window = new Avalonia.Controls.Window { Content = view, Width = 900, Height = 500 };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var ddl = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Oranda.App.Features.SqlEditor.SqlCodeView>().Single(c => c.Name == "DdlPreview");
        Assert.True(ddl.TextEditorControl.IsReadOnly);
        Assert.False(ddl.TextEditorControl.ShowLineNumbers);
        Assert.NotNull(ddl.Colorizer);
        Assert.StartsWith("ALTER SYSTEM SET processes = 400", ddl.TextEditorControl.Text, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public void Panels_and_commands_are_registered()
    {
        foreach (var id in new[] { "oranda.panel.dbInfo", "oranda.panel.objectSearch", "oranda.panel.ddlExtract" })
        {
            Assert.Equal(ToolPanelKind.Document, ShellTestHelpers.RequirePanel(id).Kind);
        }

        var reg = ShellTestHelpers.CreateCommandRegistry();
        foreach (var id in new[]
        {
            "oranda.schema.compileInvalid", "oranda.schema.recyclebinPurge", "oranda.schema.recyclebinFlashback",
            "oranda.schema.objectSearch", "oranda.db.info", "oranda.db.ddlExtract",
        })
        {
            Assert.NotNull(reg.Find(id));
        }
    }

    [Fact]
    public void Default_type_list_matches_exporter_order()
        => Assert.Equal(Oranda.Oracle.Schema.DdlBulkExporter.DefaultTypes, DdlExtractViewModel.DefaultTypes);

    // ---- DB 정보 ----

    [Fact]
    public async Task Db_info_load_fills_sections_and_filter_narrows_text()
    {
        var vm = new DbInfoViewModel(new FakeInfo());

        await vm.LoadAsync(new FakeSession());

        Assert.Equal(["인스턴스", "파라미터"], vm.Sections.Select(s => s.Title));
        Assert.Equal("인스턴스", vm.SelectedSection?.Title);
        vm.SelectedSection = vm.Sections[1];
        Assert.Contains("processes", vm.SectionText);
        Assert.Contains("sga_target", vm.SectionText);

        vm.FilterText = "SGA";
        Assert.DoesNotContain("processes", vm.SectionText);
        Assert.Contains("sga_target", vm.SectionText);
    }

    [Fact]
    public async Task Db_info_reports_sections_that_could_not_be_read()
    {
        var vm = new DbInfoViewModel(new FakeInfo { WithError = true });

        await vm.LoadAsync(new FakeSession());

        Assert.Contains("1개는 권한 등으로 읽지 못했다", vm.StatusText);
    }

    [Fact]
    public async Task Db_info_failure_is_shown_not_thrown()
    {
        var vm = new DbInfoViewModel(new FakeInfo { Fails = true });

        await vm.LoadAsync(new FakeSession());

        Assert.Contains("DB 정보를 읽지 못했다", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public void Parameter_change_ddl_preview_updates_and_rejects_bad_names()
    {
        var vm = new DbInfoViewModel();

        vm.ParameterName = "processes";
        vm.ParameterValue = "400";
        Assert.Equal("ALTER SYSTEM SET processes = 400 SCOPE = BOTH;", vm.PreviewDdl);

        vm.ParameterScope = DbParameterScope.Spfile;
        Assert.EndsWith("SCOPE = SPFILE;", vm.PreviewDdl);

        vm.ParameterName = "x; DROP USER y";
        Assert.Equal(string.Empty, vm.PreviewDdl);
        Assert.NotEqual(string.Empty, vm.PreviewError);

        vm.ParameterName = string.Empty;
        Assert.Equal(string.Empty, vm.PreviewError);
    }

    // ---- 객체 검색 ----

    [Fact]
    public void Search_query_converts_filters()
    {
        var vm = new ObjectSearchViewModel(new FakeAdmin(), new FakeExec())
        {
            Owner = " scott ",
            NameText = "emp*",
            InvalidOnly = true,
            CreatedAfter = new DateTime(2026, 1, 2, 13, 0, 0),
            MinSizeKb = 2,
            MaxSizeKb = 10,
        };
        vm.Types.SelectOnly(["TABLE"]);

        var q = vm.BuildQuery();

        Assert.Equal("SCOTT", q.Owner);
        Assert.Equal("EMP%", q.NamePattern);
        Assert.Equal(["TABLE"], q.ObjectTypes);
        Assert.True(q.InvalidOnly);
        Assert.Equal(new DateTime(2026, 1, 2), q.CreatedAfter);
        Assert.Equal(2048, q.MinSizeBytes);
        Assert.Equal(10240, q.MaxSizeBytes);
    }

    [Fact]
    public async Task Newer_search_cancels_the_older_one_and_results_are_swapped_in_one_piece()
    {
        var admin = new FakeAdmin { Rows = [new ObjectSearchRow(new DbObjectRef("SCOTT", "OLD", "TABLE"), "VALID", null, null, null)], SearchGate = new TaskCompletionSource() };
        var vm = new ObjectSearchViewModel(admin, new FakeExec());
        vm.Attach(new FakeSession());
        var swaps = 0;
        vm.PropertyChanged += (_, e) => swaps += e.PropertyName == nameof(vm.Results) ? 1 : 0;

        var first = vm.SearchAsync(); // 앞 검색: 서버에서 도는 중
        admin.SearchGate = null;
        admin.Rows = [new ObjectSearchRow(new DbObjectRef("SCOTT", "NEW1", "VIEW"), "INVALID", null, null, null), new ObjectSearchRow(new DbObjectRef("SCOTT", "NEW2", "VIEW"), "INVALID", null, null, null)];
        await vm.SearchAsync(); // 뒤 검색
        await first; // 앞 검색은 취소로 끝난다

        Assert.Equal(["SCOTT.NEW1", "SCOTT.NEW2"], vm.Results.Select(r => r.Title));
        Assert.Equal(1, swaps); // 결과는 한 번에 바꿔 끼운다(줄마다 알리지 않는다)
        Assert.Equal("2개 찾음", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Invalid_search_lists_the_invalid_objects_of_the_owner()
    {
        var admin = new FakeAdmin { Invalid = [BadProc, BadView] };
        var vm = new ObjectSearchViewModel(admin, new FakeExec()) { InvalidOnly = true };
        vm.Attach(new FakeSession());

        await vm.SearchAsync();

        Assert.True(admin.LastQuery!.InvalidOnly);
        Assert.Equal(["SCOTT.BAD_P", "SCOTT.BAD_V"], vm.Results.Select(r => r.Title));
        Assert.All(vm.Results, r => Assert.Equal("INVALID", r.Status));
    }

    [Fact]
    public async Task Search_fills_results_and_selection_loads_references()
    {
        var admin = new FakeAdmin { Rows = [new ObjectSearchRow(new DbObjectRef("SCOTT", "EMP", "TABLE"), "VALID", new DateTime(2026, 1, 2), null, 8192)] };
        var vm = new ObjectSearchViewModel(admin, new FakeExec());
        vm.Attach(new FakeSession());

        await vm.SearchAsync();

        var item = Assert.Single(vm.Results);
        Assert.Equal("SCOTT.EMP", item.Title);
        Assert.Equal("8 KB", item.Size);
        Assert.Equal("SCOTT", admin.LastQuery?.Owner);
        Assert.Equal("1개 찾음", vm.StatusText);

        await vm.LoadReferencesAsync(item);
        vm.SelectedResult = item;
        await vm.LoadReferencesAsync(item);
        Assert.Contains("이 객체를 쓰는 것", vm.ReferencesText);
        Assert.Contains("VIEW SCOTT.EMP_V", vm.ReferencesText);
    }

    // ---- 소스 검색·열 이름 검색 ----

    [Fact]
    public void Source_query_carries_owner_type_text_and_options()
    {
        var vm = new ObjectSearchViewModel(new FakeAdmin(), new FakeExec())
        {
            Owner = " scott ",
            SourceText = "v_total",
            SourceTypeFilter = "PACKAGE BODY",
            SourceMatchCase = true,
            SourceWholeWord = true,
            SourceRegex = true,
        };

        var q = vm.BuildSourceQuery();

        Assert.Equal("SCOTT", q.Owner);
        Assert.Equal(["PACKAGE BODY"], q.ObjectTypes);
        Assert.Equal("v_total", q.Text);
        Assert.True(q.MatchCase && q.WholeWord && q.UseRegex);
        Assert.Equal(ObjectSearchViewModel.MaxHits, q.Limit);
        Assert.Empty(new ObjectSearchViewModel(new FakeAdmin(), new FakeExec()).BuildSourceQuery().ObjectTypes);
    }

    [Fact]
    public async Task Source_search_lists_lines_with_trimmed_text_and_says_when_capped()
    {
        var body = new DbObjectRef("SCOTT", "PK", "PACKAGE BODY");
        var admin = new FakeAdmin
        {
            SourceHits = new([new SourceSearchHit(body, 6, "    v_total := 0;  "), new SourceSearchHit(body, 9, "\tRETURN v_total;")], Capped: false),
        };
        var vm = new ObjectSearchViewModel(admin, new FakeExec()) { SourceText = "v_total" };
        vm.Attach(new FakeSession());

        await vm.SearchSourceAsync();

        Assert.Equal([6, 9], vm.SourceHits.Select(h => h.Line));
        Assert.Equal("SCOTT.PK", vm.SourceHits[0].Title);
        Assert.Equal("PACKAGE BODY", vm.SourceHits[0].ObjectType);
        Assert.Equal("v_total := 0;", vm.SourceHits[0].Text);
        Assert.Equal("RETURN v_total;", vm.SourceHits[1].Text);
        Assert.Equal("6", vm.SourceHits[0].LineText);
        Assert.Equal("2줄 찾음(1개 객체)", vm.StatusText);
        Assert.Equal("SCOTT", admin.LastSourceQuery?.Owner);
        Assert.False(vm.IsBusy);

        admin.SourceHits = admin.SourceHits with { Capped = true };
        await vm.SearchSourceAsync();
        Assert.StartsWith($"처음 {ObjectSearchViewModel.MaxHits}줄만 보인다", vm.StatusText);
        Assert.Contains("더 있다", vm.StatusText);
    }

    [Fact]
    public async Task Source_search_needs_text_and_shows_a_rejected_pattern_instead_of_failing()
    {
        var admin = new FakeAdmin
        {
            SourceHits = new([new SourceSearchHit(new DbObjectRef("SCOTT", "P", "PROCEDURE"), 1, "x")], Capped: false),
        };
        var vm = new ObjectSearchViewModel(admin, new FakeExec()) { SourceText = "   " };
        vm.Attach(new FakeSession());

        await vm.SearchSourceAsync();
        Assert.Equal("찾을 글자를 넣으세요", vm.StatusText);
        Assert.Null(admin.LastSourceQuery);

        vm.SourceText = "x";
        await vm.SearchSourceAsync();
        Assert.Single(vm.SourceHits);

        admin.SourceFailure = new SearchPatternException(SourceSearchPattern.Describe(12725));
        vm.SourceText = "(abc";
        vm.SourceRegex = true;
        await vm.SearchSourceAsync();

        Assert.Empty(vm.SourceHits);
        Assert.Equal("정규식이 올바르지 않다 — 괄호 ( ) 짝이 맞지 않는다 (ORA-12725)", vm.StatusText);
        Assert.False(vm.IsBusy);

        admin.SourceFailure = new InvalidOperationException("ORA-00942: table or view does not exist");
        await vm.SearchSourceAsync();
        Assert.StartsWith("소스를 검색하지 못했다: ORA-00942", vm.StatusText);
    }

    [Fact]
    public async Task Column_search_matches_name_case_insensitively_and_lists_type_and_nullability()
    {
        var admin = new FakeAdmin
        {
            ColumnHits = new(
            [
                new ColumnSearchHit(new DbObjectRef("SCOTT", "EMP", "TABLE"), "EMP_ID", "NUMBER(6)", false),
                new ColumnSearchHit(new DbObjectRef("SCOTT", "EMP_V", "VIEW"), "EMP_ID", "NUMBER(6)", true),
            ], Capped: false),
        };
        var vm = new ObjectSearchViewModel(admin, new FakeExec());
        vm.Attach(new FakeSession());

        await vm.SearchColumnsAsync();
        Assert.Equal("찾을 열 이름을 넣으세요", vm.StatusText);
        Assert.Null(admin.LastColumnQuery);

        vm.ColumnNameText = "emp_id";
        await vm.SearchColumnsAsync();

        Assert.Equal("%EMP\\_ID%", admin.LastColumnQuery?.NamePattern);
        Assert.Equal("SCOTT", admin.LastColumnQuery?.Owner);
        Assert.Equal(["SCOTT.EMP", "SCOTT.EMP_V"], vm.ColumnHits.Select(h => h.Title));
        Assert.Equal(["N", "Y"], vm.ColumnHits.Select(h => h.Nullable));
        Assert.Equal("VIEW", vm.ColumnHits[1].ObjectType);
        Assert.Equal("NUMBER(6)", vm.ColumnHits[0].DataType);
        Assert.Equal("2개 열 찾음(2개 테이블·뷰)", vm.StatusText);

        admin.ColumnHits = admin.ColumnHits with { Capped = true };
        await vm.SearchColumnsAsync();
        Assert.StartsWith($"처음 {ObjectSearchViewModel.MaxHits}개 열만 보인다", vm.StatusText);
    }

    [Fact]
    public async Task Double_click_opens_the_source_line_in_the_plsql_editor_and_the_column_table_detail()
    {
        var session = new FakeSession();
        var body = new DbObjectRef("SCOTT", "PK", "PACKAGE BODY");
        var emp = new DbObjectRef("SCOTT", "EMP", "TABLE");
        var openedSource = new List<(ISession, DbObjectRef, int)>();
        var openedDetail = new List<(ISession, DbObjectRef)>();
        var vm = new ObjectSearchViewModel(new FakeAdmin(), new FakeExec())
        {
            OpenSourceAt = (s, r, line, _) =>
            {
                openedSource.Add((s, r, line));
                return Task.CompletedTask;
            },
            OpenDetail = (s, r, _) =>
            {
                openedDetail.Add((s, r));
                return Task.CompletedTask;
            },
        };

        // 접속 전에는 아무것도 열지 않는다.
        await vm.OpenSourceHitAsync(new SourceSearchItem(new SourceSearchHit(body, 6, "x")));
        Assert.Empty(openedSource);

        vm.Attach(session);
        await vm.OpenSourceHitAsync(new SourceSearchItem(new SourceSearchHit(body, 6, "x")));
        vm.SelectedColumnHit = new ColumnSearchItem(new ColumnSearchHit(emp, "EMP_ID", "NUMBER", true));
        await vm.OpenColumnHitAsync();

        var (sourceSession, sourceRef, line) = Assert.Single(openedSource);
        Assert.Same(session, sourceSession);
        Assert.Equal(body, sourceRef);
        Assert.Equal(6, line);
        var (detailSession, detailRef) = Assert.Single(openedDetail);
        Assert.Same(session, detailSession);
        Assert.Equal(emp, detailRef);
    }

    [Fact]
    public async Task Opening_hits_without_hooks_or_services_does_nothing()
    {
        var vm = new ObjectSearchViewModel(new FakeAdmin(), new FakeExec());
        vm.Attach(new FakeSession());

        await vm.OpenSourceHitAsync(new SourceSearchItem(new SourceSearchHit(new DbObjectRef("SCOTT", "P", "PROCEDURE"), 1, "x")));
        await vm.OpenColumnHitAsync();

        Assert.Equal(string.Empty, vm.StatusText);
    }

    // ---- INVALID 일괄 컴파일 ----

    [Fact]
    public async Task Compile_invalid_runs_alter_compile_through_execution_service_and_lists_results()
    {
        var exec = new FakeExec();
        var admin = new FakeAdmin { Invalid = [BadProc, BadView] };
        exec.OnExecute = stmt =>
        {
            // 컴파일하면 VALID 로 바뀐다.
            admin.Invalid = [.. admin.Invalid.Where(o => !stmt.Text.Contains(o.Name, StringComparison.Ordinal))];
        };
        var vm = new ObjectSearchViewModel(admin, exec);
        vm.Attach(new FakeSession());

        await vm.CompileInvalidAsync();

        Assert.Equal(["ALTER PROCEDURE \"SCOTT\".\"BAD_P\" COMPILE", "ALTER VIEW \"SCOTT\".\"BAD_V\" COMPILE"], exec.Statements.Select(s => s.Text));
        Assert.All(exec.Options, o => Assert.Equal(Oranda.Core.History.HistorySource.SchemaBrowser, o!.Source));
        Assert.Equal(2, vm.CompileResults.Count);
        Assert.All(vm.CompileResults, r => Assert.Equal(CompileOutcome.Compiled, r.Outcome));
        Assert.Contains("2개 컴파일됨", vm.StatusText);
    }

    [Fact]
    public async Task Compile_invalid_includes_java_sources_before_classes_and_skips_java_without_jvm()
    {
        // TSK-14-23: 그 스키마의 INVALID JAVA SOURCE·JAVA CLASS 도 대상(소스 먼저). JVM 이 없으면 제공자가 빈 목록을 준다.
        var exec = new FakeExec();
        var admin = new FakeAdmin { Invalid = [BadProc] };
        var java = new JavaInvalidMeta
        {
            Invalid =
            [
                new JavaObjectInfo(new DbObjectRef("SCOTT", "com/example/Hello", "JAVA CLASS"), "com/example/Hello", "INVALID"),
                new JavaObjectInfo(new DbObjectRef("SCOTT", "com/example/Hello", "JAVA SOURCE"), "com/example/Hello", "INVALID"),
            ],
        };
        exec.OnExecute = stmt =>
        {
            admin.Invalid = [.. admin.Invalid.Where(o => !stmt.Text.Contains(o.Name, StringComparison.Ordinal))];
            java.Invalid = [.. java.Invalid.Where(o => !stmt.Text.Contains(o.Ref.ObjectType, StringComparison.Ordinal))];
        };
        var vm = new ObjectSearchViewModel(admin, exec) { Metadata = java };
        vm.Attach(new FakeSession());

        await vm.CompileInvalidAsync();

        Assert.Equal(
        [
            "ALTER PROCEDURE \"SCOTT\".\"BAD_P\" COMPILE",
            "ALTER JAVA SOURCE \"SCOTT\".\"com/example/Hello\" COMPILE",
            "ALTER JAVA CLASS \"SCOTT\".\"com/example/Hello\" RESOLVE",
        ], exec.Statements.Select(s => s.Text));
        Assert.Equal("SCOTT", java.LastQuery?.Owner);
        Assert.True(java.LastQuery?.InvalidOnly);
        Assert.Equal(["JAVA SOURCE", "JAVA CLASS"], java.LastQuery?.ObjectTypes);
        Assert.All(vm.CompileResults, r => Assert.Equal(CompileOutcome.Compiled, r.Outcome));
        Assert.Contains("3개 컴파일됨", vm.StatusText);

        var noJvm = new ObjectSearchViewModel(new FakeAdmin { Invalid = [BadProc] }, new FakeExec()) { Metadata = new JavaInvalidMeta { Jvm = false } };
        noJvm.Attach(new FakeSession());
        await noJvm.CompileInvalidAsync();
        Assert.Equal([BadProc], noJvm.CompileResults.Select(r => r.Ref));
    }

    [Fact]
    public async Task Compile_invalid_reports_objects_that_stay_invalid_and_retries_without_progress_loop()
    {
        var exec = new FakeExec { Failure = new DbError("ORA-24344", "success with compilation error") };
        var admin = new FakeAdmin { Invalid = [BadProc] };
        var vm = new ObjectSearchViewModel(admin, exec);
        vm.Attach(new FakeSession());

        await vm.CompileInvalidAsync();

        var row = Assert.Single(vm.CompileResults);
        Assert.Equal(CompileOutcome.StillInvalid, row.Outcome);
        Assert.Single(exec.Statements); // 진전이 없으면 되풀이하지 않는다
        Assert.Contains("1개 남음", vm.StatusText);
    }

    [Fact]
    public async Task Compile_invalid_stops_when_policy_denies()
    {
        var exec = new FakeExec { DeniedMessage = "운영 접속입니다" };
        var admin = new FakeAdmin { Invalid = [BadProc, BadView] };
        var vm = new ObjectSearchViewModel(admin, exec);
        vm.Attach(new FakeSession());

        await vm.CompileInvalidAsync();

        Assert.Single(exec.Statements);
        Assert.All(vm.CompileResults, r => Assert.Equal(CompileOutcome.Failed, r.Outcome));
        Assert.Contains("운영 접속입니다", vm.CompileResults[0].Line);
    }

    [Fact]
    public async Task Compile_invalid_is_refused_on_read_only_session_without_calling_engine()
    {
        var exec = new FakeExec();
        var vm = new ObjectSearchViewModel(new FakeAdmin { Invalid = [BadProc] }, exec);
        vm.Attach(new FakeSession(readOnly: true));

        await vm.CompileInvalidAsync();

        Assert.Empty(exec.Statements);
        Assert.Contains("읽기 전용", vm.StatusText);
    }

    [Fact]
    public void Compile_classification()
    {
        var table = new DbObjectRef("SCOTT", "T", "TABLE");
        Assert.Equal(CompileOutcome.Skipped, ObjectSearchViewModel.Classify(table, true, null).Outcome);
        Assert.Equal(CompileOutcome.Compiled, ObjectSearchViewModel.Classify(BadProc, false, "ORA-24344").Outcome);
        Assert.Equal(CompileOutcome.Failed, ObjectSearchViewModel.Classify(BadProc, true, "ORA-00942").Outcome);
        Assert.Equal(CompileOutcome.StillInvalid, ObjectSearchViewModel.Classify(BadProc, true, "ORA-24344 …").Outcome);
    }

    // ---- 휴지통 ----

    [Fact]
    public async Task Purge_goes_through_engine_with_name_retype_target_then_refreshes_list()
    {
        var item = new RecycleBinItem("BIN$x==$0", "EMP", "TABLE", "USERS", new DateTime(2026, 1, 1), true, true, 8);
        var exec = new FakeExec();
        var admin = new FakeAdmin { Bin = [item] };
        exec.OnExecute = _ => admin.Bin = [];
        var vm = new ObjectSearchViewModel(admin, exec);
        vm.Attach(new FakeSession());
        await vm.LoadRecycleBinAsync();
        vm.SelectedRecycle = vm.RecycleBin[0];

        await vm.PurgeAsync();

        var stmt = Assert.Single(exec.Statements);
        Assert.Equal("PURGE TABLE \"BIN$x==$0\"", stmt.Text);
        Assert.Equal("EMP", stmt.DangerousTarget);
        Assert.Empty(vm.RecycleBin);
        Assert.Equal("비웠다", vm.StatusText);
    }

    [Fact]
    public async Task Flashback_uses_new_name_and_denied_purge_keeps_list()
    {
        var item = new RecycleBinItem("BIN$x==$0", "EMP", "TABLE", null, null, true, true, null);
        var exec = new FakeExec();
        var vm = new ObjectSearchViewModel(new FakeAdmin { Bin = [item] }, exec) { FlashbackName = "EMP_BACK" };
        vm.Attach(new FakeSession());

        await vm.FlashbackAsync(item);
        Assert.Equal("FLASHBACK TABLE \"BIN$x==$0\" TO BEFORE DROP RENAME TO \"EMP_BACK\"", exec.Statements[0].Text);

        exec.DeniedMessage = "확인하지 않았다";
        await vm.PurgeAsync(item);
        Assert.StartsWith("막힘:", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("확인하지 않았다", vm.StatusText);
    }

    [Fact]
    public async Task Recyclebin_changes_need_a_target_and_a_writable_session()
    {
        var exec = new FakeExec();
        var vm = new ObjectSearchViewModel(new FakeAdmin(), exec);
        vm.Attach(new FakeSession());

        await vm.PurgeAsync();
        Assert.Contains("고르세요", vm.StatusText);

        var ro = new ObjectSearchViewModel(new FakeAdmin(), exec);
        ro.Attach(new FakeSession(readOnly: true));
        await ro.PurgeAllAsync();
        Assert.Contains("읽기 전용", ro.StatusText);
        Assert.Empty(exec.Statements);
    }

    // ---- DDL 일괄 추출 ----

    [Fact]
    public void Rename_lines_are_parsed_and_bad_lines_skipped()
    {
        var list = DdlExtractViewModel.ParseRenames("# 주석\nSCOTT = \"HR\"\n\nnoequals\n=x\nA=B\r\n");

        Assert.Equal([("SCOTT", "HR"), ("A", "B")], list);
    }

    [Fact]
    public void Plan_carries_filters_options_and_selected_types()
    {
        var vm = new DdlExtractViewModel(new FakeMeta());
        vm.Attach(new FakeSession());
        vm.NameText = "emp";
        vm.IncludeStorage = true;
        vm.SqlTerminator = false;
        vm.FilePerObject = true;
        foreach (var t in vm.TypeChoices.Where(t => t.Name != "TABLE"))
        {
            t.IsSelected = false;
        }

        var plan = vm.BuildPlan();

        Assert.Equal("SCOTT", plan.Owner);
        Assert.Equal(["TABLE"], plan.ObjectTypes);
        Assert.Equal("%EMP%", plan.NamePattern);
        Assert.True(plan.Ddl.IncludeStorage);
        Assert.False(plan.Ddl.SqlTerminator);
        Assert.True(plan.FilePerObject);
    }

    [Fact]
    public async Task Run_writes_ddl_files_and_lists_results()
    {
        var dir = Path.Combine(Path.GetTempPath(), "oranda-dbtools-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var vm = new DdlExtractViewModel(new FakeMeta());
            vm.Attach(new FakeSession());
            vm.OutputDirectory = dir;
            vm.RenameText = "SCOTT=HR";

            await vm.RunAsync();

            var file = Assert.Single(vm.Files);
            Assert.EndsWith("HR.sql", file, StringComparison.Ordinal);
            var text = await File.ReadAllTextAsync(file);
            Assert.Contains("CREATE TABLE \"HR\".\"EMP\"", text);
            Assert.Contains("CREATE TABLE \"HR\".\"DEPT\"", text);
            Assert.Equal(2, vm.Results.Count);
            Assert.All(vm.Results, r => Assert.True(r.Succeeded));
            Assert.Equal(100, vm.Progress);
            Assert.Contains("2개 추출", vm.StatusText);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Run_requires_session_owner_output_and_types()
    {
        var vm = new DdlExtractViewModel(new FakeMeta());
        await vm.RunAsync();
        Assert.Equal("접속이 없다", vm.StatusText);

        vm.Attach(new FakeSession());
        vm.OutputDirectory = string.Empty;
        await vm.RunAsync();
        Assert.Contains("출력 폴더", vm.StatusText);

        vm.OutputDirectory = Path.GetTempPath();
        foreach (var t in vm.TypeChoices)
        {
            t.IsSelected = false;
        }

        await vm.RunAsync();
        Assert.Contains("유형", vm.StatusText);
    }

    [Fact]
    public async Task Run_reports_runner_failure()
    {
        var vm = new DdlExtractViewModel(new FakeMeta())
        {
            Runner = (_, _, _, _, _) => throw new InvalidOperationException("디스크가 가득 찼다"),
        };
        vm.Attach(new FakeSession());

        await vm.RunAsync();

        Assert.Contains("추출하지 못했다", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    // ---- 명령 처리기 ----

    [Fact]
    public async Task Handler_opens_panels_and_fills_them_for_the_current_session()
    {
        var session = new FakeSession();
        var opened = new List<(string Id, ToolPanelViewModelBase Vm)>();
        var handler = new DbToolsCommandHandler(new Services(new FakeInfo(), new FakeAdmin { Invalid = [] }, new FakeExec(), new FakeMeta()))
        {
            SessionProvider = () => session,
            OpenDocument = (id, vm, _) =>
            {
                opened.Add((id, vm));
                return vm;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.db.info", FakeContextKeys.AllTrue), default);
        await handler.ExecuteAsync(new CommandContext("oranda.schema.objectSearch", FakeContextKeys.AllTrue), default);
        await handler.ExecuteAsync(new CommandContext("oranda.db.ddlExtract", FakeContextKeys.AllTrue), default);
        await handler.ExecuteAsync(new CommandContext("oranda.schema.compileInvalid", FakeContextKeys.AllTrue), default);

        Assert.Equal(["oranda.panel.dbInfo", "oranda.panel.objectSearch", "oranda.panel.ddlExtract", "oranda.panel.objectSearch"], opened.Select(o => o.Id));
        var info = Assert.IsType<DbInfoViewModel>(opened[0].Vm);
        Assert.Equal(2, info.Sections.Count);
        Assert.Equal("SCOTT", Assert.IsType<ObjectSearchViewModel>(opened[1].Vm).Owner);
        Assert.Equal("SCOTT", Assert.IsType<DdlExtractViewModel>(opened[2].Vm).Owner);
        Assert.Equal("INVALID 객체가 없다", Assert.IsType<ObjectSearchViewModel>(opened[3].Vm).StatusText);
    }

    [Fact]
    public async Task Handler_without_session_or_services_does_nothing()
    {
        var called = false;
        var handler = new DbToolsCommandHandler(new Services())
        {
            SessionProvider = () => null,
            OpenDocument = (_, vm, _) =>
            {
                called = true;
                return vm;
            },
        };

        await handler.ExecuteAsync(new CommandContext("oranda.db.info", FakeContextKeys.AllTrue), default);
        await new DbToolsCommandHandler().ExecuteAsync(new CommandContext("oranda.db.info", FakeContextKeys.AllTrue), default);

        Assert.False(called);
    }

    [Fact]
    public async Task Recyclebin_command_without_target_lists_the_bin_and_with_target_purges()
    {
        var item = new RecycleBinItem("BIN$x==$0", "EMP", "TABLE", null, null, true, true, null);
        var exec = new FakeExec();
        var search = new ObjectSearchViewModel(new FakeAdmin { Bin = [item] }, exec);
        var handler = new DbToolsCommandHandler(new Services())
        {
            SessionProvider = () => new FakeSession(),
            ActivePanelProvider = () => search,
            OpenDocument = (_, vm, _) => vm,
        };

        await handler.ExecuteAsync(new CommandContext("oranda.schema.recyclebinPurge", FakeContextKeys.AllTrue), default);
        Assert.Single(search.RecycleBin);
        Assert.Empty(exec.Statements);

        await handler.ExecuteAsync(new CommandContext("oranda.schema.recyclebinPurge", FakeContextKeys.AllTrue, item), default);
        Assert.StartsWith("PURGE TABLE", exec.Statements.Single().Text, StringComparison.Ordinal);
    }

    // ---- 가짜 ----

    private sealed class Services(params object[] items) : IServiceProvider
    {
        public object? GetService(Type serviceType) => items.FirstOrDefault(serviceType.IsInstanceOfType);
    }

    private sealed class FakeSession(bool readOnly = false) : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("dev", "scott", "h:1521/s", ReadOnly: readOnly);

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeInfo : IDatabaseInfoProvider
    {
        public bool WithError { get; init; }

        public bool Fails { get; init; }

        public Task<IReadOnlyList<DetailSection>> GetAsync(ISession s, CancellationToken ct)
        {
            if (Fails)
            {
                return Task.FromException<IReadOnlyList<DetailSection>>(new InvalidOperationException("ORA-00942"));
            }

            IReadOnlyList<DetailSection> sections =
            [
                new DetailSection("인스턴스", ["항목", "값"], [["instance_name", "FREE"]]),
                WithError
                    ? new DetailSection("파라미터", ["오류"], [["ORA-00942"]])
                    : new DetailSection("파라미터", ["name", "value"], [["processes", "300"], ["sga_target", "2G"]]),
            ];
            return Task.FromResult(sections);
        }
    }

    internal sealed class FakeAdmin : ISchemaAdminProvider
    {
        public IReadOnlyList<ObjectSearchRow> Rows { get; set; } = [];

        public IReadOnlyList<DbObjectRef> Invalid { get; set; } = [];

        public IReadOnlyList<RecycleBinItem> Bin { get; set; } = [];

        public ObjectSearchQuery? LastQuery { get; private set; }

        public SearchHits<SourceSearchHit> SourceHits { get; set; } = new([], Capped: false);

        public Exception? SourceFailure { get; set; }

        public SourceSearchQuery? LastSourceQuery { get; private set; }

        public SearchHits<ColumnSearchHit> ColumnHits { get; set; } = new([], Capped: false);

        public ColumnSearchQuery? LastColumnQuery { get; private set; }

        /// <summary>닫아 두면 객체 검색이 열릴 때까지(또는 취소될 때까지) 끝나지 않는다.</summary>
        public TaskCompletionSource? SearchGate { get; set; }

        public async Task<IReadOnlyList<ObjectSearchRow>> SearchObjectsAsync(ISession s, ObjectSearchQuery q, CancellationToken ct)
        {
            LastQuery = q;
            IReadOnlyList<ObjectSearchRow> rows = q.InvalidOnly
                ? [.. Invalid.Select(o => new ObjectSearchRow(o, "INVALID", null, null, null))]
                : Rows;
            if (SearchGate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }

            return rows;
        }

        public Task<IReadOnlyList<ObjectReference>> GetReferencesAsync(ISession s, DbObjectRef obj, CancellationToken ct)
        {
            IReadOnlyList<ObjectReference> refs =
            [
                new ObjectReference(false, new DbObjectRef("SCOTT", "DEPT", "TABLE")),
                new ObjectReference(true, new DbObjectRef("SCOTT", "EMP_V", "VIEW")),
            ];
            return Task.FromResult(refs);
        }

        public Task<IReadOnlyList<RecycleBinItem>> ListRecycleBinAsync(ISession s, CancellationToken ct) => Task.FromResult(Bin);

        public Task<SearchHits<SourceSearchHit>> SearchSourceAsync(ISession s, SourceSearchQuery q, CancellationToken ct)
        {
            LastSourceQuery = q;
            return SourceFailure is { } e ? Task.FromException<SearchHits<SourceSearchHit>>(e) : Task.FromResult(SourceHits);
        }

        public Task<SearchHits<ColumnSearchHit>> SearchColumnsAsync(ISession s, ColumnSearchQuery q, CancellationToken ct)
        {
            LastColumnQuery = q;
            return Task.FromResult(ColumnHits);
        }
    }

    internal sealed class FakeExec : IExecutionService
    {
        public List<SqlStatement> Statements { get; } = [];

        public List<ExecutionOptions?> Options { get; } = [];

        public Action<SqlStatement>? OnExecute { get; set; }

        public DbError? Failure { get; set; }

        public string? DeniedMessage { get; set; }

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                  ExecutionOptions? options, CancellationToken ct)
        {
            Statements.Add(stmt);
            Options.Add(options);
            OnExecute?.Invoke(stmt);
            if (DeniedMessage is not null)
            {
                return Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Denied)
                {
                    Policy = new PolicyDecision(PolicyDecisionKind.Deny, PolicyRule.None, DeniedMessage),
                });
            }

            return Task.FromResult(Failure is null
                ? new ExecutionResult(stmt, ExecutionStatus.Succeeded)
                : new ExecutionResult(stmt, ExecutionStatus.Failed) { Error = Failure });
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                         ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>INVALID Java 객체 목록만 아는 가짜(실제 제공자처럼 JVM 이 없으면 빈 목록).</summary>
    private sealed class JavaInvalidMeta : IMetadataProvider
    {
        public bool Jvm { get; init; } = true;

        public IReadOnlyList<JavaObjectInfo> Invalid { get; set; } = [];

        public ObjectQuery? LastQuery { get; private set; }

        public Task<IReadOnlyList<JavaObjectInfo>> ListJavaObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            LastQuery = q;
            return Task.FromResult(Jvm ? Invalid : []);
        }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct)
        {
            IReadOnlyList<DbObjectRef> list = [new("SCOTT", "EMP", "TABLE"), new("SCOTT", "DEPT", "TABLE")];
            return Task.FromResult(list);
        }

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct)
            => Task.FromResult($"CREATE TABLE \"{obj.Owner}\".\"{obj.Name}\" (ID NUMBER);");

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
