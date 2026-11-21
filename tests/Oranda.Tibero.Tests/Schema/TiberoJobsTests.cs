using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using static Oranda.Tibero.Tests.Schema.SchemaFakes;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>
/// Tibero 작업(JOB): <c>*_objects</c> 에 JOB 유형이 없어(2026-11-21 실측) 스케줄러 잡·DBMS_JOB 을 <c>meta.jobs</c> 로 목록에 덧붙이고,
/// 표식(<c>meta.marks_jobs</c>)·상세(<c>meta.job_detail</c>)·DBMS_JOB DDL 을 따로 읽는다.
/// </summary>
public class TiberoJobsTests
{
    private static readonly Core.Catalog.SqlCatalog Catalog = TiberoEmbeddedCatalog.Load();

    private static readonly string[] ObjectColumns = ["OWNER", "OBJECT_NAME", "OBJECT_TYPE", "STATUS", "LAST_DDL_TIME", "CREATED"];

    [Fact]
    public async Task The_full_list_appends_jobs_after_the_objects_query()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, Recording(calls, ById(
            ("meta.objects", Table(ObjectColumns, ["SCOTT", "EMP", "TABLE", "VALID", null, null], ["SCOTT", "V", "VIEW", "VALID", null, null])),
            ("meta.jobs", Table(ObjectColumns, ["SCOTT", "10", "JOB", "VALID", null, null], ["SCOTT", "NIGHTLY", "JOB", "VALID", null, null])))));
        await using var session = Session();

        var all = await provider.RefreshAsync(session, CancellationToken.None);
        var jobs = await provider.ListObjectsAsync(session, new ObjectQuery { Owner = "SCOTT", ObjectTypes = ["JOB"] }, CancellationToken.None);

        Assert.Equal(["meta.objects", "meta.jobs"], calls.Select(c => c.Id));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = null, ["name_pattern"] = null }, calls[1].Binds);
        Assert.Equal(4, all.Count);
        Assert.Equal([new DbObjectRef("SCOTT", "10", "JOB"), new DbObjectRef("SCOTT", "NIGHTLY", "JOB")], jobs);
    }

    [Fact]
    public void Jobs_are_wanted_only_for_conditions_a_job_row_can_meet()
    {
        Assert.True(TiberoMetadataProvider.WantsJobs(new ObjectQuery()));
        Assert.True(TiberoMetadataProvider.WantsJobs(new ObjectQuery { ObjectTypes = ["TABLE", "JOB"] }));
        Assert.False(TiberoMetadataProvider.WantsJobs(new ObjectQuery { ObjectTypes = ["TABLE"] }));
        Assert.False(TiberoMetadataProvider.WantsJobs(new ObjectQuery { InvalidOnly = true })); // 작업 줄은 늘 VALID
        Assert.False(TiberoMetadataProvider.WantsJobs(new ObjectQuery { CreatedAfter = DateTimeOffset.UnixEpoch })); // 시각이 없다
        Assert.False(TiberoMetadataProvider.WantsJobs(new ObjectQuery { ModifiedAfter = DateTimeOffset.UnixEpoch }));
        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name_pattern"] = "E%" },
            TiberoMetadataProvider.JobBinds(new ObjectQuery { Owner = "SCOTT", NamePattern = "E%", Limit = 3 }));
    }

    [Fact]
    public async Task A_failing_job_query_leaves_the_object_list()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, id => id == "meta.jobs"
            ? throw ServerError(-17004, "Permission denied.")
            : Table(ObjectColumns, ["SCOTT", "EMP", "TABLE", "VALID", null, null])));
        await using var session = Session();

        var all = await provider.RefreshAsync(session, CancellationToken.None);

        Assert.Equal(["meta.objects", "meta.jobs"], calls.Select(c => c.Id));
        Assert.Equal("EMP", Assert.Single(all).Name);
    }

    [Fact]
    public async Task Rows_of_other_types_from_the_job_query_are_dropped_and_the_limit_counts_jobs()
    {
        var provider = new TiberoMetadataProvider(Recording([], id => id == "meta.jobs"
            ? Table(ObjectColumns, ["A", "J1", "JOB", "VALID", null, null], ["A", "T9", "TABLE", "VALID", null, null])
            : Table(ObjectColumns, ["A", "T1", "TABLE", "VALID", null, null], ["B", "T2", "TABLE", "VALID", null, null])));
        await using var session = Session();

        var limited = await provider.ListObjectsAsync(session, new ObjectQuery { Limit = 2 }, CancellationToken.None);

        Assert.Equal([new DbObjectRef("A", "J1", "JOB"), new DbObjectRef("A", "T1", "TABLE")], limited); // 소유자·유형·이름 차례
    }

    [Fact]
    public void Job_catalog_files_read_both_kinds_and_map_the_owner_number_to_a_name()
    {
        foreach (var id in new[] { "meta.jobs", ObjectMarkRows.Jobs, "meta.job_detail" })
        {
            var sql = Catalog.Get(id, 6, Dbms.Tibero).Sql;
            Assert.Contains("@PFX@_scheduler_jobs", sql, StringComparison.Ordinal);
            Assert.Contains("@PFX@_jobs d", sql, StringComparison.Ordinal);
            Assert.Contains("@PFX@_jobs_with_name n ON n.job = d.job", sql, StringComparison.Ordinal);
            Assert.Contains("all_users u ON u.user_id = d.schema_user", sql, StringComparison.Ordinal); // schema_user 는 사용자 번호(실측)
            Assert.Contains("NVL(n.name, TO_CHAR(d.job))", sql, StringComparison.Ordinal);
        }

        var marks = Catalog.Get(ObjectMarkRows.Jobs, 6, Dbms.Tibero).Sql;
        Assert.Contains("WHEN d.broken = 'Y' THEN 'BROKEN' WHEN d.failures > 0 THEN 'FAILED'", marks, StringComparison.Ordinal);
        Assert.Contains("(j.enabled <> 'TRUE' OR j.state <> 'SCHEDULED')", marks, StringComparison.Ordinal); // Oracle 판과 같은 규칙
        Assert.Contains(ObjectMarkRows.Jobs, TiberoMetadataProvider.MarkIds);
    }

    [Fact]
    public void Mark_rows_of_both_kinds_land_on_job_objects()
    {
        object?[][] rows = [["NIGHTLY", "FALSE", "DISABLED", null], ["10", "TRUE", "BROKEN", "DBMS_JOB"], ["11", "TRUE", null, "DBMS_JOB"]];

        var marks = ObjectMarkRows.Read(ObjectMarkRows.Jobs, "SCOTT", rows).ToDictionary(m => m.Ref, m => m.Mark);

        Assert.Equal(new ObjectMark(null, "DISABLED"), marks[new DbObjectRef("SCOTT", "NIGHTLY", "JOB")]);
        Assert.Equal(new ObjectMark("DBMS_JOB", "BROKEN"), marks[new DbObjectRef("SCOTT", "10", "JOB")]);
        Assert.Equal(new ObjectMark("DBMS_JOB", null), marks[new DbObjectRef("SCOTT", "11", "JOB")]);
    }

    private static TiberoSessionQuery.Table DetailRow(params object?[] row) =>
        Table(["JOB_KIND", "JOB", "ENABLED", "STATE", "NEXT_RUN", "RUN_INTERVAL", "WHAT", "FAILURES", "LAST_RUN", "BROKEN", "JOB_TYPE", "COMMENTS"], row);

    private static readonly object?[] DbmsJob =
        ["DBMS_JOB", 10m, null, null, "2026-11-22 07:00:00", "trunc(sysdate)+1+7/24", "begin APP.DAILY_PL('x'); end;", 0m, "2026-11-21 07:00:04", "Y", null, null];

    [Fact]
    public async Task A_dbms_job_detail_shows_its_schedule_and_what()
    {
        var calls = new List<(string Id, IReadOnlyDictionary<string, object?>? Binds)>();
        var provider = new TiberoMetadataProvider(Recording(calls, ById(("meta.job_detail", DetailRow(DbmsJob)))));
        await using var session = Session();
        var job = new DbObjectRef("SCOTT", "10", "JOB");

        var general = Assert.Single(await provider.GetDetailPartAsync(session, job, DetailPart.General, CancellationToken.None));

        Assert.Equal(new Dictionary<string, object?> { ["owner"] = "SCOTT", ["name"] = "10" }, Assert.Single(calls).Binds);
        Assert.Equal("일반", general.Title);
        var values = general.Rows.ToDictionary(r => (string)r[0]!, r => r[1]);
        Assert.Equal("DBMS_JOB", values["종류"]);
        Assert.Equal("10", values["작업 번호"]);
        Assert.Equal("Y", values["고장(BROKEN)"]);
        Assert.Equal("2026-11-22 07:00:00", values["다음 실행"]);
        Assert.Equal("trunc(sysdate)+1+7/24", values["실행 간격"]);
        Assert.Equal("begin APP.DAILY_PL('x'); end;", values["실행 내용(WHAT)"]);
        Assert.Equal("0", values["실패 수"]);
        Assert.Equal("2026-11-21 07:00:04", values["마지막 실행"]);
        Assert.DoesNotContain("사용", values.Keys); // 스케줄러 잡 칸은 없다
    }

    [Fact]
    public async Task A_scheduler_job_detail_shows_enabled_state_and_type_and_a_missing_job_has_no_section()
    {
        var scheduler = new TiberoMetadataProvider(Recording([], ById(("meta.job_detail", DetailRow(
            "SCHEDULER", null, "FALSE", "DISABLED", null, "FREQ=DAILY", "BEGIN NULL; END;", 2m, null, null, "PLSQL_BLOCK", "night")))));
        var none = new TiberoMetadataProvider(Recording([], _ => Table(["JOB_KIND"])));
        await using var session = Session();
        var job = new DbObjectRef("SCOTT", "NIGHTLY", "JOB");

        var values = Assert.Single(await scheduler.GetDetailPartAsync(session, job, DetailPart.General, CancellationToken.None)).Rows.ToDictionary(r => (string)r[0]!, r => r[1]);

        Assert.Equal(("DBMS_SCHEDULER", "FALSE", "DISABLED", "PLSQL_BLOCK", "night"), (values["종류"], values["사용"], values["상태"], values["작업 종류"], values["설명"]));
        Assert.DoesNotContain("작업 번호", values.Keys);
        Assert.Empty(await none.GetDetailPartAsync(session, job, DetailPart.General, CancellationToken.None));
    }

    [Fact]
    public async Task A_dbms_job_ddl_is_a_submit_block_and_a_scheduler_job_ddl_is_not_supported()
    {
        var dbms = new TiberoMetadataProvider(Recording([], ById(("meta.job_detail", DetailRow(DbmsJob)))));
        var scheduler = new TiberoMetadataProvider(Recording([], ById(("meta.job_detail", DetailRow(
            "SCHEDULER", null, "TRUE", "SCHEDULED", null, null, "BEGIN NULL; END;", 0m, null, null, "PLSQL_BLOCK", null)))));
        var none = new TiberoMetadataProvider(Recording([], _ => Table(["JOB_KIND"])));
        await using var session = Session();
        var job = new DbObjectRef("SCOTT", "10", "JOB");

        var ddl = await dbms.GetDdlAsync(session, job, new DdlOptions(), CancellationToken.None);

        Assert.Equal(
            "DECLARE\n  l_job NUMBER;\nBEGIN\n"
            + "  DBMS_JOB.SUBMIT(l_job, 'begin APP.DAILY_PL(''x''); end;', TO_DATE('2026-11-22 07:00:00', 'YYYY-MM-DD HH24:MI:SS'), 'trunc(sysdate)+1+7/24');\n"
            + "  DBMS_JOB.BROKEN(l_job, TRUE);\n  COMMIT;\nEND;\n/",
            ddl);
        await Assert.ThrowsAsync<NotSupportedException>(() => scheduler.GetDdlAsync(session, job, new DdlOptions(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => none.GetDdlAsync(session, job, new DdlOptions(), CancellationToken.None));
    }

    [Fact]
    public void A_dbms_job_without_next_date_or_interval_submits_sysdate_and_null()
    {
        Assert.Equal(
            "DECLARE\n  l_job NUMBER;\nBEGIN\n  DBMS_JOB.SUBMIT(l_job, 'x;', SYSDATE, NULL);\n  COMMIT;\nEND;",
            TiberoDdlAssembler.DbmsJob("x;", null, null, broken: false, terminator: false));
    }
}
