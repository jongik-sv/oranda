using System.Globalization;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.App.Shell.Settings;
using Oranda.Core.Catalog;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Settings;
using Oranda.Core.Shell;
using Oranda.Language.Editing;
using Oranda.Oracle.Catalog;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// TSK-13-05 플래시백 조회·복구(commands.md 「결과」). 결과 그리드 문맥 메뉴 「플래시백」 묶음.
/// 읽기 메뉴(N분 전 값 보기·이 행 변경 이력)는 단일 표 SELECT 결과(키 PK, 일반 실행 결과도 포함)에서 동작하고,
/// 편집 모드(F9) 결과에서는 편집 세션의 키(PK·ROWID)를 쓴다. 되돌리기(쓰기)는 편집 모드 결과에서만 한다.
/// 쓸 수 없으면 이유를 결과 요약 줄과 메시지 창에 보인다.
/// 서버에 보조 객체를 설치하지 않는다. 되돌리기는 미리보기를 거쳐 커밋 없이 반영한다(커밋은 사용자가 한다).
/// </summary>
[OrandaCommand("oranda.grid.flashback.asOf", CommandCategory.Result, Title = "N분 전 값 보기…", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/edit/flashback@20"])]
[OrandaCommand("oranda.grid.flashback.history", CommandCategory.Result, Title = "이 행 변경 이력", When = "gridFocus", Enablement = "gridHasSelection",
    Placements = ["context:grid/edit/flashback@30"])]
[OrandaCommand("oranda.grid.flashback.restore", CommandCategory.Result, Title = "이전 값으로 되돌리기…", When = "gridFocus", Enablement = "gridHasSelection && !sessionBusy",
    Placements = ["context:grid/edit/flashback@130"])]
public sealed class FlashbackCommandHandler : ICommandHandler
{
    private const int DefaultAsOfMinutes = 10;
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
            var (source, why) = await LocateAsync(container, model, cancellationToken).ConfigureAwait(true);
            if (source is null)
            {
                Notify(container, model, why);
                return;
            }

            var edit = source.Edit;
            if (edit is { IsDirty: true })
            {
                model.ReportNotice("반영하지 않은 편집이 있습니다. 반영하거나 되돌린 뒤 플래시백을 쓰세요.");
                return;
            }

            var runner = Runner(container);
            switch (context.CommandId)
            {
                case "oranda.grid.flashback.asOf":
                    await AsOfAsync(runner, model, source, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.history":
                    await HistoryAsync(container, model, source, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.restore" when edit is null:
                    Notify(container, model, FlashbackTarget.RestoreNeedsEditMode);
                    break;
                case "oranda.grid.flashback.restore":
                    await RestoreAsync(container, runner, model, source.Target, edit, cancellationToken).ConfigureAwait(true);
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

    /// <summary>
    /// 그리드가 속한 결과 탭·접속·플래시백 대상을 찾는다. 편집 모드 결과는 편집 세션의 키를, 일반 실행 결과는 탭의 문장을
    /// 편집 모드와 같은 판정(<see cref="GridEditSession.PlanAsync"/>)으로 분석해 PK 를 쓴다(편집 세션·ROWID 재실행 없이).
    /// 쓸 수 없으면 null 과 이유.
    /// </summary>
    internal static async Task<(FlashbackSource? Source, string Reason)> LocateAsync(ServiceContainer container, ResultGridModel model, CancellationToken ct)
    {
        if (SqlEditorHost.Current.Active?.ResultPane is not ResultTabsViewModel tabs
            || tabs.Tabs.FirstOrDefault(x => ReferenceEquals(x.Pane?.Model, model)) is not { } tab)
        {
            return (null, "플래시백은 편집기 결과 탭에서 쓸 수 있습니다.");
        }

        if (tab.EditSession is { } es)
        {
            var created = FlashbackTarget.TryCreate(es.Capability, es.Plan.ColumnNames, es.Columns, out var reason);
            return created is null ? (null, reason) : (new FlashbackSource(tabs, tab, es.Session, created, es), string.Empty);
        }

        if (tab.Statement is not { } statement
            || ExecutionCommands.ActiveTabSession(container) is not { } session
            || container.GetService<IRowEditor>() is not { } editor
            || container.GetService<IMetadataProvider>() is not { } metadata)
        {
            return (null, FlashbackTarget.NeedsEditMode);
        }

        var (target, why) = await PlainTargetAsync(editor, metadata, session, statement, model.Columns, ct).ConfigureAwait(true);
        return target is null ? (null, why) : (new FlashbackSource(tabs, tab, session, target, null), string.Empty);
    }

    /// <summary>일반 실행 결과의 대상: 문장이 단일 표 SELECT 이고 PK 열이 결과에 모두 있어야 한다(편집 모드와 같은 판정, 키는 PK 뿐).</summary>
    internal static async Task<(FlashbackTarget? Target, string Reason)> PlainTargetAsync(IRowEditor editor, IMetadataProvider metadata, ISession session,
                                                                                         SqlStatement statement, IReadOnlyList<ResultColumn> columns, CancellationToken ct)
    {
        var plan = await GridEditSession.PlanAsync(editor, metadata, session, statement, columns, null, ct).ConfigureAwait(true);
        if (plan.IsJoin)
        {
            return (null, FlashbackTarget.MultipleTables); // 편집 모드는 조인 결과도 한 표를 골라 고치지만, 플래시백은 표 하나로 정해진 결과만 읽는다
        }

        var target = FlashbackTarget.TryCreate(plan.Capability, plan.ColumnNames, columns, out var why, plain: true);
        return (target, why);
    }

    /// <summary>쓸 수 없는 이유를 결과 요약 줄에 남기고, 눈에 띄도록 메시지 창에 경고로 올려 연다(새 대화상자 없이).</summary>
    private static void Notify(ServiceContainer container, ResultGridModel model, string text)
    {
        model.ReportNotice(text);
        ExecutionLog.For(container.GetService<IEventBus>()).AddMessage(MessageKind.Warning, text);
        container.GetService<IShellUi>()?.Docking.ShowTool("oranda.panel.messages");
    }

    /// <summary>「N분 전 값 보기」: 불러온 행의 키로 AS OF 질의를 실행해 새 결과 탭에 열고 지금 값과 다른 칸을 강조한다.</summary>
    private static async Task AsOfAsync(FlashbackRunner runner, ResultGridModel model, FlashbackSource source, CancellationToken ct)
    {
        var (tabs, tab, session, target) = (source.Tabs, source.Tab, source.Session, source.Target);
        var minutes = await AskMinutesAsync(runner, session, "N분 전 값 보기", DefaultAsOfMinutes, ct).ConfigureAwait(true);
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
            rs = await runner.QueryAsync(session, query, ct).ConfigureAwait(true);
        }
        catch (InvalidOperationException e) when (FlashbackErrorAdvisor.IsDefinitionChanged(e.Message))
        {
            var ddl = await runner.LastDdlAsync(session, Catalog.Value, target.Table, ct).ConfigureAwait(true);
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

    /// <summary>「이 행 변경 이력」: 활성 행의 전체 변경 이력 질의를 새 SQL 탭에 넣고 바로 실행한다(원래 결과를 만든 탭의 접속으로, 기간은 묻지 않는다).</summary>
    private static async Task HistoryAsync(ServiceContainer container, ResultGridModel model, FlashbackSource source, CancellationToken ct)
    {
        var target = source.Target;
        var row = (await model.ReadRowsAsync(model.Active.Row, model.Active.Row, target.Ordinals, ct).ConfigureAwait(true)).FirstOrDefault();
        if (row is null || target.KeyPositions.Any(p => row[p] is null or DBNull))
        {
            model.ReportNotice("이 행의 키 값을 읽을 수 없어 변경 이력을 볼 수 없습니다.");
            return;
        }

        // 기간은 설정 「변경 추적 기간」(TSK-13-44). 최근 N일이면 표 정의가 마지막으로 바뀐 시각 앞으로는 잡지 않는다(ORA-01466).
        var days = FlashbackTrackSql.DaysFrom(container.GetService<SettingsService>()?.Current.Grid ?? new GridSettings());
        var lastDdl = days is null ? null : await Runner(container).LastDdlAsync(source.Session, Catalog.Value, target.Table, ct).ConfigureAwait(true);
        var host = SqlEditorHost.Current;
        var profileId = host.Active?.ConnectionProfileId ?? source.Session.Profile.Id;
        var text = FlashbackTrackSql.Build(target.Table, target.Names, target.KeyNames, target.KeyOf(row),
            source.Session.Server.SessionUser ?? source.Session.Profile.User, days, lastDdl);
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

        var minutes = await AskMinutesAsync(runner, edit.Session, "이전 값으로 되돌리기", DefaultAsOfMinutes, ct).ConfigureAwait(true);
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
    private static async Task<int?> AskMinutesAsync(FlashbackRunner runner, ISession session, string title, int initial, CancellationToken ct)
    {
        var env = await runner.EnvironmentAsync(session, Catalog.Value, ct).ConfigureAwait(true);
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
