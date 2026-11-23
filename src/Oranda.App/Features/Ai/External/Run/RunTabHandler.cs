using System;
using System.Diagnostics;
using System.Linq;
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
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Language.Lexing;

namespace Oranda.App.Features.Ai.External.Run;

/// <summary>
/// <c>run_tab</c>(계획 agent-cli-phase2 §3.1, 결정 E-24·E-33·E-34): 탭의 SELECT 한 문장을 사람이 F5 한 것과 같은 길로 그 탭 결과 그리드에서 실행한다.
/// <list type="bullet">
/// <item>문장은 탭 글 그대로(LLM 이 보낸 글이 아니다). <c>base_version</c> 이 지금 판과 다르면 실행하지 않고 <c>stale</c>. 실행 문장은 시작 때 사본이다.</item>
/// <item>범위: <c>document</c>(기본) = 탭이 한 문장일 때만, <c>statement</c> = 커서(선택) 문장(<see cref="ExecutionController.PrepareSingleStatementAsync"/> 규칙).</item>
/// <item>관문: 허가 안 접속·<see cref="AgentGrantPolicy.MayExecute"/> → 열린 접속(묻는 창 없음) → 세션 바쁨이면 <c>tab_busy</c>(기다리지 않음) → 문장 가드(<see cref="Language.Ai.Handlers.External.IExternalSelectGuard"/>)
/// → <see cref="ExecutionController.RunPreparedAsync"/>(출처 <see cref="HistorySource.Agent"/>: 이력·정책 규칙 0 이중 방어).</item>
/// <item><c>binds</c>(agent-cli-set-binds): 있으면 <c>stale</c> 확인 뒤 검사하고(<c>bind_invalid</c>), 문장 가드까지 지난 뒤 탭 「바인드 변수」 목록에 넣고 실행한다(<c>set_binds</c> 와 같은 길).
/// 실행 값은 언제나 그 목록에서 만든다(<see cref="ExportBinds.Resolve"/>) — 이력·감사는 사람이 목록에 친 값과 같은 모양으로 남는다.</item>
/// <item>시간 상한 30초 기본·60초 최대, 넘으면 실행을 취소하고 <c>canceled</c>. 행은 돌려주지 않는다(그리드 첫 페이지까지, 읽기는 <c>get_result</c>).</item>
/// <item>사람이 보는 표시: 그 탭 띠 「… 가 이 문장을 실행하는 중입니다 [취소]」 → 「… 실행했습니다 · N행 [닫기]」, 메시지 창 「[에이전트] …」, 결과 탭 제목 앞 <c>AI·</c>.</item>
/// </list>
/// </summary>
[OrandaService(typeof(IAppToolHandler))]
public sealed class RunTabHandler(IServiceProvider services) : IAppToolHandler
{
    private readonly IServiceProvider _services = services ?? throw new ArgumentNullException(nameof(services));

    public string ToolName => AppToolNames.RunTab;

    /// <summary>부품(시험이 바꾼다). 기본은 셸 컨테이너에서 처음 쓸 때.</summary>
    internal ExternalRunParts? Parts { get; set; }

    public ValueTask<AgentToolOutput> ExecuteAsync(AppToolInvocation invocation, CancellationToken ct) => AppToolOutputs.Guard(async () =>
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var args = invocation.Arguments;
        var tabId = AppToolArgs.RequiredText(args, AppFields.Tab);
        var baseVersion = args.ContainsKey(AppFields.BaseVersion) ? (long?)AppToolArgs.Number(args, AppFields.BaseVersion, 0, 0) : null;
        var range = AppToolArgs.Text(args, AppFields.Range) ?? AppToolValues.Scopes.Document;
        if (range is not (AppToolValues.Scopes.Document or AppToolValues.Scopes.Statement))
        {
            throw new AppToolArgumentException(AppFields.Range, "must be document or statement");
        }

        var timeout = AppToolArgs.Number(args, AppFields.TimeoutSeconds, AppToolValues.DefaultTimeoutSeconds, 1, AppToolValues.MaxTimeoutSeconds);
        if ((Parts ??= ExternalRunParts.From(_services)) is not { } parts)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.NotImplemented, "Running tabs is not available in this window.");
        }

        var binds = ExternalBindFill.Arg(args);
        var request = new Request(invocation, tabId, baseVersion, range == AppToolValues.Scopes.Statement, TimeSpan.FromSeconds(timeout), binds);
        return await ExternalRunSupport.OnUiAsync(() => RunAsync(parts, request, ct)).ConfigureAwait(false);
    });

    /// <param name="Binds">에이전트가 보낸 바인드 값(<c>binds</c>, 없으면 null). 관문을 다 지난 뒤 실행 바로 앞에서 탭 목록에 넣는다(<see cref="ExternalBindFill"/>).</param>
    private sealed record Request(AppToolInvocation Invocation, string TabId, long? BaseVersion, bool CaretStatement, TimeSpan Timeout, JsonObject? Binds);

    private static async Task<AgentToolOutput> RunAsync(ExternalRunParts parts, Request request, CancellationToken ct)
    {
        var target = ExternalRunSupport.Resolve(parts.Host, request.Invocation.Grant, request.TabId);
        var output = target.Denial ?? await RunOnAsync(parts, request, target.Tab!, ct).ConfigureAwait(true);
        return ExternalRunSupport.WithProfile(output, target.Tab);
    }

    private static async Task<AgentToolOutput> RunOnAsync(ExternalRunParts parts, Request request, SqlEditorViewModel tab, CancellationToken ct)
    {
        if (request.BaseVersion is { } expected && expected != tab.Version)
        {
            return AgentToolOutput.Ok(new JsonObject { ["status"] = AppToolValues.RunStatus.Stale, ["version"] = tab.Version });
        }

        // 바인드 값은 먼저 검사만 한다(틀리면 다른 관문 전에 bind_invalid). 목록에 넣는 일은 문장 가드까지 지난 뒤.
        var fill = request.Binds is { } given ? ExternalBindFill.Plan(tab, given) : null;
        if (fill?.Denial is { } badBinds)
        {
            return badBinds;
        }

        var controller = parts.Controller;
        if (parts.MissingConnection(tab) is not null)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.Connection, ExternalEditorText.NotConnected); // 묻는 창을 띄우지 않는다
        }

        if (Busy(controller, tab))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.TabBusy, ExternalRunText.Busy);
        }

        // 문장 고르기 = 시작 때 사본. document 는 탭 전체가 한 문장일 때만, statement 는 사람의 「문장 실행」 규칙(선택 영역·빈 줄 구분).
        SqlStatement statement;
        if (request.CaretStatement)
        {
            var (picked, _) = await controller.PrepareSingleStatementAsync(tab, ct).ConfigureAwait(true);
            if (picked is null)
            {
                return Busy(controller, tab)
                    ? AppToolOutputs.Deny(AgentErrorCodes.TabBusy, ExternalRunText.Busy)
                    : AppToolOutputs.Deny(AgentErrorCodes.NoStatement, ExternalRunText.NoCaretStatement);
            }

            statement = picked;
        }
        else
        {
            var all = StatementSplitter.Split(tab.Text, SplitOptions.Default);
            if (all.Count != 1)
            {
                return AppToolOutputs.Deny(all.Count == 0 ? AgentErrorCodes.NoStatement : AgentErrorCodes.SqlNotSingleQuery,
                    all.Count == 0 ? ExternalRunText.EmptyTab : ExternalRunText.ManyStatements(all.Count));
            }

            if (!await controller.EnsureConnectionAsync(tab, ct).ConfigureAwait(true))
            {
                return AppToolOutputs.Deny(AgentErrorCodes.Connection, ExternalEditorText.NotConnected);
            }

            statement = all[0];
        }

        if (controller.SessionFor(tab) is not { } session)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.Connection, ExternalEditorText.NotConnected);
        }

        if (Busy(controller, tab))
        {
            return AppToolOutputs.Deny(AgentErrorCodes.TabBusy, ExternalRunText.Busy);
        }

        var check = await parts.Guard.CheckAsync(session, statement.Text, ExternalRunSupport.GuardOptions, ct).ConfigureAwait(true);
        if (!check.Allowed)
        {
            return ExternalRunSupport.Refusal(check);
        }

        if (fill is not null)
        {
            // 사람이 바인드 창에 친 것과 같다: 값이 그 탭 목록에 남고(작업 공간에 저장), 실행은 그 목록 값을 쓴다.
            ExternalBindFill.Apply(tab, fill, line => controller.Log.AddMessage(MessageKind.Info, line), request.Invocation.Client);
        }

        var bound = ExportBinds.Resolve(statement, null, tab.Binds);
        if (bound.Problem is not null)
        {
            return AppToolOutputs.Deny(AgentErrorCodes.InvalidArguments, ExternalRunText.MissingBinds);
        }

        return await RunOnTabAsync(parts, request, tab, statement, bound.Binds, ct).ConfigureAwait(true);
    }

    private static bool Busy(ExecutionController controller, SqlEditorViewModel tab) =>
        controller.SessionFor(tab) is { } session && controller.State.IsBusy(session.Id);

    private static async Task<AgentToolOutput> RunOnTabAsync(
        ExternalRunParts parts, Request request, SqlEditorViewModel tab, SqlStatement statement,
        System.Collections.Generic.IReadOnlyDictionary<string, BindValue> binds, CancellationToken ct)
    {
        var controller = parts.Controller;
        var client = ExternalRunSupport.ClientName(request.Invocation.Client);
        using var human = new CancellationTokenSource();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, human.Token);
        limit.CancelAfter(request.Timeout);
        var running = new AgentBarItem(AgentBarItemKind.Notice, () => ExternalRunText.Running(client))
        {
            DiscardText = () => ExternalRunText.Cancel,
            Discard = () => human.Cancel(),
        };
        var bar = ExternalRunSupport.ShowStatus(tab, running);
        var clock = Stopwatch.StartNew();
        bool ok;
        try
        {
            ok = await controller.RunPreparedAsync(tab, statement, binds, HistorySource.Agent, limit.Token).ConfigureAwait(true);
        }
        finally
        {
            bar.Remove(running);
        }

        ct.ThrowIfCancellationRequested(); // CLI 가 끊었다: 그리드에는 사람이 보는 결과가 남는다
        var results = tab.ResultPane as ResultTabsViewModel;
        var item = results?.Tabs.LastOrDefault(t => ReferenceEquals(t.Statement, statement));
        var json = new JsonObject { ["fetched"] = 0L, ["more"] = false }; // 실패·취소도 같은 모양(행 수 0)
        long fetched = 0;
        Exception? fetchError = null;
        string status;
        if (item is null)
        {
            if (Busy(controller, tab))
            {
                return AppToolOutputs.Deny(AgentErrorCodes.TabBusy, ExternalRunText.Busy);
            }

            status = limit.IsCancellationRequested ? AppToolValues.RunStatus.Canceled : AppToolValues.RunStatus.Failed;
        }
        else
        {
            item.Title = ExternalEditorService.AiTitle(item.Title); // 결과 탭 제목 앞 AI·
            json["result_index"] = results!.Tabs.IndexOf(item);
            json["statement_kind"] = statement.Kind.ToString().ToLowerInvariant();
            if (item.Pane?.Model is { } model)
            {
                fetchError = await FirstPageAsync(model, ct).ConfigureAwait(true);
                fetched = model.RowCount;
                json["fetched"] = fetched;
                json["more"] = !model.Source.IsComplete;
            }

            status = !ok ? AppToolValues.RunStatus.Canceled
                : item.IsError || fetchError is not null ? AppToolValues.RunStatus.Failed
                : AppToolValues.RunStatus.Succeeded;
        }

        json["status"] = status;
        json["elapsed_ms"] = item?.Elapsed is { } elapsed ? (long)elapsed.TotalMilliseconds : clock.ElapsedMilliseconds;
        if (status == AppToolValues.RunStatus.Failed)
        {
            json["error"] = Error(controller.State, tab, request.Invocation.Grant, item, fetchError);
        }

        var more = json["more"] is JsonValue m && m.GetValue<bool>();
        ExternalRunSupport.ShowStatus(tab, new AgentBarItem(AgentBarItemKind.Notice, () => status switch
        {
            AppToolValues.RunStatus.Succeeded => ExternalRunText.Ran(client, fetched, more),
            AppToolValues.RunStatus.Canceled => ExternalRunText.RunCanceled(client),
            _ => ExternalRunText.RunFailed(client),
        }));
        controller.Log.AddMessage(status == AppToolValues.RunStatus.Failed ? MessageKind.Warning : MessageKind.Info, ExternalRunText.LogRan(client, tab.Title, fetched, status));
        return AppToolOutputs.OkWithRows(json, fetched);
    }

    /// <summary>
    /// 그리드가 아직 첫 쪽을 읽지 않았으면 읽는다(사람 화면은 그릴 때 읽는다 — 처리자는 그리기 전에 행 수를 보므로 0 으로 보였다, w-it-p2 실측).
    /// 페이지 캐시라 이미 읽은 쪽은 다시 묻지 않는다. 첫 쪽 읽기에서 난 DB 오류(예: 값 변환 ORA-01722)는 실패로 돌려준다.
    /// </summary>
    private static async Task<Exception?> FirstPageAsync(Oranda.App.Features.ResultGrid.ResultGridModel model, CancellationToken ct)
    {
        try
        {
            await model.LoadFirstPageAsync(ct).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return ex;
        }
    }

    /// <summary>실패한 실행의 오류(번호 + 글). 운영처럼 리터럴을 가리는 접속은 글을 <c>***</c> 로.</summary>
    private static JsonObject Error(ExecutionState state, SqlEditorViewModel tab, AgentGrantView grant, ResultTabItem? item, Exception? fetchError)
    {
        var error = state.LastErrorTab == tab.TabId ? state.LastError : null;
        var message = error?.Message ?? fetchError?.Message ?? item?.Message ?? "The statement failed.";
        var code = !string.IsNullOrEmpty(error?.Code) ? error!.Code : Language.Errors.DbErrorClassification.CodeOf(fetchError);
        return new JsonObject
        {
            ["code"] = string.IsNullOrEmpty(code) ? null : code,
            ["message"] = grant.MasksLiterals(tab.ConnectionProfileId) ? AppToolValues.Masked : message,
        };
    }
}
