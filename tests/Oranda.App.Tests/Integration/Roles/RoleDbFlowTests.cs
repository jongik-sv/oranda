using Oranda.App.Features.Execution;
using Oranda.App.Features.LobRowView;
using Oranda.App.Shell.Features;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Features;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration.Roles;

/// <summary>
/// TSK-07-08 통합 5(실제 Oracle): 조회 전용(R6) 실행 차단을 <b>앱 조립 전체</b>(실제 FeatureGate → 정책 관문 → 실행 엔진 → 드라이버)로 본다.
/// 앱 명령(<c>oranda.exec.runStatement</c>·<c>runScript</c>·<c>oranda.txn.commit</c>)과 LOB 저장 경로를 그대로 거치고, 거부된 문장이 서버에 아무것도 남기지 않았음을
/// 앱 밖의 다른 연결로 확인한다. 같은 시험을 26ai(FREEPDB1)와 11.2(oranda-ora11g XE)에서 돌린다(환경 변수 ORANDA_IT_HOST/PORT/SERVICE/ADMIN_*).
/// ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다(ORANDA_IT_REQUIRE=1 이면 실패).
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class RoleDbFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private const string Denied = "조회 전용 Role 에서는";

    private static string NewName() => "RL_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private bool Skip()
    {
        if (db.Enabled)
        {
            return false;
        }

        if (Environment.GetEnvironmentVariable("ORANDA_IT_REQUIRE") == "1")
        {
            throw new InvalidOperationException("ORANDA_IT_ADMIN_PASSWORD 가 없다.");
        }

        return true;
    }

    private static async Task SetRolesAsync(FlowRig rig, params string[]? roles)
    {
        var gate = (FeatureGate)rig.Container.GetRequiredService<IFeatureGate>();
        var want = roles is { Length: 1 } && roles[0] == RoleIds.Viewer;
        await rig.Container.GetRequiredService<SettingsService>().UpdateAsync(s => s with { Features = new FeaturesSettings { Roles = roles } });
        await HeadlessHost.WaitUntil(() => gate.ReadOnlyRole == want); // 설정 저장이 알림을 내면 게이트가 바로 다시 해석한다
    }

    private static string Message(ResultTabsViewModel tabs) => string.Join(" | ", tabs.Tabs.Select(t => t.Message));

    private long Count(string sql) => Convert.ToInt64(db.Scalar(sql), System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public Task Viewer_only_runs_select_and_denies_every_write_with_nothing_left_on_the_server() => Ui.Run(async () =>
    {
        if (Skip())
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewName();
        var fresh = NewName();
        db.Exec($"create table {t} (id number primary key, v varchar2(30))");
        db.Exec($"insert into {t} values (1, 'a')");
        try
        {
            await rig.ConnectAsync();
            await SetRolesAsync(rig, RoleIds.Viewer);
            var objectsBefore = Count("select count(*) from user_objects");
            output.WriteLine($"서버 {rig.Session.Server.Version} / 시험 사용자 {db.User}");

            // 조회는 실행된다.
            var ok = await rig.RunSqlAsync($"select id, v from {t}");
            var item = Assert.Single(ok.Tabs);
            Assert.Equal("1", await FlowRig.CellAsync(item, 0, 0));
            Assert.Equal("a", await FlowRig.CellAsync(item, 0, 1));
            Assert.DoesNotContain(Denied, Message(ok), StringComparison.Ordinal);

            // 쓰기·DDL·익명 블록·트랜잭션 잠금: 드라이버에 보내기 전에 거부된다.
            string[] writes =
            [
                $"update {t} set v = 'x' where id = 1",
                $"update {t} set v = 'x'",
                $"delete from {t} where id = 1",
                $"insert into {t} values (2, 'b')",
                $"merge into {t} d using dual s on (d.id = 3) when not matched then insert (id, v) values (3, 'm')",
                $"create table {fresh} (x number)",
                $"alter table {t} add (extra number)",
                $"truncate table {t}",
                $"drop table {t}",
                $"begin insert into {t} values (4, 'blk'); commit; end;",
                $"declare n number; begin delete from {t}; end;",
                $"create or replace procedure {fresh} as begin null; end;",
                $"comment on table {t} is 'x'",
                $"lock table {t} in exclusive mode",
                "alter session set nls_date_format = 'YYYY'",
                "alter session set statistics_level = all", // 계획 통계 재실행(R6 에서도 막힘, followups 07-05)
                "alter session set events '10053 trace name context forever, level 1'", // 옵티마이저 트레이스
            ];
            foreach (var sql in writes)
            {
                var tabs = await rig.RunSqlAsync(sql);
                var text = Message(tabs);
                Assert.True(text.Contains(Denied, StringComparison.Ordinal), $"거부 문구가 없다: {sql} → {text}");
                Assert.False(rig.Session.HasPendingTransaction, $"거부된 문장이 미커밋을 남겼다: {sql}");
            }

            // 서버에는 아무것도 들어가지 않았다(앱 밖 연결로 확인).
            Assert.Equal(1, Count($"select count(*) from {t}"));
            Assert.Equal("a", Convert.ToString(db.Scalar($"select v from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(0, Count($"select count(*) from user_tab_columns where table_name = '{t}' and column_name = 'EXTRA'"));
            Assert.Equal(0, Count($"select count(*) from user_objects where object_name = '{fresh}'"));
            Assert.Equal(objectsBefore, Count("select count(*) from user_objects"));
            Assert.Equal(0, Count($"select count(*) from {t} where v in ('x','b','m','blk')"));

            // 스크립트 실행(F5)도 같은 관문: 중간의 쓰기만 거부되고 서버는 그대로.
            var script = await rig.RunSqlAsync($"select 1 from dual;\ndelete from {t};\nselect 2 from dual;", "oranda.exec.runScript");
            Assert.Contains(Denied, Message(script), StringComparison.Ordinal);
            Assert.Equal(1, Count($"select count(*) from {t}"));

            // 트랜잭션 제어·EXPLAIN 은 막지 않는다(읽기 전용 프로필과 같은 기준).
            var explain = await rig.RunSqlAsync($"explain plan for select * from {t} where id = 1");
            Assert.DoesNotContain(Denied, Message(explain), StringComparison.Ordinal);
            var commit = await rig.RunSqlAsync("commit");
            Assert.DoesNotContain(Denied, Message(commit), StringComparison.Ordinal);
            Assert.False(rig.Session.HasPendingTransaction);
        }
        finally
        {
            Cleanup(t, fresh);
        }
    });

    [Fact]
    public Task Viewer_together_with_sql_developer_lets_the_same_statements_through_and_back_to_viewer_denies_again() => Ui.Run(async () =>
    {
        if (Skip())
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewName();
        var made = NewName();
        db.Exec($"create table {t} (id number primary key, v varchar2(30))");
        db.Exec($"insert into {t} values (1, 'a')");
        try
        {
            await rig.ConnectAsync();
            await SetRolesAsync(rig, RoleIds.Viewer, RoleIds.SqlDev); // R6 + R1: 차단하지 않는다(D-006)

            var update = await rig.RunSqlAsync($"update {t} set v = 'u' where id = 1");
            Assert.DoesNotContain(Denied, Message(update), StringComparison.Ordinal);
            Assert.True(rig.Session.HasPendingTransaction);
            Assert.Equal("a", Convert.ToString(db.Scalar($"select v from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture)); // 미커밋: 다른 연결에는 아직 안 보인다
            await rig.Run("oranda.txn.commit");
            Assert.Equal("u", Convert.ToString(db.Scalar($"select v from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture));

            var ddl = await rig.RunSqlAsync($"create table {made} (x number)");
            Assert.DoesNotContain(Denied, Message(ddl), StringComparison.Ordinal);
            Assert.Equal(1, Count($"select count(*) from user_tables where table_name = '{made}'"));

            var block = await rig.RunSqlAsync($"begin insert into {t} values (5, 'blk'); commit; end;");
            Assert.DoesNotContain(Denied, Message(block), StringComparison.Ordinal);
            Assert.Equal(1, Count($"select count(*) from {t} where id = 5"));

            // 되돌림: 다시 R6 만 → 같은 쓰기가 다음 문장부터 거부된다.
            await SetRolesAsync(rig, RoleIds.Viewer);
            var again = await rig.RunSqlAsync($"delete from {t} where id = 5");
            Assert.Contains(Denied, Message(again), StringComparison.Ordinal);
            Assert.Equal(1, Count($"select count(*) from {t} where id = 5"));

            // 모든 기능 쓰기(Role 고르지 않음)로 풀면 다시 지워진다.
            await rig.Container.GetRequiredService<SettingsService>().UpdateAsync(s => s with { Features = new FeaturesSettings() });
            await HeadlessHost.WaitUntil(() => !((FeatureGate)rig.Container.GetRequiredService<IFeatureGate>()).ReadOnlyRole);
            var del = await rig.RunSqlAsync($"delete from {t} where id = 5");
            Assert.DoesNotContain(Denied, Message(del), StringComparison.Ordinal);
            await rig.Run("oranda.txn.commit");
            Assert.Equal(0, Count($"select count(*) from {t} where id = 5"));
        }
        finally
        {
            Cleanup(t, made);
        }
    });

    [Fact]
    public Task Uncommitted_work_from_before_the_switch_can_still_be_committed_or_rolled_back_after_viewer_only() => Ui.Run(async () =>
    {
        // followups(07-05): 엔진 밖 Commit/Rollback 직접 호출 경로. 정책 관문은 트랜잭션 제어를 막지 않는다(읽기 전용 프로필과 같은 기준).
        // 그래서 Role 을 바꾸기 전에 쌓인 미커밋은 R6 만 고른 뒤에도 커밋·롤백된다. 지금 동작을 고정하고 결과서에 「사용자 결정」 으로 올린다.
        if (Skip())
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewName();
        db.Exec($"create table {t} (id number primary key, v varchar2(30))");
        db.Exec($"insert into {t} values (1, 'a')");
        db.Exec($"insert into {t} values (2, 'b')");
        try
        {
            await rig.ConnectAsync();
            await rig.RunSqlAsync($"update {t} set v = 'kept' where id = 1"); // 모든 기능 켬: 미커밋
            Assert.True(rig.Session.HasPendingTransaction);

            await SetRolesAsync(rig, RoleIds.Viewer);
            Assert.True(rig.Session.HasPendingTransaction); // 전환해도 미커밋은 그대로 남는다
            var denied = await rig.RunSqlAsync($"update {t} set v = 'blocked' where id = 2");
            Assert.Contains(Denied, Message(denied), StringComparison.Ordinal);

            await rig.Run("oranda.txn.commit"); // 앱의 커밋 명령(엔진 밖 직접 호출)은 R6 에서도 돈다
            Assert.False(rig.Session.HasPendingTransaction);
            Assert.Equal("kept", Convert.ToString(db.Scalar($"select v from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal("b", Convert.ToString(db.Scalar($"select v from {t} where id = 2"), System.Globalization.CultureInfo.InvariantCulture)); // 거부된 문장은 아무것도 못 했다

            // 롤백도 같다: R1 로 돌아가 한 번 더 쌓고 R6 로 바꾼 뒤 롤백.
            await SetRolesAsync(rig, RoleIds.Viewer, RoleIds.SqlDev);
            await rig.RunSqlAsync($"update {t} set v = 'gone' where id = 2");
            Assert.True(rig.Session.HasPendingTransaction);
            await SetRolesAsync(rig, RoleIds.Viewer);
            await rig.Run("oranda.txn.rollback");
            Assert.False(rig.Session.HasPendingTransaction);
            Assert.Equal("b", Convert.ToString(db.Scalar($"select v from {t} where id = 2"), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Cleanup(t);
        }
    });

    [Fact]
    public Task Viewer_only_blocks_the_lob_editor_save_path_that_stays_on_for_viewers() => Ui.Run(async () =>
    {
        // lobRowView 는 R6 기본 켬이다. LOB 저장은 DML 이라 엔진(정책 관문)에서 거부되어야 한다.
        if (Skip())
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewName();
        db.Exec($"create table {t} (id number primary key, c clob)");
        db.Exec($"insert into {t} values (1, 'orig')");
        try
        {
            await rig.ConnectAsync();
            await SetRolesAsync(rig, RoleIds.Viewer);
            var writer = new LobWriter(rig.Container.GetRequiredService<IExecutionService>());
            var target = new LobSaveTarget(new DbObjectRef(db.User, t, "TABLE"), "C", new Dictionary<string, object?> { ["ID"] = 1m });

            var denied = await writer.WriteAsync(rig.Session, target, new BindValue(BindDataType.Clob, "changed"), commit: true, default);

            Assert.False(denied.Success);
            Assert.Contains(Denied, denied.Message, StringComparison.Ordinal);
            Assert.Equal("orig", Convert.ToString(db.Scalar($"select c from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture));
            await rig.Session.RollbackAsync(default); // SAVEPOINT 가 남긴 미커밋 표시(DEF-04)

            // R6 + R1: 같은 저장이 된다.
            await SetRolesAsync(rig, RoleIds.Viewer, RoleIds.SqlDev);
            var ok = await writer.WriteAsync(rig.Session, target, new BindValue(BindDataType.Clob, "changed"), commit: true, default);
            Assert.True(ok.Success, ok.Message);
            Assert.Equal("changed", Convert.ToString(db.Scalar($"select c from {t} where id = 1"), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            Cleanup(t);
        }
    });

    private void Cleanup(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                db.Exec($"drop table {n} purge");
            }
            catch (global::Oracle.ManagedDataAccess.Client.OracleException)
            {
                // 만들지 않았거나 이미 없다.
            }

            try
            {
                db.Exec($"drop procedure {n}");
            }
            catch (global::Oracle.ManagedDataAccess.Client.OracleException)
            {
            }
        }
    }
}
