using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Oranda.App.Features.Ai.External.Editor;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Composition;
using Oranda.Core.Execution;
using Oranda.Core.Transfer;
using Oranda.Language.Errors;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>
/// <c>export_result</c>(계획 agent-cli-phase2 §3.1, 결정 E-26): 탭 결과 하나의 질의를 서버에서 다시 읽어 전체 행을 새 xlsx·csv 파일에 앱이 직접 쓴다. 행 값은 LLM 에 가지 않는다.
/// <list type="bullet">
/// <item>관문: 허가 안 접속·<see cref="AgentGrantPolicy.MayExecute"/> → 결과 → <see cref="ExportSource"/>(<see cref="DataTransferCommands.ForResult"/>) → 문장 가드 다시.
/// 내보내기의 다시 실행은 실행 엔진을 편집기 출처·이력 없음으로 지난다(§6 #1) — 그래서 가드가 그 앞에서 반드시 본다.</item>
/// <item>파일: 절대 경로, 부모 폴더 있음, 확장자 = 형식, 이미 있으면 <c>file_exists</c>(덮지 않음). 같은 폴더의 임시 이름에 쓰고 끝나면 바꿔 넣는다. 실패·취소면 임시 파일을 지운다.</item>
/// <item>행 한도·시간 상한 없음. 띠 「파일로 저장 중 … N행 [취소]」 와 CLI 끊김(ct)으로 멈춘다. 끝나면 「저장함 · 경로 · N행 [폴더 열기] [닫기]」, 메시지 창 「[에이전트] …」.</item>
/// </list>
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class ExportResultHandler(IServiceProvider services) : IAppToolHandler
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    public string ToolName => AppToolNames.ExportResult;

    /// <summary>부품(시험이 바꾼다). 기본은 셸 컨테이너에서 처음 쓸 때.</summary>
    internal ExternalRunParts? Parts { get; set; }

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = invocation.Arguments;
        var tabId = AppToolArgs.RequiredText(args, AppFields.Tab);
        var format = AppToolArgs.RequiredText(args, AppFields.Format).Trim().ToLowerInvariant();
        if (format is not (AppToolValues.ExportFormats.Xlsx or AppToolValues.ExportFormats.Csv))
        {
            throw new AppToolArgumentException(AppFields.Format, "must be xlsx or csv");
        }

        var output = AppToolArgs.RequiredText(args, AppFields.Out);
        var index = AppToolArgs.Number(args, AppFields.Index, -1, 0);
        var sheet = AppToolArgs.Text(args, AppFields.Sheet);
        if (CheckPath(output, format) is { } bad)
        {
            return bad;
        }

        if ((Parts ??= ExternalRunParts.From(_services)) is not { Transfer: not null } parts)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, ExternalRunText.NoTransfer);
        }

        var request = new Request(invocation, tabId, format, Path.GetFullPath(output), index, sheet);
        return await ExternalRunSupport.OnUiAsync(() => ExportAsync(parts, request, ct)).ConfigureAwait(false);
    });

    private sealed record Request(AppToolInvocation Invocation, string TabId, string Format, string Out, int Index, string? Sheet);

    /// <summary>경로 규칙(§3.1 「파일」). 맞으면 null.</summary>
    internal static AgentToolOutput? CheckPath(string path, string format)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.PathInvalid, ExternalRunText.RelativePath);
        }

        if (!string.Equals(Path.GetExtension(path), "." + format, StringComparison.OrdinalIgnoreCase))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.PathInvalid, ExternalRunText.WrongExtension(format));
        }

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is not { Length: > 0 } folder || !Directory.Exists(folder))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.PathInvalid, ExternalRunText.NoFolder);
        }

        return File.Exists(path) || Directory.Exists(path) ? AppToolOutputs.Deny(AgentErrorCodes.FileExists, ExternalRunText.Exists) : null;
    }

    private static async Task<AgentToolOutput> ExportAsync(ExternalRunParts parts, Request request, CancellationToken ct)
    {
        var target = ExternalRunSupport.Resolve(parts.Host, request.Invocation.Grant, request.TabId);
        if (target.Denial is { } denial)
        {
            return denial;
        }

        var tab = target.Tab!;
        var controller = parts.Controller;
        if (parts.MissingConnection(tab) is not null || controller.SessionFor(tab) is not { } session)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.Connection, ExternalEditorText.NotConnected);
        }

        if (controller.State.IsBusy(session.Id))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.TabBusy, ExternalRunText.Busy);
        }

        if (DataTransferCommands.ForResult(tab, request.Index, session) is not { } source)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NoResult, ExternalRunText.NoResultGrid);
        }

        if (source.Query.Kind != StatementKind.Query)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.SqlNotSingleQuery, ExternalRunText.ResultNotSelect);
        }

        if (source.BindProblem is not null)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.InvalidArguments, ExternalRunText.MissingBinds);
        }

        // 서버에 보낼 글 그대로(그리드 열 순서로 감싼 글이면 그것) 다시 본다: 사람이 실행한 결과일 수도 있다.
        var query = source.QueryInColumnOrder();
        var check = await parts.Guard.CheckAsync(session, query.Text, ExternalRunSupport.GuardOptions, ct).ConfigureAwait(true);
        if (!check.Allowed)
        {
            return ExternalRunSupport.Refusal(check);
        }

        return await WriteAsync(parts, request, tab, session, query, source, ct).ConfigureAwait(true);
    }

    private static async Task<AgentToolOutput> WriteAsync(
        ExternalRunParts parts, Request request, SqlEditorViewModel tab, Oranda.Core.Sessions.ISession session, SqlStatement query, ExportSource source, CancellationToken ct)
    {
        var client = ExternalRunSupport.ClientName(request.Invocation.Client);
        using var human = new CancellationTokenSource();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, human.Token);
        long written = 0;
        var saving = new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.Saving(client, Interlocked.Read(ref written)))
        {
            DiscardText = () => ExternalRunText.Cancel,
            Discard = () => human.Cancel(),
        };
        var bar = ExternalRunSupport.ShowStatus(tab, saving);
        var progress = new Progress<long>(rows =>
        {
            Interlocked.Exchange(ref written, rows);
            if (ReferenceEquals(bar.Current, saving))
            {
                bar.Refresh();
            }
        });
        var options = new ExportOptions(request.Format == AppToolValues.ExportFormats.Xlsx ? TransferFormat.Xlsx : TransferFormat.Csv)
        {
            SheetName = string.IsNullOrWhiteSpace(request.Sheet) ? null : request.Sheet.Trim(),
        };
        var folder = Path.GetDirectoryName(request.Out)!;
        var temp = Path.Combine(folder, "." + Path.GetFileName(request.Out) + "." + Guid.NewGuid().ToString("N")[..8] + ".part");
        var clock = Stopwatch.StartNew();
        var transfer = parts.Transfer!;
        TransferSummary? summary = null;
        Exception? failure = null;
        var exists = false;
        // 내보내는 동안 그 세션을 「실행 중」으로 잡는다: 사람의 F5·다른 run_tab 이 같은 연결에 끼어들지 않고(tab_busy), 실행 취소 명령도 이 일을 끊는다.
        var state = parts.Controller.State;
        state.BeginRun(session.Id, parts.Host.WorkspaceOf(tab));
        using var cancelHook = state.RegisterCancel(session.Id, human.Cancel);
        try
        {
            summary = await Task.Run(
                async () =>
                {
                    await using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    return await transfer.ExportAsync(session, query, source.Binds, options, stream, progress, stop.Token).ConfigureAwait(false);
                },
                CancellationToken.None).ConfigureAwait(true);
            try
            {
                File.Move(temp, request.Out, overwrite: false); // 그사이 같은 이름이 생겼으면 덮지 않는다
            }
            catch (IOException) when (File.Exists(request.Out))
            {
                exists = true;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException || stop.IsCancellationRequested)
        {
            // 취소: 아래에서 임시 파일을 지운다.
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            state.EndRun(session.Id);
            bar.Remove(saving);
            TryDelete(temp);
        }

        ct.ThrowIfCancellationRequested();
        var rows = summary?.Rows ?? Interlocked.Read(ref written);
        var log = parts.Controller.Log;
        if (exists)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.FileExists, ExternalRunText.Exists);
        }

        if (failure is not null)
        {
            ExternalRunSupport.ShowStatus(tab, new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.SaveFailed(client)));
            log.AddMessage(MessageKind.Warning, ExternalRunText.LogSaved(client, request.Out, rows, "failed"));
            return AppToolOutputs.Deny(AgentErrorCodes.ToolFailed, ExternalRunText.ExportFailed(DbErrorClassification.CodeOf(failure))) with
            {
                LogDetail = failure.GetType().Name + ": " + failure.Message,
            };
        }

        var done = summary is not null;
        var status = done ? AppToolValues.ExportStatus.Written : AppToolValues.ExportStatus.Canceled;
        var json = new JsonObject
        {
            ["status"] = status,
            ["path"] = done ? request.Out : null,
            ["rows"] = rows,
            ["bytes"] = done ? new FileInfo(request.Out).Length : 0,
            ["elapsed_ms"] = clock.ElapsedMilliseconds,
        };
        if (done)
        {
            ExternalRunSupport.ShowStatus(tab, new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.Saved(request.Out, rows))
            {
                AcceptText = () => ExternalRunText.OpenFolder,
                Accept = async () =>
                {
                    await parts.OpenFolder(folder).ConfigureAwait(true);
                    return null;
                },
                DiscardText = () => ExternalRunText.Close,
            });
        }
        else
        {
            ExternalRunSupport.ShowStatus(tab, new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.SaveCanceled(client)));
        }

        log.AddMessage(MessageKind.Info, ExternalRunText.LogSaved(client, request.Out, rows, status));
        return AppToolOutputs.OkWithRows(json, rows);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
