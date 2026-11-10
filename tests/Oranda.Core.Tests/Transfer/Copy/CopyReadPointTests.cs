using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Xunit;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 같은 시점 읽기 + 동시 복사(PF2b)의 시점 지정 읽기 실패 안내(<see cref="CopyReadPointErrors.Describe"/>): 시점 데이터 잃음(전체 중단)·권한 없음·정의 바뀜·잘못된 시점,
/// 그리고 시점 읽기와 상관없는 오류는 예전 처리(null). 어느 안내든 「지금 데이터로 바꿔 읽지 않습니다」가 들어간다.
/// </summary>
public sealed class CopyReadPointTests
{
    private static readonly CopyReadPointSupport Support = new("SCN", "AS OF SCN :{0}", [], "FLASHBACK", "FLASHBACK ANY TABLE")
    {
        SnapshotTooOldErrors = "ORA-01555·ORA-08180",
        DefinitionChangedNumbers = [1466],
        InvalidPointNumbers = [8181],
    };

    private static readonly CopyReadPoint Point = new(98765, Support);

    [Theory]
    [InlineData(1555)]
    [InlineData(8180)]
    public void Snapshot_too_old_stops_the_run_and_asks_for_more_undo(int number)
    {
        var failure = CopyReadPointErrors.Describe("SRC.ORDERS", Point, Error(number, DbErrorKind.SnapshotTooOld));

        Assert.NotNull(failure);
        Assert.True(failure!.Value.AbortRun);
        Assert.StartsWith("SRC.ORDERS: 원본 SCN 98765 시점의 데이터가 원본 UNDO 에 남아 있지 않아 읽지 못했습니다(ORA-01555·ORA-08180).", failure.Value.Message, StringComparison.Ordinal);
        Assert.Contains("UNDO_RETENTION", failure.Value.Message, StringComparison.Ordinal);
        Assert.Contains(CopyReadPointErrors.NoFallback, failure.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_privilege_fails_only_the_table_and_names_both_privileges()
    {
        var failure = CopyReadPointErrors.Describe("APP.ITEM", Point, Error(1031, DbErrorKind.InsufficientPrivileges));

        Assert.False(failure!.Value.AbortRun);
        Assert.Equal(
            "APP.ITEM: 원본 테이블을 SCN 98765 시점으로 읽을 권한(FLASHBACK 또는 FLASHBACK ANY TABLE)이 없습니다(ORA-01031). 지금 데이터로 바꿔 읽지 않습니다. 권한을 받거나, 동시 테이블 수를 1로 하거나, 같은 시점 읽기를 끄세요.",
            failure.Value.Message);
    }

    [Fact]
    public void Definition_changed_fails_only_the_table()
    {
        var failure = CopyReadPointErrors.Describe("T", Point, Error(1466, DbErrorKind.Other));

        Assert.False(failure!.Value.AbortRun);
        Assert.Contains("원본 테이블 정의가 SCN 98765 뒤에 바뀌어(DDL) 그 시점으로 읽을 수 없습니다(ORA-01466)", failure.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_point_stops_the_run()
    {
        var failure = CopyReadPointErrors.Describe("T", Point, Error(8181, DbErrorKind.Other));

        Assert.True(failure!.Value.AbortRun);
        Assert.Contains("SCN 98765 을(를) 올바른 시점으로 받지 않았습니다(ORA-08181)", failure.Value.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(942, DbErrorKind.ObjectNotFound)]
    [InlineData(1722, DbErrorKind.Other)]
    [InlineData(60, DbErrorKind.Deadlock)]
    public void Other_errors_keep_the_old_handling(int number, DbErrorKind kind)
    {
        Assert.Null(CopyReadPointErrors.Describe("T", Point, Error(number, kind)));
    }

    // ---- 번호를 실측하지 못한 원본(PF2b2 Tibero 모양) ----

    private static readonly CopyReadPointSupport Unmeasured = new("TSN", "AS OF SCN :{0}", [], "FLASHBACK", "FLASHBACK ANY TABLE")
    {
        SnapshotTooOldMessages = ["snapshot too old"],
        UnmappedErrorsFailTable = true,
        PrivilegeUnverified = true,
    };

    private static readonly CopyReadPoint TsnPoint = new(781925950, Unmeasured);

    [Fact]
    public void Snapshot_too_old_found_by_the_message_stops_the_run_with_the_received_code()
    {
        var failure = CopyReadPointErrors.Describe("SRC.ORDERS", TsnPoint, Tibero(-12345, DbErrorKind.Other, "TBR-12345: Snapshot too old."));

        Assert.True(failure!.Value.AbortRun);
        Assert.StartsWith("SRC.ORDERS: 원본 TSN 781925950 시점의 데이터가 원본 UNDO 에 남아 있지 않아 읽지 못했습니다(TBR-12345).", failure.Value.Message, StringComparison.Ordinal);
        Assert.Contains(CopyReadPointErrors.NoFallback, failure.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_server_errors_fail_only_the_table_with_the_point_and_never_fall_back()
    {
        var failure = CopyReadPointErrors.Describe("SRC.ORDERS", TsnPoint, Tibero(-5099, DbErrorKind.Other, "TBR-5099: something about 'SECRET' value" + "\n" + "second line"));

        Assert.NotNull(failure);
        Assert.False(failure!.Value.AbortRun);
        Assert.StartsWith("SRC.ORDERS: 원본 테이블을 TSN 781925950 시점으로 읽지 못했습니다(TBR-5099: something about '…' value).", failure.Value.Message, StringComparison.Ordinal);
        Assert.Contains(CopyReadPointErrors.NoFallback, failure.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", failure.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("second line", failure.Value.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("표", failure.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_kinds_keep_their_own_handling_and_numbers_match_without_the_driver_sign()
    {
        // 권한 부족(TBR-8053 = InsufficientPrivileges)은 권한 안내, 없는 객체·교착은 예전 처리(모르는 오류 처리는 Other 만).
        var privilege = CopyReadPointErrors.Describe("T", TsnPoint, Tibero(-8053, DbErrorKind.InsufficientPrivileges, "TBR-8053: not enough privilege"));
        Assert.StartsWith("T: 원본 테이블을 TSN 781925950 시점으로 읽을 권한(FLASHBACK 또는 FLASHBACK ANY TABLE)이 없습니다(TBR-8053).", privilege!.Value.Message, StringComparison.Ordinal);
        Assert.False(privilege.Value.AbortRun);
        Assert.Null(CopyReadPointErrors.Describe("T", TsnPoint, Tibero(-8033, DbErrorKind.ObjectNotFound, "TBR-8033: no object")));
        Assert.Null(CopyReadPointErrors.Describe("T", TsnPoint, Tibero(-12032, DbErrorKind.Deadlock, "TBR-12032: deadlock")));

        // 음수 번호도 번호표와 맞춘다(Tibero 드라이버는 번호를 음수로 준다).
        var numbered = Unmeasured with { DefinitionChangedNumbers = [7777], InvalidPointNumbers = [8888] };
        var point = new CopyReadPoint(1, numbered);
        Assert.Contains("원본 테이블 정의가 TSN 1 뒤에 바뀌어(DDL)", CopyReadPointErrors.Describe("T", point, Tibero(-7777, DbErrorKind.Other, "x"))!.Value.Message, StringComparison.Ordinal);
        Assert.True(CopyReadPointErrors.Describe("T", point, Tibero(-8888, DbErrorKind.Other, "x"))!.Value.AbortRun);
    }

    [Fact]
    public void Without_the_unmapped_flag_unknown_errors_keep_the_old_handling()
    {
        Assert.Null(CopyReadPointErrors.Describe("T", Point, Error(1722, DbErrorKind.Other)));
        Assert.Null(CopyReadPointErrors.Describe("T", Point, Error(1722, DbErrorKind.Other) with { Message = "ORA-01722: invalid number" }));
    }

    [Fact]
    public void Messages_never_say_pyo()
    {
        foreach (var error in new[] { Error(1555, DbErrorKind.SnapshotTooOld), Error(1031, DbErrorKind.InsufficientPrivileges), Error(1466, DbErrorKind.Other), Error(8181, DbErrorKind.Other) })
        {
            Assert.DoesNotContain("표", CopyReadPointErrors.Describe("T", Point, error)!.Value.Message, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("표", CopyConcurrency.ReadPointNote, StringComparison.Ordinal);
    }

    private static DbError Tibero(int number, DbErrorKind kind, string message) =>
        new("TBR-" + Math.Abs(number).ToString(System.Globalization.CultureInfo.InvariantCulture), message) { Number = number, Kind = kind };

    private static DbError Error(int number, DbErrorKind kind) =>
        new("ORA-" + number.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "msg") { Number = number, Kind = kind };
}
