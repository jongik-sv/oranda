using System.Globalization;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.Core.Catalog;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Shell;
using Oranda.Language.Editing;
using Oranda.Oracle.Catalog;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// TSK-13-05 플래시백 조회·복구(commands.md 「결과」). 결과 그리드 문맥 메뉴 「플래시백」 묶음.
/// 대상은 편집 모드(F9)로 연 단일 표 결과(키 PK·ROWID)다. 아닌 결과에서는 누르면 이유를 결과 요약 줄에 보이고 아무것도 하지 않는다.
/// 서버에 보조 객체를 설치하지 않는다. 되돌리기는 미리보기를 거쳐 커밋 없이 반영한다(커밋은 사용자가 한다).
/// </summary>
[OrandaCommand("oranda.grid.flashback.asOf", CommandCategory.Result, Title = "N분 전 값 보기…", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/flashback@10"])]
[OrandaCommand("oranda.grid.flashback.history", CommandCategory.Result, Title = "이 행 변경 이력", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/flashback@20"])]
[OrandaCommand("oranda.grid.flashback.track", CommandCategory.Result, Title = "변경 추적하기", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/flashback@25"])]
[OrandaCommand("oranda.grid.flashback.restore", CommandCategory.Result, Title = "이전 값으로 되돌리기…", When = "gridFocus", Enablement = "gridHasSelection && !sessionBusy",
    Placements = ["context:grid/flashback@30"])]
public sealed class FlashbackCommandHandler : ICommandHandler
{
    private const int DefaultAsOfMinutes = 10;
    private const int DefaultHistoryMinutes = 15;
    private const int MaxRestoreRows = 200;

    private static readonly Lazy<SqlCatalog> Catalog = new(EmbeddedCatalog.Load);

    private readonly IServiceProvider? _services;

    public FlashbackCommandHandler()
    {
    }

    public FlashbackCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is not ServiceContainer container || (context.Parameter as ResultGridModel ?? ResultGridCommands.Active) is not { HasSelection: true } model)
        {
            return;
        }

        try
        {
            if (!Locate(model, out var tabs, out var tab, out var target, out var edit, out var why))
            {
                model.ReportNotice(why);
                return;
            }

            if (edit.IsDirty)
            {
                model.ReportNotice("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 플래시백을 쓰세요.");
                return;
            }

            var runner = Runner(container);
            switch (context.CommandId)
            {
                case "oranda.grid.flashback.asOf":
                    await AsOfAsync(runner, model, tabs, tab, target, edit, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.history":
                    await HistoryAsync(runner, model, tabs, tab, target, edit, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.track":
                    await TrackAsync(container, model, target, edit, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.restore":
                    await RestoreAsync(container, runner, model, target, edit, cancellationToken).ConfigureAwait(true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            model.ReportNotice("플래시백을 취소했습니다.");
        }
        catch (Exception e)
        {
            model.ReportNotice("플래시백을 하지 못했습니다: " + e.Message.ReplaceLineEndings(" "));
        }
    }

    /// <summary>그리드가 속한 결과 탭·편집 세션·플래시백 대상을 찾는다. 쓸 수 없으면 false 와 이유.</summary>
    internal static bool Locate(ResultGridModel model, out ResultTabsViewModel tabs, out ResultTabItem tab, out FlashbackTarget target,
                                out GridEditSession edit, out string reason)
    {
        tabs = null!;
        tab = null!;
        target = null!;
        edit = null!;
        if (SqlEditorHost.Current.Active?.ResultPane is not ResultTabsViewModel t
            || t.Tabs.FirstOrDefault(x => ReferenceEquals(x.Pane?.Model, model)) is not { } found)
        {
            reason = "플래시백은 편집기 결과 탭에서 쓸 수 있습니다.";
            return false;
        }

        tabs = t;
        tab = found;
        if (found.EditSession is not { } es)
        {
            reason = FlashbackTarget.NeedsEditMode;
            return false;
        }

        edit = es;
        var created = FlashbackTarget.TryCreate(es.Capability, es.Plan.ColumnNames, es.Columns, out reason);
        if (created is null)
        {
            return false;
        }

        target = created;
        return true;
    }

    /// <summary>「N분 전 값 보기」: 불러온 행의 키로 AS OF 질의를 실행해 새 결과 탭에 열고 지금 값과 다른 칸을 강조한다.</summary>
    private static async Task AsOfAsync(FlashbackRunner runner, ResultGridModel model, ResultTabsViewModel tabs, ResultTabItem tab,
                                 FlashbackTarget target, GridEditSession edit, CancellationToken ct)
    {
        var minutes = await AskMinutesAsync(runner, edit, "N분 전 값 보기", DefaultAsOfMinutes, ct).ConfigureAwait(true);
        if (minutes is not { } n)
        {
            return;
        }

        var count = (int)Math.Min(model.BaseRowCount, FlashbackQueryBuilder.MaxKeyRows);
        var cut = model.BaseRowCount > count ? $" 불러온 행이 {FlashbackQueryBuilder.MaxKeyRows}개를 넘어 앞쪽만 읽었습니다." : string.Empty;
        var rows = (await model.ReadRowsAsync(0, count - 1, target.Ordinals, ct).ConfigureAwait(true))
            .Where(r => target.KeyPositions.All(p => r[p] is not (null or DBNull)))
            .Select(r => r.ToArray())
            .ToList();
        if (rows.Count == 0)
        {
            model.ReportNotice("키 값이 있는 행을 아직 불러오지 못했습니다.");
            return;
        }

        var query = FlashbackQueryBuilder.BuildAsOf(target.Table, target.Names, target.KeyNames, [.. rows.Select(r => (IReadOnlyList<object?>)target.KeyOf(r))], n);
        model.ReportNotice($"{n}분 전 값을 읽는 중…");
        IResultSet rs;
        try
        {
            rs = await runner.QueryAsync(edit.Session, query, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException e) when (FlashbackErrorAdvisor.IsDefinitionChanged(e.Message))
        {
            var ddl = await runner.LastDdlAsync(edit.Session, Catalog.Value, target.Table, ct).ConfigureAwait(true);
            throw ddl is { } t
                ? new InvalidOperationException($"{e.Message} 표 정의가 마지막으로 바뀐 시각: {t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}. 그 이후 시각만 조회할 수 있습니다.", e)
                : e;
        }

        var result = new RelatedRowsResult(rs, $"{n}분 전: {target.Table.Name}", query.Statement, query.Binds);
        var opened = tabs.AddRelated(result, tab.Pane);
        if (opened.Pane?.Model is { } shown)
        {
            shown.EditOverlay = new FlashbackOverlay(shown, target.KeyPositions, rows);
            shown.NotifyEditChanged();
            shown.ReportNotice($"{n}분 전 값입니다. 지금 값과 다른 칸은 강조됩니다. 그때 없던 행(그 뒤 INSERT)은 이 탭에 나오지 않습니다." + cut);
        }

        model.ReportNotice(string.Empty);
    }

    /// <summary>「이 행 변경 이력」: VERSIONS BETWEEN 으로 활성 행의 변경 이력을 새 결과 탭에 연다.</summary>
    private static async Task HistoryAsync(FlashbackRunner runner, ResultGridModel model, ResultTabsViewModel tabs, ResultTabItem tab,
                                    FlashbackTarget target, GridEditSession edit, CancellationToken ct)
    {
        var minutes = await AskMinutesAsync(runner, edit, "이 행 변경 이력", DefaultHistoryMinutes, ct).ConfigureAwait(true);
        if (minutes is not { } n)
        {
            return;
        }

        var row = (await model.ReadRowsAsync(model.Active.Row, model.Active.Row, target.Ordinals, ct).ConfigureAwait(true)).FirstOrDefault();
        if (row is null || target.KeyPositions.Any(p => row[p] is null or DBNull))
        {
            model.ReportNotice("이 행의 키 값을 읽을 수 없어 이력을 볼 수 없습니다.");
            return;
        }

        var query = FlashbackQueryBuilder.BuildVersions(target.Table, target.Names, target.KeyNames, target.KeyOf(row), n);
        var notice = $"최근 {n}분 동안의 변경 이력입니다(최신이 위). VERSIONS_OPERATION: I 삽입 · U 수정 · D 삭제.";
        IResultSet rs;
        try
        {
            rs = await runner.QueryAsync(edit.Session, query, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException e) when (FlashbackErrorAdvisor.IsDefinitionChanged(e.Message))
        {
            // 최근에 만들었거나 ALTER 한 표: 창 시작을 표 정의가 바뀐 시각 뒤로 당겨 한 번만 다시 한다.
            if (await runner.LastDdlAsync(edit.Session, Catalog.Value, target.Table, ct).ConfigureAwait(true) is not { } ddl)
            {
                throw;
            }

            query = FlashbackQueryBuilder.BuildVersions(target.Table, target.Names, target.KeyNames, target.KeyOf(row), n, ddl);
            rs = await runner.QueryAsync(edit.Session, query, ct).ConfigureAwait(true);
            notice = $"표 정의가 {ddl.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} 에 바뀌어 그 뒤 이력만 보입니다. VERSIONS_OPERATION: I 삽입 · U 수정 · D 삭제.";
        }

        var opened = tabs.AddRelated(new RelatedRowsResult(rs, $"이력: {target.Table.Name}", query.Statement, query.Binds), tab.Pane);
        opened.Pane?.Model?.ReportNotice(notice);
        model.ReportNotice(string.Empty);
    }

    /// <summary>「변경 추적하기」: 활성 행의 전체 변경 이력 질의를 새 SQL 탭에 넣고 바로 실행한다(원래 결과를 만든 탭의 접속으로).</summary>
    private static async Task TrackAsync(ServiceContainer container, ResultGridModel model, FlashbackTarget target, GridEditSession edit, CancellationToken ct)
    {
        var row = (await model.ReadRowsAsync(model.Active.Row, model.Active.Row, target.Ordinals, ct).ConfigureAwait(true)).FirstOrDefault();
        if (row is null || target.KeyPositions.Any(p => row[p] is null or DBNull))
        {
            model.ReportNotice("이 행의 키 값을 읽을 수 없어 변경을 추적할 수 없습니다.");
            return;
        }

        var host = SqlEditorHost.Current;
        var profileId = host.Active?.ConnectionProfileId ?? edit.Session.Profile.Id;
        var text = FlashbackTrackSql.Build(target.Table, target.Names, target.KeyNames, target.KeyOf(row),
            edit.Session.Server.SessionUser ?? edit.Session.Profile.User);
        await OpenAndRunAsync(host, text, profileId, token => ExecutionCommands.Resolve(container).RunAsync(RunMode.Statement, ct: token), ct).ConfigureAwait(true);
        model.ReportNotice(string.Empty);
    }

    /// <summary>새 SQL 탭에 <paramref name="text"/> 를 넣고(캐럿은 글 끝, 접속 지정) <paramref name="run"/> 으로 실행한다.</summary>
    internal static async Task<SqlEditorViewModel> OpenAndRunAsync(SqlEditorHost host, string text, Guid? profileId, Func<CancellationToken, Task> run, CancellationToken ct)
    {
        var doc = host.NewDocument(text);
        doc.ConnectionProfileId = profileId;
        doc.IsDirty = false;
        doc.Select(text.Length, 0);
        await run(ct).ConfigureAwait(true);
        return doc;
    }

    /// <summary>「이전 값으로 되돌리기 UPDATE」: 고른 행의 N분 전 값으로 UPDATE 를 만들어 미리 보여 주고, 확인하면 커밋 없이 반영한다.</summary>
    private static async Task RestoreAsync(ServiceContainer container, FlashbackRunner runner, ResultGridModel model, FlashbackTarget target, GridEditSession edit,
                                    CancellationToken ct)
    {
        if (target.EditablePositions.Count == 0)
        {
            model.ReportNotice("되돌릴 수 있는 열이 없습니다.");
            return;
        }

        var minutes = await AskMinutesAsync(runner, edit, "이전 값으로 되돌리기", DefaultAsOfMinutes, ct).ConfigureAwait(true);
        if (minutes is not { } n)
        {
            return;
        }

        var (from, to) = model.SelectionRect();
        var last = Math.Min(to.Row, from.Row + MaxRestoreRows - 1);
        var cut = to.Row > last ? $" 고른 행이 {MaxRestoreRows}개를 넘어 앞쪽만 처리했습니다." : string.Empty;
        var selected = await model.ReadRowsAsync(from.Row, last, target.Ordinals, ct).ConfigureAwait(true);
        var now = selected.Select((r, i) => (Row: from.Row + i, Values: r.ToArray()))
            .Where(x => target.KeyPositions.All(p => x.Values[p] is not (null or DBNull)))
            .ToList();
        if (now.Count == 0)
        {
            model.ReportNotice("키 값이 있는 행을 고르세요.");
            return;
        }

        var query = FlashbackQueryBuilder.BuildAsOf(target.Table, target.Names, target.KeyNames, [.. now.Select(x => (IReadOnlyList<object?>)target.KeyOf(x.Values))], n);
        var past = await FlashbackRunner.ReadAllAsync(await runner.QueryAsync(edit.Session, query, ct).ConfigureAwait(true), ct).ConfigureAwait(true);
        var plan = FlashbackPlanner.PlanRestore(target, now, past);
        if (plan.Changes.Count == 0)
        {
            model.ReportNotice(plan.MissingThen > 0
                ? $"되돌릴 행이 없습니다: 고른 행 {plan.MissingThen}개는 {n}분 전에 없었습니다."
                : $"{n}분 전과 값이 같아 되돌릴 칸이 없습니다.");
            return;
        }

        var dml = plan.Changes.Select(RowDmlBuilder.Build).Where(d => d is not null).Select(d => d!).ToList();
        if (GridEditCommands.ConfirmPreview is not { } confirm || !await confirm(RowDmlBuilder.FormatPreview(dml), false, ct).ConfigureAwait(true))
        {
            model.ReportNotice("되돌리기를 취소했습니다.");
            return;
        }

        if (container.GetService<IRowEditor>() is not { } editor)
        {
            model.ReportNotice("행 편집기가 없어 되돌릴 수 없습니다.");
            return;
        }

        var applied = await editor.ApplyAsync(edit.Session, plan.Changes, false, ct).ConfigureAwait(true);
        var skipped = plan.MissingThen + plan.Unchanged > 0 ? $" (건너뜀: 그때 없던 행 {plan.MissingThen}, 같은 값 {plan.Unchanged})" : string.Empty;
        var notice = applied.Failures.Count == 0
            ? $"{applied.AppliedCount} 행을 {n}분 전 값으로 되돌렸습니다(커밋 안 함). 커밋하려면 커밋 단추, 되돌리려면 롤백.{skipped}{cut}"
            : $"{applied.AppliedCount} 행 반영, {applied.Failures.Count} 행 실패: {applied.Failures[0].Error?.Message}";
        GridEditCommands.Report?.Invoke(notice);
        model.ReportNotice(notice);
        if (applied.Failures.Count == 0 && applied.AppliedCount > 0 && (edit.Rerun ?? GridEditCommands.Rerun) is { } rerun)
        {
            await rerun(edit.Query, ct).ConfigureAwait(true);
        }
    }

    /// <summary>분 입력 대화상자. 제목에 undo_retention 안내를 붙인다. 취소하거나 잘못 입력하면 null.</summary>
    private static async Task<int?> AskMinutesAsync(FlashbackRunner runner, GridEditSession edit, string title, int initial, CancellationToken ct)
    {
        var env = await runner.EnvironmentAsync(edit.Session, Catalog.Value, ct).ConfigureAwait(true);
        var text = await new AvaloniaEditorDialogs().PromptAsync($"{title} — 몇 분 전? ({env})", initial.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(true);
        if (text is null)
        {
            return null;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > FlashbackQueryBuilder.MaxMinutes)
        {
            throw new InvalidOperationException($"분은 1~{FlashbackQueryBuilder.MaxMinutes.ToString(CultureInfo.InvariantCulture)} 사이의 정수로 입력하세요.");
        }

        return n;
    }

    private static FlashbackRunner Runner(ServiceContainer container)
    {
        var state = ExecutionState.For(container.GetService<IEventBus>());
        return new FlashbackRunner(state, () =>
        {
            ExecutionCommands.EnsureEngineServices(container, state);
            return container.GetRequiredService<IExecutionService>();
        });
    }
}
