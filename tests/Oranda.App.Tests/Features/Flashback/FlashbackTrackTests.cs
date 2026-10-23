using Oranda.App.Features.Execution;
using Oranda.App.Features.Flashback;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>TSK-13-31 「변경 추적하기」: 새 SQL 탭에 넣는 이력 질의 글과 새 탭 열기·실행.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class FlashbackTrackTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public void Single_primary_key_reads_the_whole_flashback_range_newest_first()
    {
        var sql = FlashbackTrackSql.Build(Emp, ["EMPNO", "ENAME"], ["EMPNO"], [7369m]);

        var lines = sql.Split('\n');
        Assert.StartsWith("-- SCOTT.EMP 의 변경 이력(키: EMPNO = 7369)", lines[0], StringComparison.Ordinal);
        Assert.Contains("undo 보존 기간과 표 정의 변경 시점까지만 보입니다", lines[0], StringComparison.Ordinal);
        Assert.Equal("SELECT VERSIONS_STARTTIME, VERSIONS_ENDTIME, VERSIONS_OPERATION, RAWTOHEX(VERSIONS_XID) AS VERSIONS_XID, EMPNO, ENAME", lines[1]);
        Assert.Equal("FROM SCOTT.EMP VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE", lines[2]);
        Assert.Equal("WHERE EMPNO = 7369", lines[3]);
        Assert.Equal("ORDER BY VERSIONS_STARTSCN DESC NULLS LAST", lines[4]);
        Assert.DoesNotContain(":", string.Join('\n', lines[1..]), StringComparison.Ordinal); // 바인드 없이 리터럴
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
