using Oranda.App.Features.Execution;
using Oranda.App.Features.Flashback;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Metadata;
using Oranda.Core.Settings;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>TSK-13-31 「변경 추적하기」: 새 SQL 탭에 넣는 이력 질의 글과 새 탭 열기·실행.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class FlashbackTrackTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public void Single_primary_key_reads_a_bounded_recent_window_newest_first()
    {
        // 전체 기간(SCN MINVALUE)은 운영 DB 에서 ORA-08180 이 난다. 최근 N일(SYSDATE - N)부터 지금(MAXVALUE: 방금 커밋한 변경까지)을 읽는다.
        var sql = FlashbackTrackSql.Build(Emp, ["EMPNO", "ENAME"], ["EMPNO"], [7369m]);

        var lines = sql.Split('\n');
        Assert.StartsWith("-- SCOTT.EMP 의 변경 이력(키: EMPNO = 7369)", lines[0], StringComparison.Ordinal);
        Assert.Contains("undo 보존 기간과 표 정의 변경 시점까지만 보입니다", lines[0], StringComparison.Ordinal);
        Assert.Contains("기간: 최근 1.5일(SYSDATE - 1.5)은 고쳐 다시 돌리거나 설정 > 결과 그리드 「변경 추적 기간」에서 바꿀 수 있습니다", lines[0], StringComparison.Ordinal);
        Assert.Contains("ORA-08180", lines[0], StringComparison.Ordinal);
        Assert.Equal("SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, EMPNO, ENAME", lines[1]);
        Assert.Equal("FROM SCOTT.EMP VERSIONS BETWEEN TIMESTAMP SYSDATE - 1.5 AND MAXVALUE", lines[2]);
        Assert.DoesNotContain("MINVALUE", sql, StringComparison.Ordinal);
        Assert.Equal("WHERE EMPNO = 7369", lines[3]);
        Assert.Equal("ORDER BY VERSIONS_STARTSCN DESC NULLS LAST", lines[4]);
        Assert.DoesNotContain(":", string.Join('\n', lines[1..]), StringComparison.Ordinal); // 바인드 없이 리터럴
    }

    [Fact]
    public void Window_in_days_can_be_set_and_is_written_invariantly()
    {
        var sql = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], days: 0.25m);

        Assert.Contains("FROM SCOTT.EMP VERSIONS BETWEEN TIMESTAMP SYSDATE - 0.25 AND MAXVALUE", sql, StringComparison.Ordinal);
        Assert.Contains("최근 0.25일(SYSDATE - 0.25)", sql, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], days: 0m));
    }

    [Fact]
    public void Whole_period_setting_reads_scn_minvalue_and_warns_about_ora_08180()
    {
        var lines = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], days: null, lastDdl: new DateTime(2026, 10, 27)).Split('\n');

        Assert.Equal("FROM SCOTT.EMP VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE", lines[2]);
        Assert.Contains("기간: 가능한 전체 기간(SCN MINVALUE). DB 에 따라 ORA-08180 이 날 수 있으며 설정 > 결과 그리드 「변경 추적 기간」에서 최근 N일로 바꿀 수 있습니다", lines[0], StringComparison.Ordinal);
        Assert.DoesNotContain("GREATEST", lines[2], StringComparison.Ordinal); // 전체 기간은 DDL 시각으로 자르지 않는다
    }

    [Theory]
    [InlineData(FlashbackTrackRange.RecentDays, 2.0, "TIMESTAMP SYSDATE - 2 AND MAXVALUE")]
    [InlineData(FlashbackTrackRange.All, 2.0, "SCN MINVALUE AND MAXVALUE")]
    public void Settings_decide_the_period(FlashbackTrackRange range, double days, string expected)
    {
        var grid = new GridSettings { FlashbackTrackRange = range, FlashbackTrackDays = days };

        var sql = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], days: FlashbackTrackSql.DaysFrom(grid));

        Assert.Contains("VERSIONS BETWEEN " + expected, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Recent_table_definition_change_moves_the_window_start_after_it()
    {
        // 기간 시작이 마지막 DDL 보다 앞이면 ORA-01466. 시각→SCN 대응이 몇 초 단위라 10초 뒤부터 읽는다(26ai·11.2 에서 확인).
        var ddl = new DateTime(2026, 10, 27, 13, 43, 18);

        var sql = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], lastDdl: ddl);

        Assert.Contains("FROM SCOTT.EMP VERSIONS BETWEEN TIMESTAMP GREATEST(SYSDATE - 1.5, TO_DATE('2026-10-27 13:43:18', 'YYYY-MM-DD HH24:MI:SS') + INTERVAL '10' SECOND) AND MAXVALUE",
            sql, StringComparison.Ordinal);
        Assert.Contains("표 정의가 마지막으로 바뀐 시각(2026-10-27 13:43:18)", sql.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Other_schema_table_adds_flashback_privilege_hint_to_comment_only()
    {
        var other = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], "HR").Split('\n')[0];
        var own = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [1m], "scott").Split('\n')[0];

        Assert.EndsWith("다른 스키마 표는 FLASHBACK 권한이 있어야 합니다.", other, StringComparison.Ordinal);
        Assert.DoesNotContain("다른 스키마", own, StringComparison.Ordinal);
    }

    [Fact]
    public void Composite_key_joins_conditions_and_string_quotes_are_escaped()
    {
        var sql = FlashbackTrackSql.Build(Emp, ["A", "B"], ["A", "B"], ["O'Neil", 5]);

        Assert.Contains("WHERE A = 'O''Neil' AND B = 5", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Rowid_key_uses_chartorowid_and_date_keys_use_to_date_and_to_timestamp()
    {
        var byRowId = FlashbackTrackSql.Build(Emp, ["ROWID", "ENAME"], ["ROWID"], ["AAAB1cAAEAAAACXAAA"]);
        Assert.Contains("WHERE ROWID = CHARTOROWID('AAAB1cAAEAAAACXAAA')", byRowId, StringComparison.Ordinal);
        Assert.Contains("VERSIONS_XID, ROWID, ENAME", byRowId, StringComparison.Ordinal);

        var day = FlashbackTrackSql.Build(Emp, ["D"], ["D"], [new DateTime(2026, 10, 22, 14, 30, 5)]);
        Assert.Contains("WHERE D = TO_DATE('2026-10-22 14:30:05', 'YYYY-MM-DD HH24:MI:SS')", day, StringComparison.Ordinal);

        var ts = FlashbackTrackSql.Build(Emp, ["T"], ["T"], [new DateTime(2026, 10, 22, 14, 30, 5).AddTicks(1230)]);
        Assert.Contains("WHERE T = TO_TIMESTAMP('2026-10-22 14:30:05.000123', 'YYYY-MM-DD HH24:MI:SS.FF6')", ts, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_key_value_is_refused()
    {
        Assert.Throws<ArgumentException>(() => FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [null]));
    }

    [Fact]
    public Task Open_and_run_puts_the_text_in_a_new_tab_with_the_connection_and_executes_it() => Ui.Run(async () =>
    {
        using var rig = new Rig();
        var seen = new List<string>();
        rig.Exec.Handler = (s, _) =>
        {
            seen.Add(s.Text);
            return Task.FromResult(Rig.Query(s));
        };
        var text = FlashbackTrackSql.Build(Emp, ["EMPNO"], ["EMPNO"], [7369m]);

        var doc = await FlashbackCommandHandler.OpenAndRunAsync(rig.Host, text, rig.Profile.Id,
            ct => rig.Controller.RunAsync(RunMode.Statement, ct: ct), CancellationToken.None);

        Assert.Same(doc, rig.Host.Active);
        Assert.Equal(text, doc.Text);
        Assert.Equal(rig.Profile.Id, doc.ConnectionProfileId);
        var ran = Assert.Single(seen);
        Assert.StartsWith("SELECT VERSIONS_STARTTIME", ran, StringComparison.Ordinal); // 머리 주석은 문장에서 빠진다
        Assert.Single(((ResultTabsViewModel)doc.ResultPane!).Tabs);
    });
}
