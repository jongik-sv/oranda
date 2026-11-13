using System.Globalization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.Rollback;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Composition;
using Oranda.Core.Catalog;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Script;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;

namespace Oranda.App.Features.PointCompare;

/// <summary>
/// 화면 3 「이 실행 전과 비교」를 여는 명령(설계 13.4, RB-32). 변경 일지(RB-42)가 <see cref="PointCompareArgs"/> 로 부른다.
/// 메뉴 자리는 없다(부르는 쪽이 인자를 준다).
/// </summary>
[OrandaCommand(RollbackCommandIds.ComparePoint, CommandCategory.Database, Title = "이 실행 전과 비교…")]
public sealed class PointCompareCommandHandler : ICommandHandler
{
    private readonly IServiceProvider? _services;

    public PointCompareCommandHandler()
    {
    }

    public PointCompareCommandHandler(IServiceProvider services) => _services = services;

    public async Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_services is null || context.Parameter is not PointCompareArgs args)
        {
            return;
        }

        var why = await PointCompareLauncher.OpenAsync(_services, args, null, cancellationToken).ConfigureAwait(true);
        if (why is not null)
        {
            ExecutionLog.For(_services.GetService(typeof(IEventBus)) as IEventBus).AddMessage(MessageKind.Warning, why);
        }
    }
}

/// <summary>화면 3 을 조립해 연다. 시험은 <see cref="ShowOverride"/> 로 창 대신 뷰모델을 받는다.</summary>
internal static class PointCompareLauncher
{
    public const string CustomLabel = "시각 직접 지정…";
    public const int DefaultMinutes = 10;

    /// <summary>시험 이음매: 있으면 창을 띄우지 않고 이것을 부른다.</summary>
    internal static Func<PointCompareViewModel, Task>? ShowOverride { get; set; }

    /// <summary>시험 이음매: 세션 위 DB 일을 바꾼다.</summary>
    internal static Func<ISession, IRollbackDialect, IPointCompareDb>? DbOverride { get; set; }

    /// <summary>
    /// 화면 3 을 연다. 열 수 없으면 이유 한 줄. <paramref name="afterApply"/> 는 반영 뒤(그리드 다시 조회 등, RB-33).
    /// </summary>
    public static async Task<string?> OpenAsync(IServiceProvider services, PointCompareArgs args, Func<CompareApplyResult, PointOption, Task>? afterApply, CancellationToken ct,
                                               ISession? session = null, string? scopeNote = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        // 그리드는 결과를 만든 세션을 넘긴다(같은 DB 에 더 연 세션이면 커밋·롤백 단추가 그 세션을 따른다)
        session ??= SessionRouter.ForProfile(services, args.ProfileId);
        if (session is null)
        {
            return "그 접속이 열려 있지 않습니다. 접속한 뒤 다시 여세요.";
        }

        if (DbmsServices.For<IRollbackDialect>(services, session) is not { } dialect)
        {
            return "이 DBMS 에서는 시점 비교를 쓸 수 없습니다.";
        }

        var store = services.GetService(typeof(IRollbackStore)) as IRollbackStore;
        var analyzer = services.GetService(typeof(IDmlAnalyzer)) as IDmlAnalyzer ?? new Oranda.Language.Editing.DmlShapeAnalyzer();
        var points = new List<PointOption>();
        RollbackGroup? group = null;
        if (args.GroupId is { } id)
        {
            try
            {
                group = store is null ? null : await store.GetAsync(id, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return "변경 일지 묶음을 읽지 못했습니다: " + e.Message;
            }

            if (group is null)
            {
                return "변경 일지 묶음 #" + id.ToString(CultureInfo.InvariantCulture) + " 을(를) 찾지 못했습니다.";
            }

            if (group.BasePoint is null)
            {
                return "이 묶음에는 기준 시점이 없어 비교할 수 없습니다(시점 읽기 실패).";
            }

            points.Add(await GroupOptionAsync(store!, group, analyzer, dialect.PointName, current: true, ct).ConfigureAwait(true));
        }
        else
        {
            points.Add(new PointOption($"{(args.DefaultMinutesAgo ?? DefaultMinutes).ToString(CultureInfo.InvariantCulture)}분 전", PointOptionKind.MinutesAgo)
            {
                Minutes = args.DefaultMinutesAgo ?? DefaultMinutes,
            });
        }

        var table = args.Table ?? (group is { Tables.Count: > 0 } g0 ? g0.Tables[0] : null);
        if (store is not null && table is not null)
        {
            // 그 표를 건드린 커밋 묶음(최신순). 범위 근거 문장은 고를 때가 아니라 지금 함께 읽는다(묶음 수가 적다).
            IReadOnlyList<RollbackGroup> found;
            try
            {
                found = await store.SearchAsync(new RollbackQuery { DbKey = session.DbKey, Table = table, Limit = 30 }, ct).ConfigureAwait(true);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                found = []; // 다른 묶음 목록만 빠진다
            }

            foreach (var g in found.Where(g => g.Id != group?.Id && g.BasePoint is not null && g.EndKind is not TransactionEndKind.Rollback))
            {
                points.Add(await GroupOptionAsync(store, g, analyzer, dialect.PointName, current: false, ct).ConfigureAwait(true));
            }
        }

        points.Add(new PointOption(CustomLabel, PointOptionKind.Custom));

        var catalog = DbmsServices.For<ISqlCatalog>(services, session);
        var db = DbOverride?.Invoke(session, dialect) ?? CreateDb(services, session, dialect, catalog);
        var keys = args.Keys?.Select(k => (IReadOnlyList<object?>)k).ToList();
        var connection = (SessionRouter.Find(services)?.LabelOf(session)) ?? session.Profile.Name;
        var registry = services.GetService(typeof(ICommandRegistry)) as ICommandRegistry;
        var contextKeys = services.GetService(typeof(IContextKeys)) as IContextKeys;
        var ledger = services.GetService(typeof(IRollbackLedger)) as IRollbackLedger;
        var bus = services.GetService(typeof(IEventBus)) as IEventBus;
        var vm = new PointCompareViewModel(db, connection, table, points, keys)
        {
            KeyColumns = args.KeyColumns,
            ScopeNote = scopeNote,
            ConfirmApply = session.Profile.Environment == Oranda.Core.Connection.ConnectionEnvironment.Production
                && Oranda.App.Features.GridEdit.GridEditCommands.ConfirmPreview is { } confirm
                    ? (text, token) => confirm(text, false, token) // 운영 접속: 반영 전에 한 번 더 확인(문장마다 묻지 않는다)
                    : null,
            OpenEditor = text =>
            {
                var doc = SqlEditorHost.Current.NewDocument(text);
                doc.ConnectionProfileId = session.Profile.Id;
                doc.Title = "비교 되돌리기 스크립트";
            },
            OpenRollbackScript = registry is null || contextKeys is null
                ? null
                : groupId => _ = registry.ExecuteAsync(RollbackCommandIds.OpenScript, contextKeys, new RollbackScriptArgs(groupId)),
            Applied = async (result, point) =>
            {
                RecordDerived(ledger, bus, session, result, point);
                ExecutionLog.For(bus).AddMessage(result.Failed + result.Conflicts > 0 ? MessageKind.Warning : MessageKind.Info, PointCompareViewModel.Describe(result));
                if (afterApply is not null)
                {
                    await afterApply(result, point).ConfigureAwait(true);
                }
            },
        };

        if (ShowOverride is { } show)
        {
            await show(vm).ConfigureAwait(true);
            return null;
        }

        var window = new PointCompareWindow { DataContext = vm };
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var loading = vm.LoadAsync(ct);
        if (owner is not null)
        {
            await Task.WhenAll(window.ShowDialog(owner), loading).ConfigureAwait(true);
        }
        else
        {
            window.Show();
            await loading.ConfigureAwait(true);
        }

        return null;
    }

    /// <summary>커밋 묶음 한 줄: 기준·커밋 시점, 표, 원장 문서의 문장(범위 근거). 문서를 열 수 없어도(키 없음·손상) 시점 비교는 된다.</summary>
    internal static async Task<PointOption> GroupOptionAsync(IRollbackStore store, RollbackGroup g, IDmlAnalyzer analyzer, string pointName, bool current, CancellationToken ct)
    {
        RollbackScriptDocument? doc = null;
        try
        {
            doc = await store.LoadDocumentAsync(g.Id, ct).ConfigureAwait(true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            doc = null; // 14장: 묶음 정보와 시점 비교는 그대로 쓸 수 있다(WHERE 범위만 못 쓴다)
        }

        var scopes = new Dictionary<DbObjectRef, IReadOnlyList<CompareStatementScope>>();
        foreach (var t in g.Tables)
        {
            // 문장의 표는 원장 문장 정보(시노님을 푼 실제 표)로 정한다. 정보가 없는 문장은 이 표의 근거로 보고 모양을 모르면 표 전체로 물러난다.
            scopes[t] = doc is null
                ? []
                : [.. doc.Statements.Where(s => !s.Invalidated && (TableOf(g, s.Seq) is not { } st || st == t)).Select(s => PointCompareLoader.ScopeOf(s, analyzer))];
        }

        var at = g.BasePoint!.ReadAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var label = current
            ? $"기준 {pointName} {g.BasePoint.Number.ToString(CultureInfo.InvariantCulture)} ({at}, 실행 직전)"
            : $"커밋 묶음 #{g.Id.ToString(CultureInfo.InvariantCulture)} 실행 직전 · {at}";
        return new PointOption(label, PointOptionKind.Group)
        {
            BasePoint = g.BasePoint.Number,
            BaseAt = g.BasePoint.ReadAt,
            CommitPoint = g.CommitPoint?.Number,
            CommitAt = g.CommitPoint?.ReadAt,
            TransactionId = g.TransactionId,
            GroupId = g.Id,
            HasScriptFile = g.HasScriptFile,
            Tables = g.Tables,
            Scopes = scopes,
        };
    }

    private static DbObjectRef? TableOf(RollbackGroup g, int seq)
    {
        foreach (var info in g.Statements)
        {
            if (info.Seq == seq)
            {
                return info.Table;
            }
        }

        return null;
    }

    /// <summary>반영이 1행을 바꾼 문장마다 반대 문장을 묶음으로 기록한다(RD-8). 기록 실패는 반영을 되돌리지 않는다.</summary>
    private static void RecordDerived(IRollbackLedger? ledger, IEventBus? bus, ISession session, CompareApplyResult result, PointOption point)
    {
        if (ledger is null || result.Executed.Count == 0)
        {
            return;
        }

        var reverse = RollbackReverse.ReverseAll(result.Executed, out var skipped);
        if (reverse.Count == 0)
        {
            return;
        }

        try
        {
            ledger.RecordDerived(session.Id, RollbackOrigin.CompareApply, point.GroupId ?? 0, new RollbackStatementRecord
            {
                SessionId = session.Id,
                ProfileId = session.Profile.Id,
                ProfileName = session.Profile.Name,
                DbKey = session.DbKey,
                ServerName = session.Server.ServiceName,
                Environment = session.Profile.Environment,
                Dbms = session.Server.Dbms,
                Source = Oranda.Core.History.HistorySource.Editor,
                ExecutedAt = DateTimeOffset.Now,
                Text = string.Join(";" + Environment.NewLine, result.Executed.Select(s => s.Sql)),
                Verb = DmlVerb.Other,
                Table = result.Table,
                RowsAffected = result.Executed.Count,
                BeforePoint = result.BeforePoint,
                State = skipped > 0 || reverse.Any(r => r.Note is not null) ? RollbackScriptState.Incomplete : RollbackScriptState.Open,
                IncompleteReason = skipped > 0 ? $"반대 문장을 만들 수 없는 문장 {skipped.ToString(CultureInfo.InvariantCulture)}개(PK 없는 행 되살림 등)"
                    : reverse.FirstOrDefault(r => r.Note is not null)?.Note,
                Steps = reverse,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 기록 실패는 반영을 되돌리지 않는다(14장 「원장 저장 실패」와 같다)
            ExecutionLog.For(bus).AddMessage(MessageKind.Warning, "비교 반영의 반대 문장을 기록하지 못했습니다: " + e.Message);
        }
    }

    private static SessionPointCompareDb CreateDb(IServiceProvider services, ISession session, IRollbackDialect dialect, ISqlCatalog? catalog)
    {
        var container = (ServiceContainer)services;
        var state = ExecutionState.For(container.GetService<IEventBus>());
        return new SessionPointCompareDb(session, dialect, state, () =>
        {
            ExecutionCommands.EnsureEngineServices(container, state);
            return container.GetRequiredService<IExecutionService>();
        }, container.GetRequiredService<IMetadataProvider>(), catalog);
    }
}
