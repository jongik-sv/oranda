using System.Text;
using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Run;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Language.Ai.Handlers.External;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>
/// <c>export_result</c>(계획 agent-cli-phase2 §3.1·E-26): 결과 → 내보내기 대상 → 가드 다시 → 전체 행을 새 파일에(임시 이름 뒤 바꿔 넣기), 경로 규칙, 취소·실패면 임시 파일 지움, 띠.
/// </summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class ExportResultHandlerTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("oranda-export-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeTransfer : IDataTransferService
    {
        public List<(SqlStatement Query, ExportOptions Options)> Exports { get; } = [];

        /// <summary>쓰는 동안 기다릴 것(취소 시험).</summary>
        public Func<CancellationToken, Task>? During { get; set; }

        public Exception? Fail { get; set; }

        public async Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            Exports.Add((query, options));
            var bytes = Encoding.UTF8.GetBytes("N\na\nb\nc\n");
            await destination.WriteAsync(bytes, ct);
            progress?.Report(2);
            if (During is { } during)
            {
                await during(ct);
            }

            if (Fail is { } fail)
            {
                throw fail;
            }

            return new TransferSummary(1234, TimeSpan.FromMilliseconds(3));
        }

        public Task<TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static async Task<(RunTabHandlerTests.Env Env, ExportResultHandler Export, FakeTransfer Transfer)> RanAsync()
    {
        var env = new RunTabHandlerTests.Env();
        Assert.False((await env.RunAsync()).IsError);
        var transfer = new FakeTransfer();
        var parts = new ExternalRunParts { Host = env.Rig.Host, Controller = env.Rig.Controller, Guard = env.Guard, Transfer = transfer, OpenFolder = f => { env.Opened.Add(f); return Task.CompletedTask; } };
        return (env, new ExportResultHandler(new NoServices()) { Parts = parts }, transfer);
    }

    private static JsonObject Args(RunTabHandlerTests.Env env, string format, string path, int? index = null)
    {
        var args = new JsonObject { ["tab"] = env.Tab.TabId.ToString(), ["format"] = format, ["out"] = path };
        if (index is { } i)
        {
            args["index"] = i;
        }

        return args;
    }

    private string[] Leftovers() => Directory.GetFiles(_folder, "*.part");

    [Fact]
    public Task Writes_all_rows_to_a_new_file_through_the_guard_and_shows_where() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        var path = Path.Combine(_folder, "orders.csv");
        env.Guard.Checked.Clear();

        var output = await export.ExecuteAsync(env.Call(Args(env, "csv", path)), CancellationToken.None);

        Assert.False(output.IsError, output.ErrorMessage);
        var result = output.Result!;
        Assert.Equal("written", result["status"]!.GetValue<string>());
        Assert.Equal(path, result["path"]!.GetValue<string>());
        Assert.Equal(1234, result["rows"]!.GetValue<long>());
        Assert.Equal(new FileInfo(path).Length, result["bytes"]!.GetValue<long>());
        Assert.Equal(1234, output.RowCount);
        Assert.Equal("N\na\nb\nc\n", await File.ReadAllTextAsync(path));
        Assert.Empty(Leftovers());

        var (query, options) = Assert.Single(transfer.Exports);
        Assert.Contains("sample_orders", query.Text, StringComparison.Ordinal);
        Assert.Equal(TransferFormat.Csv, options.Format);
        Assert.Equal([query.Text], env.Guard.Checked); // 서버에 보낼 글 그대로 다시 본다

        var bar = env.Tab.AgentBar!;
        Assert.Equal($"저장함 · {path} · 1,234행", bar.Headline);
        Assert.Equal("폴더 열기", bar.AcceptLabel);
        Assert.Equal("닫기", bar.DiscardLabel);
        await bar.AcceptCommand.ExecuteAsync(null);
        Assert.Equal([_folder], env.Opened);
        Assert.Contains(env.Rig.Log.Messages, m => m.Text.StartsWith("[에이전트] Claude Code 가 결과를 파일로 저장했습니다", StringComparison.Ordinal));
    });

    [Fact]
    public Task Xlsx_takes_the_sheet_name() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        var args = Args(env, "xlsx", Path.Combine(_folder, "orders.XLSX"));
        args["sheet"] = "주문";

        var output = await export.ExecuteAsync(env.Call(args), CancellationToken.None);

        Assert.False(output.IsError, output.ErrorMessage);
        Assert.Equal(TransferFormat.Xlsx, transfer.Exports.Single().Options.Format);
        Assert.Equal("주문", transfer.Exports.Single().Options.SheetName);
    });

    [Fact]
    public Task Path_rules_refuse_relative_wrong_extension_missing_folder_and_existing_files() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        var existing = Path.Combine(_folder, "old.csv");
        await File.WriteAllTextAsync(existing, "keep");

        Assert.Equal("path_invalid", (await export.ExecuteAsync(env.Call(Args(env, "csv", "orders.csv")), CancellationToken.None)).ErrorCode);
        Assert.Equal("path_invalid", (await export.ExecuteAsync(env.Call(Args(env, "xlsx", Path.Combine(_folder, "orders.csv"))), CancellationToken.None)).ErrorCode);
        Assert.Equal("path_invalid", (await export.ExecuteAsync(env.Call(Args(env, "csv", Path.Combine(_folder, "nope", "orders.csv"))), CancellationToken.None)).ErrorCode);
        Assert.Equal("file_exists", (await export.ExecuteAsync(env.Call(Args(env, "csv", existing)), CancellationToken.None)).ErrorCode);
        Assert.Equal("invalid_arguments", (await export.ExecuteAsync(env.Call(Args(env, "json", Path.Combine(_folder, "o.json"))), CancellationToken.None)).ErrorCode);

        Assert.Empty(transfer.Exports);
        Assert.Equal("keep", await File.ReadAllTextAsync(existing));
    });

    [Fact]
    public Task No_result_or_guard_refusal_writes_nothing() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        var path = Path.Combine(_folder, "orders.csv");

        Assert.Equal("no_result", (await export.ExecuteAsync(env.Call(Args(env, "csv", path, index: 5)), CancellationToken.None)).ErrorCode);
        env.Guard.Refusal = ExternalSelectRefusal.Unsafe;
        Assert.Equal("sql_unsafe", (await export.ExecuteAsync(env.Call(Args(env, "csv", path)), CancellationToken.None)).ErrorCode);

        Assert.Empty(transfer.Exports);
        Assert.False(File.Exists(path));
    });

    [Fact]
    public Task Cancel_from_the_bar_leaves_no_file_and_no_temp_file() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        var writing = new TaskCompletionSource();
        transfer.During = async ct =>
        {
            writing.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        };
        var path = Path.Combine(_folder, "orders.csv");

        var run = export.ExecuteAsync(env.Call(Args(env, "csv", path)), CancellationToken.None).AsTask();
        await writing.Task;
        Assert.Equal("tab_busy", (await env.RunAsync()).ErrorCode); // 내보내는 동안 세션은 실행 중이다
        Assert.True(env.Rig.State.IsBusy(env.Rig.Session.Id));
        var bar = env.Tab.AgentBar!;
        Assert.StartsWith("Claude Code 가 파일로 저장하는 중", bar.Headline, StringComparison.Ordinal);
        Assert.Equal("취소", bar.DiscardLabel);
        bar.DiscardCommand.Execute(null);
        var output = await run;

        Assert.Equal("canceled", output.Result!["status"]!.GetValue<string>());
        Assert.False(env.Rig.State.IsBusy(env.Rig.Session.Id));
        Assert.False(File.Exists(path));
        Assert.Empty(Leftovers());
        Assert.Equal("Claude Code 의 파일 저장을 취소했습니다", bar.Headline);
    });

    [Fact]
    public Task A_failed_export_removes_the_temp_file_and_reports_tool_failed() => Ui.Run(async () =>
    {
        var (env, export, transfer) = await RanAsync();
        using var _ = env;
        transfer.Fail = new InvalidOperationException("disk full");
        var path = Path.Combine(_folder, "orders.csv");

        var output = await export.ExecuteAsync(env.Call(Args(env, "csv", path)), CancellationToken.None);

        Assert.Equal("tool_failed", output.ErrorCode);
        Assert.DoesNotContain("disk full", output.ErrorMessage, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.Empty(Leftovers());
    });
}
