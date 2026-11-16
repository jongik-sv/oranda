using System.Runtime.CompilerServices;
using Oranda.App.Features.Rollback;
using Oranda.App.Tests.Features.Execution;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.Rollback;

/// <summary>
/// 원복 스크립트 탭과 원복 실행(되돌리기 지점 설계 6.7·11.3, RD-8): 역순 글, 무효·불완전 주석, 운영 확인 한 번, 미리 확인·기록 안 함으로 보내기,
/// 0행·충돌·오류 요약, 반대 문장 기록, 복사·내보내기, 열기 명령의 거절 이유.
/// </summary>
public sealed class RollbackScriptTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 5, 3, 21, TimeSpan.Zero);
    private static readonly ConnectionProfile Prod = new("prod-example", "app", "db:1521/ORCL", Environment: ConnectionEnvironment.Production);

    private static RollbackGroup Group(Guid profileId, RollbackScriptState state = RollbackScriptState.Confirmed) => new()
    {
        Id = 318,
        ProfileId = profileId,
        ProfileName = "prod-example",
        DbKey = "prod-example",
        StartedAt = T0,
        EndedAt = T0.AddMinutes(2),
        EndKind = TransactionEndKind.Commit,
        CommitPoint = new ChangePoint(48214022, "SCN", T0, "test"),
        State = state,
        HasScriptFile = true,
        Tables = [new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE")],
    };

    private static RollbackScriptStatement St(int seq, string text, params string[] steps) =>
        new(seq, T0.AddSeconds(seq * 40), text, [], [.. steps.Select(s => new RollbackStep(s))], [], false, null, steps.Length);

    private static RollbackScriptDocument Doc() => new(RollbackScriptDocument.CurrentSchemaVersion, 318,
    [
        St(1, "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = UNIT_PRICE * 1.1 WHERE CATEGORY_ID = 3",
            "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 25900\n WHERE PRODUCT_ID = 1001 AND UNIT_PRICE = 28490",
            "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300\n WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730"),
        St(2, "DELETE FROM SAMPLE.PRODUCTS WHERE DISCONTINUED = 'Y'",
            "INSERT INTO SAMPLE.PRODUCTS (PRODUCT_ID, PRODUCT_NAME) VALUES (1040, '보리차 티백')") with { IncompleteReason = "큰 열(CLOB) 1행" },
        St(3, "UPDATE SAMPLE.PRODUCTS SET MEMO = NULL", "UPDATE SAMPLE.PRODUCTS SET MEMO = 'x' WHERE PRODUCT_ID = 1") with { Invalidated = true, IncompleteReason = "ROLLBACK TO SP1 로 되돌림" },
    ]);

    [Fact]
    public void Tab_shows_the_core_script_text_and_skips_statements_marked_invalid_in_the_group()
    {
        var group = Group(Guid.NewGuid()) with
        {
            // 묶음 정보에서만 무효로 표시된 문장(저장점으로 되돌림)도 실행 차례에서 뺀다.
            Statements = [new RollbackStatementInfo(1, T0, DmlVerb.Update, null, "UPDATE …", 2, null, RollbackScriptState.Open, null, 2, Invalidated: true)],
        };
        var vm = new RollbackScriptViewModel(group, Doc());

        Assert.Equal(Oranda.Core.Rollback.Script.RollbackScriptWriter.Write(group, Doc()), vm.Text);
        Assert.Equal(1, vm.StepCount); // [3/3] 무효(문서), [1/3] 무효(묶음) → [2/3] 의 INSERT 하나
        Assert.Equal([2], RollbackRunner.Order(Doc(), group).Select(o => o.Seq));
    }

    [Fact]
    public async Task Runner_sends_preconfirmed_unrecorded_statements_and_summarises_zero_rows_conflicts_and_errors()
    {
        var exec = new ScriptedExecution(sql => sql switch
        {
            _ when sql.StartsWith("INSERT", StringComparison.Ordinal) => Failed(sql, DbErrorKind.ConstraintViolation),
            _ when sql.Contains("1040", StringComparison.Ordinal) => Rows(sql, 0),
            _ => Rows(sql, 1),
        });
        var ledger = new RecordingRollbackLedger();
        var session = new FakeSession(Prod);
        var runner = new RollbackRunner(exec, ledger) { ReverseAll = steps => [.. steps.Reverse().Select(s => new RollbackStep("-- inverse of " + s.Sql))] };

        var summary = await runner.RunAsync(session, Group(Prod.Id), Doc(), null, default);

        Assert.Equal(3, exec.Sent.Count);
        Assert.StartsWith("INSERT", exec.Sent[0].Text, StringComparison.Ordinal); // [2/3] 이 먼저
        Assert.All(exec.Options, o =>
        {
            Assert.Equal(ChangeRecording.None, o.Recording);
            Assert.NotNull(o.PreConfirmed);
        });
        Assert.Contains(exec.Sent[1].Text, exec.Options[1].PreConfirmed!);
        Assert.Equal((1, 1, 1, 0), (summary.Reverted, summary.ZeroRows, summary.Conflicts, summary.Errors));
        Assert.Contains("커밋하지 않았습니다", summary.Text, StringComparison.Ordinal);
        Assert.Equal(0, session.Commits);

        // 실제로 실행한(성공한) 문장마다 기록 한 건: 글 = 실행한 원복 문장, 반대 문장 = 1행을 되돌린 문장만. 충돌로 실패한 INSERT 는 남기지 않는다.
        Assert.Equal(2, ledger.Derived.Count);
        Assert.All(ledger.Derived, d => Assert.Equal((RollbackOrigin.RollbackRun, 318L), (d.Origin, d.OriginGroupId)));
        Assert.Equal([exec.Sent[1].Text, exec.Sent[2].Text], ledger.Derived.Select(d => d.Record.Text));
        Assert.Equal([1, 0], ledger.Derived.Select(d => d.Record.Steps.Count));
        Assert.Equal([1L, 0L], ledger.Derived.Select(d => d.Record.RowsAffected ?? -1));
    }

    [Fact]
    public async Task Rollback_run_journals_each_executed_statement_with_its_text_and_the_point_read_just_before_it()
    {
        var exec = new ScriptedExecution(sql => Rows(sql, 1));
        var store = new InMemoryRollbackStore();
        var ledger = new Oranda.Core.Rollback.Ledger.RollbackLedger(store, null);
        var session = new FakeSession(Prod);
        var reads = 0;
        var runner = new RollbackRunner(exec, ledger)
        {
            ReverseAll = steps => [.. steps.Reverse().Select(s => new RollbackStep("-- inverse of " + s.Sql))],
            ReadPoint = (_, _) =>
            {
                reads++;
                Assert.Empty(exec.Sent); // 첫 원복 문장 직전
                return Task.FromResult<ChangePoint?>(new ChangePoint(48300000, "SCN", T0, "test"));
            },
        };
        var group = Group(Prod.Id);
        var doc = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 318,
        [
            St(1, "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = :p WHERE CATEGORY_ID = 3",
                "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 25900\n WHERE PRODUCT_ID = 1001 AND UNIT_PRICE = 28490",
                "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300\n WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 4730"),
        ]);

        await runner.RunAsync(session, group, doc, null, default);
        await ledger.EndAsync(new TransactionEnded(session.Id, TransactionEndKind.Commit, TransactionEndSource.SessionApi), default);

        Assert.Equal(1, reads);
        var saved = Assert.Single(store.Groups);
        Assert.Equal((RollbackOrigin.RollbackRun, 318L), (saved.Origin, saved.OriginGroupId));
        Assert.Equal(48300000, saved.BasePoint?.Number); // 시점 칸·「이 실행 전과 비교」의 기준
        Assert.Equal(RollbackScriptState.Confirmed, saved.State);
        Assert.Equal(2, saved.Statements.Count); // 원복 문장 두 개 = 두 줄
        Assert.All(saved.Statements, s => Assert.Equal(new DbObjectRef("SAMPLE", "PRODUCTS", "TABLE"), s.Table));
        Assert.Equal([DmlVerb.Update, DmlVerb.Update], saved.Statements.Select(s => s.Verb));
        var texts = store.Document(saved.Id)!.Statements.Select(s => s.Text).ToList();
        Assert.Equal([exec.Sent[0].Text, exec.Sent[1].Text], texts); // 머리 주석이 아니라 실행한 문장 원문
        Assert.Contains("UNIT_PRICE = 25900", texts[0], StringComparison.Ordinal);
        // 평문 저장소에 남는 목록 요약은 값을 가린다(RD-10). 값은 암호화 문서의 원문에만.
        Assert.Equal("UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = ? WHERE PRODUCT_ID = ? AND UNIT_PRICE = ?", saved.Statements[0].Summary);
        Assert.DoesNotContain(saved.Statements, s => s.Summary.Contains("25900", StringComparison.Ordinal));

        // 변경 일지: 문장 줄 원인 = 「원복 실행(#318)」, 첫 줄 시점 = 실행 직전 SCN.
        var item = new Oranda.App.Features.ChangeJournal.RollbackGroupItem(saved);
        var rows = saved.Statements.Select(s => new Oranda.App.Features.ChangeJournal.RollbackStatementItem(item, s)).ToList();
        Assert.Equal(["원복 실행(#318)", "원복 실행(#318)"], rows.Select(r => r.CauseText));
        Assert.Equal("48300000", rows[0].PointText);
        Assert.Equal("SCN 48300000", item.PointText);
    }

    [Fact]
    public async Task Undoing_a_rollback_run_replays_real_inverses_last_first_and_a_multi_row_change_makes_the_group_incomplete()
    {
        var exec = new ScriptedExecution(sql => Rows(sql, sql.Contains("2002", StringComparison.Ordinal) ? 2 : 1));
        var store = new InMemoryRollbackStore();
        var ledger = new Oranda.Core.Rollback.Ledger.RollbackLedger(store, null);
        var session = new FakeSession(Prod);
        var runner = new RollbackRunner(exec, ledger); // Core 의 실제 반대 문장
        var doc = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 318,
        [
            St(1, "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 1",
                "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 25900 WHERE PRODUCT_ID = 1001 AND UNIT_PRICE = 1",
                "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 4300 WHERE PRODUCT_ID = 1040 AND UNIT_PRICE = 1",
                "UPDATE SAMPLE.PRODUCTS SET UNIT_PRICE = 700 WHERE PRODUCT_ID = 2002 AND UNIT_PRICE = 1"),
        ]);

        await runner.RunAsync(session, Group(Prod.Id), doc, null, default);
        await ledger.EndAsync(new TransactionEnded(session.Id, TransactionEndKind.Commit, TransactionEndSource.SessionApi), default);

        var saved = Assert.Single(store.Groups);
        Assert.Equal(3, saved.Statements.Count);
        Assert.Equal(RollbackScriptState.Incomplete, saved.State); // 2행을 바꾼 문장은 반대 문장을 만들지 않는다
        Assert.Contains("영향 행 2", saved.StateReason, StringComparison.Ordinal);
        var replay = RollbackRunner.Order(store.Document(saved.Id)!, saved).Select(o => o.Step.Sql).ToList();
        Assert.Equal(2, replay.Count);
        Assert.Contains("PRODUCT_ID = 1040", replay[0], StringComparison.Ordinal); // 마지막에 실행한 문장의 반대 문장이 먼저
        Assert.Contains("UNIT_PRICE = 1", replay[0], StringComparison.Ordinal);
        Assert.Contains("PRODUCT_ID = 1001", replay[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("UPDATE \"T2\" SET NAME = 'O''Brien', N1 = -12.5E+3 WHERE ID = 7", "UPDATE \"T2\" SET NAME = ?, N1 = -? WHERE ID = ?")]
    [InlineData("INSERT INTO S.T (C_1, D) VALUES (TO_DATE('2026-11-16', 'YYYY-MM-DD'), 0)", "INSERT INTO S.T (C_1, D) VALUES (TO_DATE(?, ?), ?)")]
    public void Rollback_run_summary_masks_values_but_keeps_names(string sql, string expected) =>
        Assert.Equal(expected, RollbackRunner.MaskValues(sql));

    [Fact]
    public async Task Run_on_production_asks_once_and_needs_an_open_session()
    {
        var dialogs = new FakeScriptDialogs { Answer = false };
        var session = new FakeSession(Prod);
        var exec = new ScriptedExecution(sql => Rows(sql, 1));
        var vm = new RollbackScriptViewModel(Group(Prod.Id) with { DbKey = session.DbKey }, Doc())
        {
            Dialogs = dialogs,
            SessionFinder = _ => null,
            Execution = () => exec,
        };

        Assert.Null(await vm.RunAsync());
        Assert.Contains("열려 있지 않습니다", vm.Message, StringComparison.Ordinal);
        Assert.Empty(dialogs.Asked);

        vm.SessionFinder = id => id == Prod.Id ? session : null;
        Assert.Null(await vm.RunAsync()); // 운영 확인에서 취소
        Assert.Equal("prod-example(운영)에서 원복 문장 3개를 실행합니다. 커밋하지 않습니다.", Assert.Single(dialogs.Asked));
        Assert.Empty(exec.Sent);

        dialogs.Answer = true;
        var summary = await vm.RunAsync();
        Assert.NotNull(summary);
        Assert.Equal(3, summary.Reverted);
        Assert.Equal(3, exec.Sent.Count); // 문장마다 다시 묻지 않는다
        Assert.Equal(2, dialogs.Asked.Count);
        Assert.Contains("원복 문장 3개 · 암호화 보관 · ", vm.StatusLine, StringComparison.Ordinal);

        // 같은 접속 이름이 다른 DB 를 가리키면 실행하지 않는다.
        var moved = new RollbackScriptViewModel(Group(Prod.Id) with { DbKey = "OTHER/DB/APP" }, Doc()) { Dialogs = dialogs, SessionFinder = _ => session, Execution = () => exec };
        Assert.Null(await moved.RunAsync());
        Assert.Contains("다른 DB", moved.Message, StringComparison.Ordinal);
        Assert.Equal(3, exec.Sent.Count);
        Assert.EndsWith("읽기 전용", vm.StatusLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Copy_opens_an_editable_tab_and_export_warns_about_plain_text_first()
    {
        var dialogs = new FakeScriptDialogs { Answer = false };
        var opened = new List<(string Text, Guid? Profile)>();
        var profileId = Guid.NewGuid();
        var group = Group(profileId);
        var vm = new RollbackScriptViewModel(group, Doc()) { Dialogs = dialogs, EditorOpener = (t, p) => { opened.Add((t, p)); return true; } };

        Assert.True(vm.CopyToEditor());
        Assert.Equal(vm.Text, Assert.Single(opened).Text);
        Assert.Equal(profileId, opened[0].Profile);

        Assert.False(await vm.ExportAsync());
        Assert.Contains("평문", Assert.Single(dialogs.Asked), StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), "oranda-rb-" + Guid.NewGuid().ToString("N") + ".sql");
        dialogs.Answer = true;
        dialogs.Path = path;
        try
        {
            Assert.True(await vm.ExportAsync());
            Assert.Equal("SET DEFINE OFF\n" + vm.Text, await File.ReadAllTextAsync(path)); // 값 안의 & 를 치환하지 않게
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Open_command_refuses_rolled_back_groups_and_missing_files_with_a_reason()
    {
        var store = new InMemoryRollbackStore();
        var withFile = await store.SaveAsync(Group(Prod.Id) with { Id = 0 }, Doc(), default);
        var invalid = await store.SaveAsync(Group(Prod.Id, RollbackScriptState.Invalid) with { Id = 0 }, Doc(), default);
        var noFile = await store.SaveAsync(Group(Prod.Id) with { Id = 0 }, null, default);
        var notices = new List<string>();
        var docs = new List<RollbackScriptViewModel>();
        var handler = new RollbackCommandHandler(new Services(store))
        {
            OpenDocument = vm => { docs.Add(vm); return true; },
            Notify = (m, _) => notices.Add(m),
        };

        var vm = await handler.OpenAsync(withFile, default);
        Assert.NotNull(vm);
        Assert.Equal($"원복 스크립트 #{withFile} · prod-example", vm.Title);
        Assert.Single(docs);

        Assert.Null(await handler.OpenAsync(invalid, default));
        Assert.Null(await handler.OpenAsync(noFile, default));
        Assert.Null(await handler.OpenAsync(9999, default));
        Assert.Equal(3, notices.Count);
        Assert.Contains("롤백한 묶음", notices[0], StringComparison.Ordinal);
        Assert.Single(docs);
    }

    private static ExecutionResult Rows(string sql, long n) =>
        new(new SqlStatement(sql, default, StatementKind.Dml), ExecutionStatus.Succeeded) { RowsAffected = n };

    private static ExecutionResult Failed(string sql, DbErrorKind kind) =>
        new(new SqlStatement(sql, default, StatementKind.Dml), ExecutionStatus.Failed) { Error = new DbError("ORA-00001", "unique constraint violated") { Kind = kind } };

    private sealed class ScriptedExecution(Func<string, ExecutionResult> answer) : IExecutionService
    {
        public List<SqlStatement> Sent { get; } = [];

        public List<ExecutionOptions> Options { get; } = [];

        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            Sent.Add(stmt);
            Options.Add(options ?? ExecutionOptions.Default);
            return Task.FromResult(answer(stmt.Text));
        }

        public async IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
            ExecutionOptions? options, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var st in stmts)
            {
                yield return await ExecuteAsync(s, st, new Dictionary<string, BindValue>(), options, ct);
            }
        }
    }

    private sealed class FakeScriptDialogs : IRollbackScriptDialogs
    {
        public bool Answer { get; set; }

        public string? Path { get; set; }

        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmAsync(string title, string message, string okText, CancellationToken ct)
        {
            Asked.Add(message);
            return Task.FromResult(Answer);
        }

        public Task<string?> PickExportPathAsync(string suggestedName, CancellationToken ct) => Task.FromResult(Path);
    }

    private sealed class Services(IRollbackStore store) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IRollbackStore) ? store : null;
    }
}
