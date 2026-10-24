using Oranda.Core.Tuning;

namespace Oranda.Core.Tests.Tuning;

/// <summary>TSK-06-06 대기 분해 묶기: gv$session 표본·ASH 줄 → 이벤트 비율·분류 막대.</summary>
public class WaitBreakdownTests
{
    private static WaitSample S(long sid, string? ev, string? cls = null) => new(DateTime.Now, sid, "7h35uxf5uhmm1", ev, cls);

    [Fact]
    public void Samples_not_waiting_count_as_on_cpu_and_shares_add_up()
    {
        var events = WaitBreakdown.FromSamples(
        [
            S(1, null), S(2, null), S(1, null),
            S(3, "db file scattered read", "User I/O"), S(4, "db file scattered read", "User I/O"),
            S(5, "enq: TX - row lock contention", "Application"),
        ]);

        Assert.Equal(["ON CPU", "db file scattered read", "enq: TX - row lock contention"], events.Select(e => e.Event));
        Assert.Equal("CPU", events[0].WaitClass);
        Assert.Equal(3, events[0].Samples);
        Assert.Equal(2, events[0].Sessions);
        Assert.Equal(0.5, events[0].Share, 6);
        Assert.Equal(1.0, events.Sum(e => e.Share), 6);
        Assert.Equal(WaitTone.Cpu, events[0].Tone);
        Assert.Equal(WaitTone.UserIo, events[1].Tone);
        Assert.Equal(WaitTone.Application, events[2].Tone);
    }

    [Fact]
    public void Empty_input_gives_empty_tables_without_dividing_by_zero()
    {
        Assert.Empty(WaitBreakdown.FromSamples([]));
        Assert.Empty(WaitBreakdown.FromAsh([]));
        Assert.Empty(WaitBreakdown.Classes([]));
    }

    [Fact]
    public void Uncolored_classes_fold_into_one_other_slot_at_the_end()
    {
        var events = WaitBreakdown.FromSamples(
        [
            S(1, "log file sync", "Commit"), S(1, "log file sync", "Commit"), S(1, "log file sync", "Commit"),
            S(2, "control file parallel write", "System I/O"),
            S(3, null),
            S(4, "gc cr multi block request", "Cluster"),
            S(5, "latch: shared pool", "Concurrency"),
            S(6, "odd", null),
        ]);
        var classes = WaitBreakdown.Classes(events);

        Assert.Equal(WaitTone.Other, classes[^1].Tone);
        Assert.Equal(WaitBreakdown.OtherLabel, classes[^1].Label);
        Assert.Equal(5, classes[^1].Samples); // Commit 3 + System I/O 1 + 분류 없음 1
        Assert.Equal(1.0, classes.Sum(c => c.Share), 6);
        Assert.Equal(["CPU", "Cluster", "Concurrency", "기타"], classes.Select(c => c.Label));
        Assert.Equal("Other", events.Single(e => e.Event == "odd").WaitClass);
    }

    [Fact]
    public void Ash_rows_are_reordered_and_get_cpu_class_and_no_session_count()
    {
        var events = WaitBreakdown.FromAsh(
        [
            new AshWaitRow("db file sequential read", "User I/O", 10),
            new AshWaitRow("ON CPU", null, 30),
        ]);

        Assert.Equal("ON CPU", events[0].Event);
        Assert.Equal("CPU", events[0].WaitClass);
        Assert.Null(events[0].Sessions);
        Assert.Equal(0.75, events[0].Share, 6);
    }

    [Theory]
    [InlineData("user i/o", WaitTone.UserIo)]
    [InlineData(" Application ", WaitTone.Application)]
    [InlineData("CONCURRENCY", WaitTone.Concurrency)]
    [InlineData("Idle", WaitTone.Other)]
    [InlineData(null, WaitTone.Other)]
    public void Tone_ignores_case_and_spaces(string? cls, WaitTone tone) => Assert.Equal(tone, WaitBreakdown.ToneOf(cls));
}
