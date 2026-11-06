using Oranda.Core.Transfer.Copy;
using Oranda.Platform.Storage;
using Oranda.Platform.Storage.Copy;

namespace Oranda.Platform.Tests.Storage.Copy;

// 복사 실행 기록(JSON Lines): 끝나지 않은 실행 복구, 실패 행에는 키 열만.
public sealed class CopyRunLogTests : IDisposable
{
    private const string JobId = "0123456789abcdef0123456789abcdef";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-copyruns-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private AppDataPaths Paths => new(_root);

    private CopyRunLog NewLog() => new(Paths);

    private static CopyRunHeader Header(string jobId = JobId) => new(
        jobId,
        "월간 복사",
        "src.example",
        "dev.example",
        new ResolvedPeriod(new DateTime(2026, 10, 1), new DateTime(2026, 11, 1)),
        new DateTimeOffset(2026, 11, 6, 9, 30, 15, TimeSpan.Zero));

    private static readonly DisabledObject Trigger = new(DisabledKind.Trigger, "SCOTT_DEV", "ORDERS", "TRG_ORDERS");

    private static readonly DisabledObject ForeignKey = new(DisabledKind.ForeignKey, "SCOTT_DEV", "ORDER_ITEMS", "FK_ITEMS_ORDERS");

    [Fact]
    public async Task Unfinished_run_lists_disabled_objects()
    {
        var log = NewLog();
        await using (var record = await log.BeginAsync(Header(), CancellationToken.None))
        {
            await record.RecordDisabledAsync([Trigger, ForeignKey], CancellationToken.None);
        }

        // 앱이 죽은 뒤 다시 열었을 때: 새 인스턴스가 파일만 보고 찾는다.
        var unfinished = Assert.Single(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
        Assert.Equal([Trigger, ForeignKey], unfinished.Disabled);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 9, 30, 15, TimeSpan.Zero), unfinished.StartedAt);

        await NewLog().MarkRecoveredAsync(unfinished.RunFile, CancellationToken.None);
        Assert.Empty(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Disabled_objects_are_on_disk_before_the_record_is_closed()
    {
        var log = NewLog();
        await using var record = await log.BeginAsync(Header(), CancellationToken.None);
        await record.RecordDisabledAsync([Trigger], CancellationToken.None);

        // 기록을 닫지 않았어도 다른 핸들로 읽으면 줄이 보인다(줄마다 flush).
        var file = Assert.Single(Directory.GetFiles(Paths.CopyRunsDirectory, "*.jsonl"));
        await using var reader = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var text = new StreamReader(reader);
        var content = await text.ReadToEndAsync();
        Assert.Contains("TRG_ORDERS", content, StringComparison.Ordinal);
        Assert.Equal(2, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Running_record_is_not_reported_as_unfinished()
    {
        var log = NewLog();
        await using var record = await log.BeginAsync(Header(), CancellationToken.None);
        await record.RecordDisabledAsync([Trigger], CancellationToken.None);

        Assert.Empty(await log.FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Completed_run_is_not_unfinished()
    {
        var log = NewLog();
        await using (var record = await log.BeginAsync(Header(), CancellationToken.None))
        {
            await record.RecordDisabledAsync([Trigger], CancellationToken.None);
            await record.RecordStatementAsync("ALTER TRIGGER \"SCOTT_DEV\".\"TRG_ORDERS\" DISABLE", CancellationToken.None);
            await record.RecordTableAsync(new TableRunResult("ORDERS", 10, 10, TimeSpan.FromSeconds(1), CopyOutcome.Succeeded, null), CancellationToken.None);
            await record.CompleteAsync(CopyOutcome.Succeeded, CancellationToken.None);
        }

        Assert.Empty(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Unfinished_lookup_only_returns_runs_of_that_job()
    {
        var log = NewLog();
        await using (var other = await log.BeginAsync(Header("ffffffffffffffffffffffffffffffff"), CancellationToken.None))
        {
            await other.RecordDisabledAsync([Trigger], CancellationToken.None);
        }

        Assert.Empty(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Run_started_in_same_second_gets_its_own_file()
    {
        var log = NewLog();
        await using var first = await log.BeginAsync(Header(), CancellationToken.None);
        await using var second = await log.BeginAsync(Header(), CancellationToken.None);

        var files = Directory.GetFiles(Paths.CopyRunsDirectory, "*.jsonl");
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.EndsWith("-" + JobId + ".jsonl", f, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncated_last_line_does_not_hide_disabled_objects()
    {
        var log = NewLog();
        await using (var record = await log.BeginAsync(Header(), CancellationToken.None))
        {
            await record.RecordDisabledAsync([ForeignKey], CancellationToken.None);
        }

        var file = Assert.Single(Directory.GetFiles(Paths.CopyRunsDirectory, "*.jsonl"));
        await File.AppendAllTextAsync(file, "{\"type\":\"table\",\"tab");

        var unfinished = Assert.Single(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
        Assert.Equal([ForeignKey], unfinished.Disabled);
    }

    [Fact]
    public async Task Mark_recovered_after_truncated_line_still_closes_the_run()
    {
        var log = NewLog();
        await using (var record = await log.BeginAsync(Header(), CancellationToken.None))
        {
            await record.RecordDisabledAsync([Trigger], CancellationToken.None);
        }

        var file = Assert.Single(Directory.GetFiles(Paths.CopyRunsDirectory, "*.jsonl"));
        await File.AppendAllTextAsync(file, "{\"type\":\"table\",\"tab");

        await NewLog().MarkRecoveredAsync(file, CancellationToken.None);

        Assert.Empty(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task File_without_header_is_not_reported()
    {
        Directory.CreateDirectory(Paths.CopyRunsDirectory);
        await File.WriteAllTextAsync(Path.Combine(Paths.CopyRunsDirectory, "20261106-093015-" + JobId + ".jsonl"), string.Empty);

        Assert.Empty(await NewLog().FindUnfinishedAsync(JobId, CancellationToken.None));
    }

    [Fact]
    public async Task Failed_row_record_keeps_only_key_and_error()
    {
        var log = NewLog();
        await using (var record = await log.BeginAsync(Header(), CancellationToken.None))
        {
            await record.RecordFailedRowAsync(
                new FailedRow("ORDERS", new Dictionary<string, string?> { ["ORDER_ID"] = "1001", ["LINE_NO"] = null }, "ORA-00001: unique constraint violated"),
                CancellationToken.None);
        }

        var file = Assert.Single(Directory.GetFiles(Paths.CopyRunsDirectory, "*.jsonl"));
        var lines = await File.ReadAllLinesAsync(file);
        var failed = Assert.Single(lines, l => l.Contains("\"failedRow\"", StringComparison.Ordinal));
        using var doc = System.Text.Json.JsonDocument.Parse(failed);
        var root = doc.RootElement;
        Assert.Equal(["at", "error", "key", "table", "type"], root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("1001", root.GetProperty("key").GetProperty("ORDER_ID").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, root.GetProperty("key").GetProperty("LINE_NO").ValueKind);
        Assert.Equal(2, root.GetProperty("key").EnumerateObject().Count());
        Assert.Equal("ORA-00001: unique constraint violated", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Record_after_dispose_throws()
    {
        var record = await NewLog().BeginAsync(Header(), CancellationToken.None);
        await record.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => record.RecordStatementAsync("SELECT 1 FROM DUAL", CancellationToken.None));
    }
}
