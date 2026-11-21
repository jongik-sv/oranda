using System.Globalization;
using Oranda.Core.Metadata;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Schema;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// 실제 Tibero 로 원격 동의어(<c>"이름"@링크</c>)·작업(JOB) 목록·표식·상세·DDL 을 본다(2026-11-21 실측 반영).
/// 서버마다 있는 객체가 달라 「있으면」 식으로 검사하고, 출력에는 개수만 싣는다(이름·계정·링크를 싣지 않는다). 쓰기 시험은 <c>ORT_TM_</c> 표시만 만들고 지운다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoJobsAndRemoteSynonymsIt(ITestOutputHelper output)
{
    private const string PublicFilter = "AND s.owner <> 'PUBLIC'";

    [TiberoItFact]
    public async Task Remote_synonyms_are_marked_remote_with_their_link_and_resolve_without_the_link_in_the_name()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var remote = await TiberoSessionQuery.RunAsync(session,
            "SELECT owner, synonym_name FROM all_synonyms WHERE INSTR(org_object_name, '@') > 0 AND ROWNUM <= 5");
        output.WriteLine(Line($"[원격 동의어] {remote.Rows.Count}개(최대 5)"));
        if (remote.Rows.Count == 0)
        {
            return; // 이 서버에는 원격 동의어가 없다
        }

        var provider = new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync);
        foreach (var owner in remote.Rows.Select(r => (string)r[0]!).Distinct(StringComparer.Ordinal))
        {
            // 표식 질의는 PUBLIC 을 읽지 않는다(수가 많아서) — 여기서는 같은 문장에서 그 조건만 떼어 PUBLIC 원격 동의어에도 같은 규칙이 맞는지 본다.
            var sql = TiberoCatalogQuery.Sql(session, ObjectMarkRows.Synonyms);
            Assert.Contains(PublicFilter, sql, StringComparison.Ordinal);
            var rows = await TiberoSessionQuery.RunAsync(session, sql.Replace(PublicFilter, string.Empty, StringComparison.Ordinal),
                c => TiberoSessionQuery.Bind(c, "owner", owner));
            var marks = ObjectMarkRows.Read(ObjectMarkRows.Synonyms, owner, rows.Rows).ToDictionary(m => m.Ref.Name, m => m.Mark, StringComparer.Ordinal);
            foreach (var name in remote.Rows.Where(r => (string)r[0]! == owner).Select(r => (string)r[1]!))
            {
                Assert.True(marks.TryGetValue(name, out var mark), "원격 동의어 표식이 없다");
                Assert.True(mark!.Has("REMOTE"), "원격 동의어가 REMOTE 가 아니다: " + mark.SubKind + "/" + mark.State);
                Assert.Null(mark.State); // BROKEN 아님

                var step = await provider.GetNameStepAsync(session, owner, name, CancellationToken.None);
                Assert.NotNull(step);
                Assert.True(step!.IsSynonym);
                Assert.False(string.IsNullOrEmpty(step.DbLink), "이름 풀이에 DB 링크가 없다");
                Assert.DoesNotContain("@", step.TargetName, StringComparison.Ordinal);
                Assert.DoesNotContain("\"", step.TargetName, StringComparison.Ordinal);

                var ddl = await provider.GetDdlAsync(session, new DbObjectRef(owner, name, "SYNONYM"), new DdlOptions { SqlTerminator = false }, CancellationToken.None);
                Assert.EndsWith("@" + step.DbLink, ddl, StringComparison.Ordinal);
            }
        }
    }

    [TiberoItFact]
    public async Task Job_queries_run_and_visible_jobs_land_in_the_object_list_with_detail()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);

        var all = await provider.RefreshAsync(session, CancellationToken.None);
        var jobs = all.Where(o => o.Type == TiberoMetadataProvider.JobType).ToList();
        var own = await TiberoSessionQuery.RunAsync(session, "SELECT COUNT(*) FROM user_jobs");
        var ownScheduler = await TiberoSessionQuery.RunAsync(session, "SELECT COUNT(*) FROM user_scheduler_jobs");
        output.WriteLine(Line($"[작업] 목록 {jobs.Count}개 · 자기 DBMS_JOB {own.Rows[0][0]}개 · 자기 스케줄러 잡 {ownScheduler.Rows[0][0]}개 (사전 {(session.Server.Capabilities.CanQueryDictionary ? "dba" : "all")})"));
        Assert.Equal(Convert.ToInt32(own.Rows[0][0], CultureInfo.InvariantCulture) + Convert.ToInt32(ownScheduler.Rows[0][0], CultureInfo.InvariantCulture),
            jobs.Count(j => j.Owner == owner));

        var marks = await TiberoCatalogQuery.RunAsync(session, ObjectMarkRows.Jobs, new Dictionary<string, object?> { ["owner"] = owner }, CancellationToken.None);
        var read = ObjectMarkRows.Read(ObjectMarkRows.Jobs, owner, marks.Rows);
        output.WriteLine(Line($"[작업 표식] {read.Count}개 · DBMS_JOB {read.Count(m => m.Mark.Has("DBMS_JOB"))}개"));
        Assert.All(read, m => Assert.Contains(jobs, j => j.Owner == owner && j.Name == m.Ref.Name)); // 표식은 목록에 있는 작업에 붙는다

        // 상세 질의는 없는 이름에도 돈다(문장이 이 서버에서 읽힌다).
        Assert.Empty(await provider.GetDetailPartAsync(session, new DbObjectRef(owner, "ORT_TM_NO_SUCH_JOB", "JOB"), DetailPart.General, CancellationToken.None));
        if (jobs.FirstOrDefault(j => j.Owner == owner) is { } first)
        {
            var job = new DbObjectRef(first.Owner, first.Name, "JOB");
            var general = Assert.Single(await provider.GetDetailPartAsync(session, job, DetailPart.General, CancellationToken.None));
            Assert.Contains(general.Rows, r => (string?)r[0] == "다음 실행");
        }
    }

    [TiberoWriteFact]
    public async Task A_broken_dbms_job_is_listed_marked_described_and_scripted()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        const string What = "BEGIN NULL; /*ORT_TM_JOB*/ END;";
        var remove = $"BEGIN FOR r IN (SELECT job FROM user_jobs WHERE what = '{What}') LOOP DBMS_JOB.REMOVE(r.job); END LOOP; COMMIT; END;";
        await ExecAsync(session, remove); // 지난 실행이 남긴 것
        try
        {
            await ExecAsync(session, $"DECLARE j NUMBER; BEGIN DBMS_JOB.SUBMIT(j, '{What}', SYSDATE + 1, 'SYSDATE + 1'); DBMS_JOB.BROKEN(j, TRUE); COMMIT; END;");
            var number = Convert.ToString((await TiberoSessionQuery.RunAsync(session, $"SELECT job FROM user_jobs WHERE what = '{What}'")).Rows.Single()[0], CultureInfo.InvariantCulture)!;
            var job = new DbObjectRef(owner, number, "JOB");

            var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);
            await provider.RefreshAsync(session, CancellationToken.None);
            Assert.Contains(job, await provider.ListObjectsAsync(session, new Core.Metadata.ObjectQuery { Owner = owner, ObjectTypes = ["JOB"] }, CancellationToken.None));

            var marks = await TiberoCatalogQuery.RunAsync(session, ObjectMarkRows.Jobs, new Dictionary<string, object?> { ["owner"] = owner }, CancellationToken.None);
            var mark = ObjectMarkRows.Read(ObjectMarkRows.Jobs, owner, marks.Rows).Single(m => m.Ref == job).Mark;
            Assert.Equal(new ObjectMark("DBMS_JOB", "BROKEN"), mark);

            var values = Assert.Single(await provider.GetDetailPartAsync(session, job, DetailPart.General, CancellationToken.None)).Rows
                .ToDictionary(r => (string)r[0]!, r => r[1]);
            Assert.Equal(("DBMS_JOB", number, "Y", What, "SYSDATE + 1"), (values["종류"], values["작업 번호"], values["고장(BROKEN)"], values["실행 내용(WHAT)"], values["실행 간격"]));
            Assert.False(string.IsNullOrEmpty((string?)values["다음 실행"]));

            var ddl = await provider.GetDdlAsync(session, job, new DdlOptions(), CancellationToken.None);
            Assert.Contains("DBMS_JOB.SUBMIT(l_job, 'BEGIN NULL; /*ORT_TM_JOB*/ END;', TO_DATE('", ddl, StringComparison.Ordinal);
            Assert.Contains("DBMS_JOB.BROKEN(l_job, TRUE);", ddl, StringComparison.Ordinal);
        }
        finally
        {
            await ExecAsync(session, remove);
        }

        Assert.Empty((await TiberoSessionQuery.RunAsync(session, $"SELECT job FROM user_jobs WHERE what = '{What}'")).Rows);
    }

    [TiberoWriteFact]
    public async Task A_disabled_scheduler_job_is_listed_and_marked_when_the_account_can_create_one()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var session = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        var name = TiberoItSessions.ObjectName("TM_JOB");
        var drop = $"BEGIN DBMS_SCHEDULER.DROP_JOB('{name}'); END;";
        await TryExecAsync(session, drop);
        try
        {
            try
            {
                await ExecAsync(session, $"BEGIN DBMS_SCHEDULER.CREATE_JOB(job_name => '{name}', job_type => 'PLSQL_BLOCK', job_action => 'BEGIN NULL; END;', repeat_interval => 'FREQ=DAILY', enabled => FALSE); END;");
            }
            catch (Exception ex) when (TiberoErrorClassifier.ToDbError(ex, null) is { } e)
            {
                output.WriteLine("스케줄러 잡을 만들지 못해 건너뜀: " + e.Code); // 2026-11-21 시험 계정 = ODP-14002
                return;
            }

            var provider = new TiberoMetadataProvider(new MemoryMetadataCache(), new RecordingEventBus(), Timeout.InfiniteTimeSpan, TiberoCatalogQuery.RunAsync);
            await provider.RefreshAsync(session, CancellationToken.None);
            var job = new DbObjectRef(owner, name, "JOB");
            Assert.Contains(job, await provider.ListObjectsAsync(session, new Core.Metadata.ObjectQuery { Owner = owner, ObjectTypes = ["JOB"] }, CancellationToken.None));
            var marks = await TiberoCatalogQuery.RunAsync(session, ObjectMarkRows.Jobs, new Dictionary<string, object?> { ["owner"] = owner }, CancellationToken.None);
            Assert.Equal("DISABLED", ObjectMarkRows.Read(ObjectMarkRows.Jobs, owner, marks.Rows).Single(m => m.Ref == job).Mark.StateCode);
            await Assert.ThrowsAsync<NotSupportedException>(() => provider.GetDdlAsync(session, job, new DdlOptions(), CancellationToken.None));
        }
        finally
        {
            await TryExecAsync(session, drop);
        }
    }

    private static string Line(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static async Task TryExecAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
        catch (Exception ex) when (TiberoErrorClassifier.ToDbError(ex, sql) is not null)
        {
            // 없는 작업 지우기
        }
    }

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
