using System.Globalization;
using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.PointCompare;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Features.Rollback;
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

namespace Oranda.App.Features.Flashback;

/// <summary>
/// TSK-13-05 플래시백 조회·복구(commands.md 「결과」). 결과 그리드 문맥 메뉴 「플래시백」 묶음.
/// 읽기 메뉴(N분 전 값 보기·이 행 변경 이력)는 단일 표 SELECT 결과(키 PK, 일반 실행 결과도 포함)에서 동작하고,
/// 편집 모드(F9) 결과에서는 편집 세션의 키(PK·ROWID)를 쓴다. 되돌리기(쓰기)는 편집 모드 결과에서만 한다.
/// 쓸 수 없으면 이유를 결과 요약 줄과 메시지 창에 보인다.
/// 서버에 보조 객체를 설치하지 않는다. 되돌리기는 미리보기를 거쳐 커밋 없이 반영한다(커밋은 사용자가 한다).
/// </summary>
[OrandaCommand("oranda.grid.flashback.asOf", CommandCategory.Result, Title = "N분 전 값 보기…", When = "gridFocus", Enablement = "gridHasSelection && !noImplMetadata",
    Placements = ["context:grid/edit/flashback@20"])]
[OrandaCommand("oranda.grid.flashback.history", CommandCategory.Result, Title = "이 행 변경 이력", When = "gridFocus", Enablement = "gridHasSelection && !noImplMetadata",
    Placements = ["context:grid/edit/flashback@30"])]
[OrandaCommand("oranda.grid.flashback.restore", CommandCategory.Result, Title = "이전 값으로 되돌리기…", When = "gridFocus", Enablement = "gridHasSelection && !sessionBusy && !noImplMetadata",
    Placements = ["context:grid/edit/flashback@130"])]
public sealed class FlashbackCommandHandler : ICommandHandler
{
    private const int DefaultAsOfMinutes = 10;
    private const int MaxRestoreRows = 200;

    /// <summary>
    /// 세션 서버의 DBMS 카탈로그(<c>dbinfo.flashback_ddl</c>·<c>dbinfo.flashback_env</c> 를 꺼낸다). 그 DBMS 용 구현이 없으면 이유를 담아 던진다 —
    /// 실행 입구(<see cref="ExecuteAsync"/>)가 먼저 확인해 안내하므로 여기까지 오는 일은 드물다.
    /// </summary>
    private static ISqlCatalog CatalogFor(IServiceProvider services, ISession session)
        => DbmsServices.For<ISqlCatalog>(services, session) ?? throw new InvalidOperationException(CatalogRouting.Unavailable(session));

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

            if (DbmsServices.For<ISqlCatalog>(container, source.Session) is null)
            {
                Notify(container, model, CatalogRouting.Unavailable(source.Session)); // 이 DBMS 에는 플래시백 문장 카탈로그가 없다 — 기능을 끄고 이유를 보인다
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
                    await AsOfAsync(container, runner, model, source, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.history":
                    await HistoryAsync(container, model, source, cancellationToken).ConfigureAwait(true);
                    break;
                case "oranda.grid.flashback.restore" when edit is null:
                    Notify(container, model, FlashbackTarget.RestoreNeedsEditMode);
                    break;
                case "oranda.grid.flashback.restore":
                    await RestoreAsync(container, model, source.Target, edit, cancellationToken).ConfigureAwait(true);
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
    private static async Task AsOfAsync(IServiceProvider services, FlashbackRunner runner, ResultGridModel model, FlashbackSource source, CancellationToken ct)
    {
        var (tabs, tab, session, target) = (source.Tabs, source.Tab, source.Session, source.Target);
        var minutes = await AskMinutesAsync(services, runner, session, "N분 전 값 보기", DefaultAsOfMinutes, ct).ConfigureAwait(true);
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
            var ddl = await runner.LastDdlAsync(session, CatalogFor(services, session), target.Table, ct).ConfigureAwait(true);
            throw ddl is { } t
                ? new InvalidOperationException($"{e.Message} 테이블 정의가 마지막으로 바뀐 시각: {t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}. 그 이후 시각만 조회할 수 있습니다.", e)
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
        var lastDdl = days is null ? null : await Runner(container).LastDdlAsync(source.Session, CatalogFor(container, source.Session), target.Table, ct).ConfigureAwait(true);
        // 최근 N일이 undo 보존 기간보다 길면 하한이 undo 밖이라 ORA-30052 가 난다: 보존 기간을 읽을 수 있으면 그 안으로 줄인다(못 읽으면 설정 그대로).
        var undo = days is null ? null : await UndoRetentionAsync(container, source.Session, ct).ConfigureAwait(true);
        var host = SqlEditorHost.Current;
        var profileId = host.Active?.ConnectionProfileId ?? source.Session.Profile.Id;
        var text = FlashbackTrackSql.BuildFor(container, source.Session, target.Table, target.Names, target.KeyNames, target.KeyOf(row),
            source.Session.Server.SessionUser ?? source.Session.Profile.User, days, lastDdl, undo);
        if (text is null)
        {
            model.ReportNotice(CatalogRouting.Unavailable(source.Session)); // 이 DBMS·버전에 변경 이력 질의 문장이 없다
            return;
        }

        await OpenAndRunAsync(host, text, profileId, token => ExecutionCommands.Resolve(container).RunAsync(RunMode.Statement, ct: token), ct).ConfigureAwait(true);
        model.ReportNotice(string.Empty);
    }

    /// <summary>undo_retention(초). 이 DBMS·버전 카탈로그가 없거나 읽을 권한이 없거나 읽기에 실패하면 null.</summary>
    private static async Task<object?> UndoRetentionAsync(ServiceContainer container, ISession session, CancellationToken ct)
    {
        try
        {
            var runner = Runner(container);
            return await FlashbackEnvironment.ReadUndoRetentionAsync(CatalogFor(container, session), session.Server, async text =>
            {
                var stmt = new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT" };
                var rows = await FlashbackRunner.ReadAllAsync(await runner.QueryAsync(session, new FlashbackQuery(stmt, new Dictionary<string, BindValue>()), ct)
                    .ConfigureAwait(true), ct).ConfigureAwait(true);
                return rows.Count > 0 ? rows[0][0] : null;
            }).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // 권한 없음(ORA-00942) 등: 설정 기간 그대로
        }
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

    /// <summary>「이전 값으로 되돌리기」(G2): 고른 행(최대 200)의 키로 화면 3 을 연다. 반영은 커밋 없이 하고, 끝나면 결과를 다시 조회한다.</summary>
    private static async Task RestoreAsync(ServiceContainer container, ResultGridModel model, FlashbackTarget target, GridEditSession edit,
                                    CancellationToken ct)
    {
        if (target.EditablePositions.Count == 0)
        {
            model.ReportNotice("되돌릴 수 있는 열이 없습니다.");
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

        // G2(설계 8.5): 분 입력 대신 화면 3 을 고른 행의 키 범위로 연다. 시점 목록 맨 위 「10분 전」, 그 아래 이 표의 커밋 묶음, 맨 아래 직접 지정.
        var args = new PointCompareArgs(edit.Session.Profile.Id, null, target.Table, target.KeyNames,
                                        [.. now.Select(x => target.KeyOf(x.Values))], DefaultAsOfMinutes);
        CompareApplyResult? done = null;
        var note = cut.Length > 0 ? cut.Trim() : null;
        var why = await PointCompareLauncher.OpenAsync(container, args, (applied, point) =>
        {
            done = applied;
            var when = point.Kind == PointOptionKind.MinutesAgo ? point.Label : point.Kind == PointOptionKind.Group ? "커밋 묶음 실행 직전" : "지정한 시점";
            var failures = applied.Failed + applied.Conflicts;
            var skipped = applied.KeyScope && applied.MissingThen + applied.Unchanged > 0 ? $" (건너뜀: 그때 없던 행 {applied.MissingThen}, 같은 값 {applied.Unchanged})" : string.Empty;
            var zero = applied.ZeroRows > 0 ? $" 0행 {applied.ZeroRows}개는 그 뒤 다른 사람이 바꾼 행입니다." : string.Empty;
            var notice = failures == 0
                ? $"{applied.Applied} 행을 {when} 값으로 되돌렸습니다(커밋 안 함). 커밋하려면 커밋 단추, 되돌리려면 롤백.{skipped}{(applied.KeyScope ? cut : string.Empty)}{zero}"
                : $"{applied.Applied} 행 반영, {failures} 행 실패: {applied.FirstError}{(applied.KeyScope ? cut : string.Empty)}";
            GridEditCommands.Report?.Invoke(notice);
            model.ReportNotice(notice);
            return Task.CompletedTask;
        }, ct, edit.Session, note).ConfigureAwait(true);

        // 결과 다시 조회는 창을 닫은 뒤(모달 뒤에서 돌지 않게). 일부만 반영돼도 바뀐 행이 보이게 다시 읽는다.
        if (done is { Applied: > 0 } && (edit.Rerun ?? GridEditCommands.Rerun) is { } rerun)
        {
            await rerun(edit.Query, ct).ConfigureAwait(true);
        }

        if (why is not null)
        {
            Notify(container, model, why);
        }
    }

    /// <summary>분 입력 대화상자. 제목에 undo_retention 안내를 붙인다. 취소하거나 잘못 입력하면 null.</summary>
    private static async Task<int?> AskMinutesAsync(IServiceProvider services, FlashbackRunner runner, ISession session, string title, int initial, CancellationToken ct)
    {
        var env = await runner.EnvironmentAsync(session, CatalogFor(services, session), ct).ConfigureAwait(true);
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
