using Oranda.App.Features.ChangeJournal;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.ChangeJournal;

/// <summary>CJ-01 문장 목록의 바인드 값 치환(표시 전용), CJ-02 오른쪽 아래 칸의 원복 스크립트 미리 보기.</summary>
public sealed class ChangeJournalLiteralPreviewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 5, 0, 0, TimeSpan.Zero);
    private static readonly DbObjectRef Table = new("MESAPUSER", "ATH_TEMP", "TABLE");

    private static RollbackBind B(string name, BindDataType type, string? value) => new(name, type, value);

    [Fact]
    public void Strings_are_quoted_with_doubled_quotes_and_numbers_stay_plain()
    {
        var sql = "UPDATE T SET A = :v1, B = :v2 WHERE ID = :id";
        var got = BindLiteralSubstituter.Substitute(sql,
            [B("v1", BindDataType.Varchar2, "O'Neil"), B("v2", BindDataType.Number, "12.5"), B(":id", BindDataType.Number, "7")]);
        Assert.Equal("UPDATE T SET A = 'O''Neil', B = 12.5 WHERE ID = 7", got);
    }

    [Fact]
    public void Null_and_dates_become_sql_literals()
    {
        var got = BindLiteralSubstituter.Substitute("SET A = :a, D = :d, T = :t, Z = :z",
        [
            B("a", BindDataType.Varchar2, null),
            B("d", BindDataType.Date, new DateTime(2026, 11, 12, 17, 16, 35).ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
            B("t", BindDataType.Timestamp, new DateTime(2026, 11, 12, 17, 16, 35, 120).ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
            B("z", BindDataType.TimestampTz, new DateTimeOffset(2026, 11, 12, 17, 16, 35, TimeSpan.FromHours(9)).ToString("o", System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        Assert.Contains("A = NULL", got, StringComparison.Ordinal);
        Assert.Contains("D = TO_DATE('2026-11-12 17:16:35', 'YYYY-MM-DD HH24:MI:SS')", got, StringComparison.Ordinal);
        Assert.Contains("T = TO_TIMESTAMP('2026-11-12 17:16:35.120000', 'YYYY-MM-DD HH24:MI:SS.FF6')", got, StringComparison.Ordinal);
        Assert.Contains("Z = TO_TIMESTAMP_TZ('2026-11-12 17:16:35.000000 +09:00', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')", got, StringComparison.Ordinal);
    }

    [Fact]
    public void Bind_like_text_inside_literals_and_comments_is_untouched_and_unknown_values_keep_their_name()
    {
        var sql = "UPDATE T SET A = ':v1', B = :v1 /* :v1 */ WHERE C = :pwd AND D = :other -- :v1";
        var got = BindLiteralSubstituter.Substitute(sql, [B("v1", BindDataType.Varchar2, "x"), B("pwd", BindDataType.Varchar2, null)]);
        Assert.Equal("UPDATE T SET A = ':v1', B = 'x' /* :v1 */ WHERE C = :pwd AND D = :other -- :v1", got);
    }

    [Fact]
    public void Negative_numbers_are_wrapped_so_minus_minus_never_becomes_a_comment()
    {
        var got = BindLiteralSubstituter.Substitute("SET Q = Q-:d", [B("d", BindDataType.Number, "-3")]);
        Assert.Equal("SET Q = Q-(-3)", got);
    }

    [Fact]
    public void Long_values_are_cut_with_their_length()
    {
        var got = BindLiteralSubstituter.Substitute("SET A = :a", [B("a", BindDataType.Clob, new string('가', 300))]);
        Assert.Equal("SET A = '" + new string('가', 200) + "…(300자)'", got);
    }

    private static async Task<(ChangeJournalViewModel Vm, RollbackGroupItem Group)> GroupAsync(RollbackScriptState state, bool withDocument, NotCreatedReason reason = NotCreatedReason.None)
    {
        var info = new RollbackStatementInfo(1, T0, DmlVerb.Update, Table, "UPDATE MESAPUSER.ATH_TEMP SET USER_NAME = :v1 WHERE ROWID = :rid", 1,
            new ChangePoint(100, "SCN", T0, "test"), state, null, 1, false);
        var group = new RollbackGroup
        {
            ProfileId = Guid.NewGuid(),
            ProfileName = "prod-example",
            DbKey = "prod-example",
            Environment = ConnectionEnvironment.Production,
            StartedAt = T0,
            EndedAt = T0.AddMinutes(1),
            EndKind = TransactionEndKind.Commit,
            State = state,
            Reason = reason,
            StepCount = 1,
            RowsTotal = 1,
            HasScriptFile = withDocument,
            Tables = [Table],
            Statements = [info],
        };
        var doc = withDocument
            ? new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0,
            [
                new RollbackScriptStatement(1, T0, "UPDATE MESAPUSER.ATH_TEMP SET USER_NAME = :v1 WHERE ROWID = :rid", [B("v1", BindDataType.Varchar2, "홍길동"), B("rid", BindDataType.Varchar2, "AAAB")],
                    [new RollbackStep("UPDATE MESAPUSER.ATH_TEMP SET USER_NAME = 'OLD' WHERE ROWID = CHARTOROWID('AAAB')")], [], false, null, 1),
            ])
            : null;
        var store = new InMemoryRollbackStore();
        await store.SaveAsync(group, doc, default);
        var vm = ChangeJournalViewModel.ForTests(null, store);
        await vm.ReloadAsync();
        return (vm, vm.Rows.OfType<RollbackGroupItem>().Single());
    }

    [Fact]
    public async Task Group_detail_shows_values_and_right_pane_previews_the_stored_script()
    {
        var (vm, group) = await GroupAsync(RollbackScriptState.Confirmed, withDocument: true);
        await vm.SetRowSelectionAsync([group]);

        Assert.Contains("SET USER_NAME = '홍길동' WHERE ROWID = 'AAAB'", vm.DetailText, StringComparison.Ordinal);
        Assert.DoesNotContain(":v1", vm.DetailText, StringComparison.Ordinal);
        Assert.StartsWith($"원복 스크립트: #{group.Id} · 확정", vm.DiffTitle, StringComparison.Ordinal);
        Assert.Contains(vm.Diff, l => l.Text.Contains("SET USER_NAME = 'OLD'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Right_pane_says_why_when_there_is_no_script()
    {
        var (vm, group) = await GroupAsync(RollbackScriptState.NotCreated, withDocument: false, NotCreatedReason.NotProduction);
        await vm.SetRowSelectionAsync([group]);

        Assert.Empty(vm.Diff);
        Assert.StartsWith("원복 스크립트 없음: 만들지 않음", vm.DiffTitle, StringComparison.Ordinal);
        Assert.Contains(":v1", vm.DetailText, StringComparison.Ordinal); // 문서가 없으면 가린 요약 그대로
    }

    [Fact]
    public void Values_go_in_before_the_text_is_folded_so_a_line_comment_does_not_hide_later_binds()
    {
        const string Sql = "UPDATE MESAPUSER.ATH_TEMP SET USER_NAME = :v1 -- 메모" + "\n" + "WHERE ID = :id";
        var group = new RollbackGroup
        {
            ProfileId = Guid.NewGuid(), ProfileName = "dev-example", DbKey = "dev-example", StartedAt = T0, EndedAt = T0, EndKind = TransactionEndKind.Commit,
            State = RollbackScriptState.Confirmed, StepCount = 1, RowsTotal = 1, HasScriptFile = true, Tables = [Table],
            Statements = [new RollbackStatementInfo(1, T0, DmlVerb.Update, Table, Sql, 1, null, RollbackScriptState.Confirmed, null, 1, false)],
        };
        var doc = new RollbackScriptDocument(RollbackScriptDocument.CurrentSchemaVersion, 0,
            [new RollbackScriptStatement(1, T0, Sql, [B("v1", BindDataType.Varchar2, "가\n나"), B("id", BindDataType.Number, "7")], [], [], false, null, 1)]);

        var detail = ChangeJournalViewModel.GroupDetail(group, doc);
        Assert.Single(detail.Split('\n')); // 문장 하나 = 한 줄(값 안 줄바꿈도 접힌다)
        Assert.Contains("WHERE ID = 7", detail, StringComparison.Ordinal);
        Assert.Contains("USER_NAME = '가 나'", detail, StringComparison.Ordinal);
    }
}
