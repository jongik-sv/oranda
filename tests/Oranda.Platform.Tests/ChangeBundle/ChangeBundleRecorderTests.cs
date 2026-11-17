using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Platform.ChangeBundle;
using Oranda.Testing;

namespace Oranda.Platform.Tests.ChangeBundle;

// 소유: te-bundle(T5). 반영 묶음 자동 넣기(설계 9.3)·「묶음에 넣기」 서비스.
public sealed partial class ChangeBundleRecorderTests : IDisposable
{
    private readonly TempDirectory _dir = new("oranda-bundle-rec-");
    private readonly FakeEventBus _bus = new();
    private readonly SqliteChangeBundleStore _store;
    private readonly ChangeBundleService _service;
    private readonly ChangeBundleRecorder _recorder;

    public ChangeBundleRecorderTests()
    {
        Directory.CreateDirectory(_dir);
        _store = SqliteChangeBundleStore.ForFile(Path.Combine(_dir, "oranda.db"), new ChangeBundleStoreTests.SecretRedactorStub());
        _service = new ChangeBundleService(_store, new FakeOwnerRewriter(), _bus);
        _recorder = new ChangeBundleRecorder(_bus, _service);
        _recorder.RunAsync(default).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _service.Dispose();
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private Guid Open(ConnectionEnvironment env = ConnectionEnvironment.Development, Dbms dbms = Dbms.Oracle, string name = "dev-example", string user = "SAMPLE")
    {
        var id = Guid.NewGuid();
        var profile = TestData.Profile(env) with { Name = name };
        _bus.Publish(new SessionOpened(id, profile, TestData.Server() with { SessionUser = user, Dbms = dbms }, Oranda.Core.Sessions.SessionPurpose.Work));
        return id;
    }

    private async Task Run(Guid session, string text, HistorySource source = HistorySource.Editor, ExecutionStatus status = ExecutionStatus.Succeeded,
        StatementKind kind = StatementKind.Ddl, string verb = "ALTER", Oranda.Core.Journal.ChangeTarget? target = null)
    {
        var stmt = new SqlStatement(text, default, kind) { Verb = verb, ChangeTarget = target };
        _bus.Publish(new StatementExecuted(session, stmt, new ExecutionResult(stmt, status), new ExecutionOptions { Source = source }));
        await _recorder.DrainAsync();
    }

    private async Task<ChangeBundleInfo> Current(params string[] owners)
        => (await _store.CreateAsync("릴리스 1", null, owners.Length == 0 ? ["SAMPLE"] : owners, true, default))!;

    [Fact]
    public async Task Successful_editor_ddl_is_added_with_owner_and_a_run_mark()
    {
        var bundle = await Current();
        var dev = Open();
        await Run(dev, "ALTER TABLE ORDERS ADD (NOTE VARCHAR2(100))");

        var item = Assert.Single(await _store.ListItemsAsync(bundle.Id, default));
        Assert.Equal("ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))", item.Text); // 소유자를 붙인 꼴
        Assert.Equal(ChangeBundleItemSource.SqlEditor, item.Source);
        Assert.Equal("SAMPLE", item.TargetOwner);
        Assert.NotNull((await _store.GetRunMarksAsync(bundle.Id, default))[item.Id].Development);
        Assert.Equal(Dbms.Oracle, (await _store.GetCurrentAsync(default))!.Dbms); // 빈 묶음은 첫 문장의 DBMS 를 따른다
        var changed = Assert.Single(_bus.PublishedOf<ChangeBundleChanged>());
        Assert.Equal(new ChangeBundleChanged(bundle.Id, 1, 0) { OccurredAt = changed.OccurredAt }, changed);
    }

    [Fact]
    public async Task Same_hash_marks_run_only()
    {
        var bundle = await Current();
        await Run(Open(), "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))");
        await Run(Open(ConnectionEnvironment.Test, name: "test-example"), "alter TABLE  SAMPLE.ORDERS\nADD (NOTE VARCHAR2(100))".Replace("alter", "ALTER"));

        var item = Assert.Single(await _store.ListItemsAsync(bundle.Id, default));
        var marks = (await _store.GetRunMarksAsync(bundle.Id, default))[item.Id];
        Assert.NotNull(marks.Development);
        Assert.NotNull(marks.Test);
        Assert.Null(marks.Production);
        Assert.Equal(1, _bus.PublishedOf<ChangeBundleChanged>().Last().Marked);
    }

    [Fact]
    public async Task Table_editor_source_is_recorded_as_table_editor()
    {
        var bundle = await Current();
        await Run(Open(), "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))", HistorySource.TableEditor);
        Assert.Equal(ChangeBundleItemSource.TableEditor, Assert.Single(await _store.ListItemsAsync(bundle.Id, default)).Source);
    }

    [Theory]
    [InlineData(HistorySource.ChangeBundle)]
    [InlineData(HistorySource.SchemaBrowser)]
    [InlineData(HistorySource.DataCopy)]
    [InlineData(HistorySource.Agent)]
    public async Task Bundle_run_and_other_sources_are_not_added(HistorySource source)
    {
        var bundle = await Current();
        await Run(Open(), "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))", source);
        Assert.Empty(await _store.ListItemsAsync(bundle.Id, default));
    }

    [Fact]
    public async Task Failed_and_non_ddl_statements_are_not_added()
    {
        var bundle = await Current();
        var dev = Open();
        await Run(dev, "ALTER TABLE SAMPLE.ORDERS MODIFY (NOTE VARCHAR2(1))", status: ExecutionStatus.Failed);
        await Run(dev, "UPDATE SAMPLE.ORDERS SET NOTE = 'x'", kind: StatementKind.Dml, verb: "UPDATE");
        await Run(Guid.NewGuid(), "ALTER TABLE SAMPLE.ORDERS ADD (Z NUMBER)"); // 모르는 세션
        Assert.Empty(await _store.ListItemsAsync(bundle.Id, default));
    }

    [Fact]
    public async Task Without_a_current_bundle_nothing_is_added()
    {
        var other = (await _store.CreateAsync("B", null, ["SAMPLE"], makeCurrent: false, default))!;
        await Run(Open(), "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))");
        Assert.Empty(await _store.ListItemsAsync(other.Id, default));
        Assert.Empty(_bus.PublishedOf<ChangeBundleChanged>());
    }

    [Fact]
    public async Task Dbms_mismatch_is_announced_and_not_added()
    {
        var bundle = (await _store.CreateAsync("Oracle 묶음", Dbms.Oracle, ["SAMPLE"], true, default))!;
        await Run(Open(dbms: Dbms.Tibero), "ALTER TABLE SAMPLE.ORDERS ADD (NOTE VARCHAR2(100))");

        Assert.Empty(await _store.ListItemsAsync(bundle.Id, default));
        var notice = Assert.Single(_bus.PublishedOf<ChangeBundleDbmsMismatch>());
        Assert.Equal((bundle.Id, "Oracle 묶음", Dbms.Oracle, Dbms.Tibero), (notice.BundleId, notice.BundleName, notice.BundleDbms, notice.SessionDbms));
    }

    [Fact]
    public async Task Owner_outside_the_targets_is_skipped_silently()
    {
        var bundle = await Current("SAMPLE");
        await Run(Open(user: "SCOTT"), "ALTER TABLE ORDERS ADD (NOTE VARCHAR2(100))"); // 현재 스키마 SCOTT
        await Run(Open(), "ALTER TABLE OTHER.ORDERS ADD (NOTE VARCHAR2(100))");
        Assert.Empty(await _store.ListItemsAsync(bundle.Id, default));
        Assert.Empty(_bus.PublishedOf<ChangeBundleDbmsMismatch>());
        Assert.Empty(_bus.PublishedOf<ChangeBundleChanged>());
    }

    [Fact]
    public async Task Current_schema_switch_is_followed_per_session()
    {
        var bundle = await Current("SAMPLE_APP");
        var dev = Open(user: "SAMPLE");
        await Run(dev, "ALTER SESSION SET CURRENT_SCHEMA = SAMPLE_APP", verb: "ALTER SESSION");
        await Run(dev, "ALTER TABLE ORDERS ADD (NOTE VARCHAR2(100))");

        var item = Assert.Single(await _store.ListItemsAsync(bundle.Id, default));
        Assert.Equal("ALTER TABLE SAMPLE_APP.ORDERS ADD (NOTE VARCHAR2(100))", item.Text);

        await Run(Open(user: "SAMPLE"), "ALTER TABLE ORDERS ADD (OTHER NUMBER)"); // 다른 세션은 접속 사용자 그대로 → 대상 아님
        Assert.Single(await _store.ListItemsAsync(bundle.Id, default));

        // 앱(「묶음에 넣기」)도 같은 현재 스키마를 본다.
        Assert.Equal("SAMPLE_APP", _service.OriginOf(dev)!.CurrentSchema);
        _bus.Publish(new SessionClosed(dev));
        Assert.Null(_service.OriginOf(dev));
    }

    [Fact]
    public async Task Failed_schema_switch_is_ignored()
    {
        var dev = Open(user: "SAMPLE");
        await Run(dev, "ALTER SESSION SET CURRENT_SCHEMA = NOPE", verb: "ALTER SESSION", status: ExecutionStatus.Failed);
        Assert.Equal("SAMPLE", _service.OriginOf(dev)!.CurrentSchema);
    }

    [Fact]
    public async Task Classifier_target_owner_is_used_when_the_shape_is_unknown()
    {
        var bundle = await Current("SAMPLE");
        var target = new Oranda.Core.Journal.ChangeTarget("OTHER", "ORDERS", null, Oranda.Core.Journal.ChangeOperation.Rename);
        await Run(Open(), "RENAME OTHER_SHAPE", verb: "RENAME", target: target); // 모양을 모름 + 분류기 대상 OTHER → 대상 아님
        Assert.Empty(await _store.ListItemsAsync(bundle.Id, default));
    }

    [Fact]
    public async Task Bundle_without_target_owners_adopts_the_first_statement_owner()
    {
        var bundle = (await _store.CreateAsync("빈 묶음", null, [], true, default))!;
        var dev = Open();
        await Run(dev, "ALTER TABLE OTHER.ORDERS ADD (A NUMBER)");
        await Run(dev, "ALTER TABLE SAMPLE.ORDERS ADD (B NUMBER)");

        var item = Assert.Single(await _store.ListItemsAsync(bundle.Id, default));
        Assert.Equal("OTHER", item.TargetOwner);
        var after = (await _store.GetCurrentAsync(default))!;
        Assert.Equal(["OTHER"], after.TargetOwners);
        Assert.Equal(Dbms.Oracle, after.Dbms);
    }

    [Fact]
    public async Task Unknown_shape_is_added_as_is_with_a_warning_flag()
    {
        var bundle = await Current();
        await Run(Open(), "RENAME ORDERS TO ORDERS_OLD", verb: "RENAME");
        var item = Assert.Single(await _store.ListItemsAsync(bundle.Id, default));
        Assert.Equal("RENAME ORDERS TO ORDERS_OLD", item.Text);
        Assert.Equal(ChangeBundleCautions.OwnerNotQualified, item.Cautions);
        Assert.Equal("SAMPLE", item.TargetOwner);
    }

    // ---- 「묶음에 넣기」(실행하지 않은 넣기) ----

    [Fact]
    public async Task Add_without_execution_leaves_no_run_mark_and_reports_duplicates()
    {
        var bundle = await Current();
        var origin = new ChangeBundleOrigin(Dbms.Oracle, "SAMPLE", ConnectionEnvironment.Development, "dev-example");
        var first = await _service.AddAsync(origin,
            [new ChangeBundleDraft("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER)"), new ChangeBundleDraft("ALTER TABLE SAMPLE.ORDERS DROP CONSTRAINT SYS_C0012", "서버 이름 제약")],
            ChangeBundleItemSource.TableEditor, default);
        Assert.Equal(ChangeBundleAddOutcome.Done, first.Outcome);
        Assert.Equal(2, first.Added);
        Assert.Empty(await _store.GetRunMarksAsync(bundle.Id, default));
        Assert.Equal("서버 이름 제약", (await _store.ListItemsAsync(bundle.Id, default))[1].Warning);

        var again = await _service.AddAsync(origin, [new ChangeBundleDraft("ALTER TABLE SAMPLE.ORDERS ADD (A NUMBER)")], ChangeBundleItemSource.TableEditor, default);
        Assert.Equal(ChangeBundleAddOutcome.AlreadyPresent, again.Outcome);
        Assert.Equal(1, again.AlreadyPresent);
        Assert.Equal(2, (await _store.ListItemsAsync(bundle.Id, default)).Count);

        var other = await _service.AddAsync(origin, [new ChangeBundleDraft("ALTER TABLE OTHER.T ADD (A NUMBER)")], ChangeBundleItemSource.TableEditor, default);
        Assert.Equal(ChangeBundleAddOutcome.OwnerNotTarget, other.Outcome);

        var tibero = await _service.AddAsync(origin with { Dbms = Dbms.Tibero }, [new ChangeBundleDraft("ALTER TABLE SAMPLE.T ADD (A NUMBER)")],
            ChangeBundleItemSource.TableEditor, default);
        Assert.Equal(ChangeBundleAddOutcome.DbmsMismatch, tibero.Outcome);
        Assert.Empty(_bus.PublishedOf<ChangeBundleDbmsMismatch>()); // 알림은 자동 넣기에서만 — 넣기 단추는 결과로 알린다
    }

    [Fact]
    public async Task Service_create_fills_owner_and_reports_missing_bundle()
    {
        var origin = new ChangeBundleOrigin(Dbms.Oracle, "SAMPLE", ConnectionEnvironment.Development, "dev-example");
        Assert.Equal(ChangeBundleAddOutcome.NoCurrentBundle,
            (await _service.AddAsync(origin, [new ChangeBundleDraft("DROP TABLE SAMPLE.X")], ChangeBundleItemSource.Manual, default)).Outcome);

        var created = (await _service.CreateAsync("릴리스 2", origin, makeCurrent: true, default))!;
        Assert.Equal(["SAMPLE"], created.TargetOwners);
        Assert.Null(created.Dbms);
        Assert.Equal(created.Id, (await _service.GetCurrentAsync(default))!.Id);
        Assert.Contains(_bus.PublishedOf<ChangeBundleChanged>(), e => e.BundleId is null);

        var item = (await _service.AddAsync(origin, [new ChangeBundleDraft("DROP TABLE SAMPLE.X")], ChangeBundleItemSource.Manual, default));
        Assert.Equal(1, item.Added);
        var id = (await _store.ListItemsAsync(created.Id, default))[0].Id;
        await _service.RecordRunAsync(id, origin with { Environment = ConnectionEnvironment.Production, ConnectionName = "prod-example" }, true, default);
        await _service.RecordRunAsync(id, origin with { Environment = ConnectionEnvironment.Test }, false, default);
        var marks = (await _store.GetRunMarksAsync(created.Id, default))[id];
        Assert.NotNull(marks.Production);
        Assert.Null(marks.Test);
    }

    /// <summary>
    /// Platform 시험은 Language 를 참조하지 않으므로 대상 이름만 읽는 단순한 가짜(실제 렉서 판정은 Language.Tests 의 OwnerRewriterTests).
    /// <c>TABLE [o.]t</c> 를 찾고, 소유자가 없으면 붙인다. <c>ALTER SESSION SET CURRENT_SCHEMA = x</c> 를 읽는다.
    /// </summary>
    private sealed partial class FakeOwnerRewriter : IOwnerRewriter
    {
        public OwnerQualifyResult Qualify(string text, string currentSchema)
        {
            var m = TableName().Match(text);
            if (!m.Success)
            {
                return new OwnerQualifyResult(text, false, false, null, null);
            }

            if (m.Groups["o"].Success)
            {
                return new OwnerQualifyResult(text, true, false, m.Groups["o"].Value.ToUpperInvariant(), m.Groups["n"].Value.ToUpperInvariant());
            }

            var qualified = text.Insert(m.Groups["n"].Index, currentSchema + ".");
            return new OwnerQualifyResult(qualified, true, true, currentSchema, m.Groups["n"].Value.ToUpperInvariant());
        }

        public OwnerRenameResult Rewrite(string text, IReadOnlyDictionary<string, string> map) => new(text, false, false);

        public string? CurrentSchemaSwitch(string text)
        {
            var m = SchemaSwitch().Match(text);
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        [GeneratedRegex(@"\bTABLE\s+(?:(?<o>\w+)\.)?(?<n>\w+)", RegexOptions.IgnoreCase)]
        private static partial Regex TableName();

        [GeneratedRegex(@"^ALTER SESSION SET CURRENT_SCHEMA\s*=\s*(\w+)$", RegexOptions.IgnoreCase)]
        private static partial Regex SchemaSwitch();
    }
}
