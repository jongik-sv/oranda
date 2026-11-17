using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Execution;
using Oranda.App.Features.TableEdit;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 테이블 편집 실행 연결(테이블 편집기 설계 7.1·7.6·10·11장, T7): 「실행」 → <see cref="TableEditRunner"/>(검사 → 운영 확인 한 번 → 문장별 실행) →
/// 성공이면 캐시 무효화·다시 열기·편집 끔, 중간 실패면 다시 읽은 원본 위에 남은 편집만 다시 계산(TE-5). 가상 식을 고치면 실제 데이터 5줄로 계산해 본다(7.6).
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    private CancellationTokenSource? _virtualPreviewCts;
    private bool _detailStale;

    /// <summary>마지막 실행 전 검사 줄(✖ → ⚠ → ✔). 편집 아래 줄에 보인다(개발 접속 ⚠ 는 여기에만, TE-20).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTableEditFindings), nameof(TableEditFindingsText), nameof(HasBlockingFindings), nameof(TableEditFindingRows))]
    private IReadOnlyList<CheckFinding> _tableEditFindings = [];

    /// <summary>가상 식 미리보기 한 줄(설계 7.6). 없으면 빈 글.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVirtualPreview))]
    private string _virtualPreviewText = string.Empty;

    public bool HasTableEditFindings => TableEditFindings.Count > 0;

    public bool HasBlockingFindings => TableEditFindings.Any(f => f.Severity == CheckSeverity.Block);

    public string TableEditFindingsText => CheckFindingText.FormatAll(TableEditFindings, TableEdit?.Dialect.PurgesOnDrop == true);

    public bool HasVirtualPreview => VirtualPreviewText.Length > 0;

    /// <summary>검사 줄(문구 + 「그 행 보기」 SELECT).</summary>
    public IReadOnlyList<FindingRow> TableEditFindingRows =>
        [.. TableEditFindings.Select(f => new FindingRow(CheckFindingText.Format(f, TableEdit?.Dialect.PurgesOnDrop == true), f.ViewRowsSql))];

    /// <summary>「묶음에 넣기 ▾」(설계 7.7). 반영 묶음 서비스(te-bundle <c>IChangeBundleService</c>)가 머지되면 연결한다 — 그 전에는 숨김.</summary>
    public bool CanAddToBundle => TableEdit is not null && ChangeBundleConnected;

    // te-bundle 의 IChangeBundleService 가 들어오면 서비스가 있을 때 참으로 둔다.
    internal bool ChangeBundleConnected { get; set; }

    /// <summary>「그 행 보기」: 검사가 준 SELECT 를 새 SQL 탭에 연다(이 접속, 실행하지 않음). 열었으면 true.</summary>
    public bool OpenViewRows(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return false;
        }

        return (DdlEditorOpener ?? OpenInNewEditor)(sql.TrimEnd() + Environment.NewLine, _session?.Profile.Id);
    }

    /// <summary>
    /// 편집 실행 중(검사·확인·실행·다시 읽기). 그동안 편집 표·도구줄을 막는다 — 실행 문장은 시작 때 잡은 것이라 그새 고친 칸은 실행에도 미리보기에도 맞지 않는다.
    /// <see cref="ToolPanelViewModelBase"/> 의 IsBusy(DDL 다시 읽기도 씀)와 따로 둔다.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunTableEdit), nameof(CanEditTable))]
    private bool _isTableEditRunning;

    /// <summary>실행 뒤 정리(다시 읽기)가 실패해 서버 구조를 모른다. 새로 고침 전에는 실행을 막는다(이미 반영된 문장을 다시 보내지 않게).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunTableEdit))]
    private bool _tableEditNeedsRefresh;

    /// <summary>편집 표·도구줄을 쓸 수 있다(실행 중이 아님).</summary>
    public bool CanEditTable => !IsTableEditRunning;

    /// <summary>「실행」을 누를 수 있다(문장이 있고 읽기 전용 접속이 아니고 실행 중이 아니고 다시 읽기를 기다리지 않음).</summary>
    public bool CanRunTableEdit => CanSaveTableEditScript && !IsTableEditRunning && !TableEditNeedsRefresh && _session is { Profile.ReadOnly: false };

    /// <summary>개발·테스트는 「실행」이 기본 단추, 운영은 「스크립트 저장…」을 강조한다(설계 7.7).</summary>
    public bool RunIsDefault => !IsProductionConnection;

    /// <summary>시험용: 정책(기본 = 서비스의 <see cref="IExecutionPolicy"/>).</summary>
    internal IExecutionPolicy? TableEditPolicy { get; set; }

    /// <summary>시험용: 확인 창(기본 = 서비스의 <see cref="IExecutionInteraction"/>).</summary>
    internal IExecutionInteraction? TableEditInteraction { get; set; }

    /// <summary>시험용: 검사 읽기(기본 = 세션 DBMS 의 <see cref="ITableEditChecks"/>).</summary>
    internal ITableEditChecks? TableEditChecks { get; set; }

    /// <summary>시험용: 검사 질의 시간 제한(기본 10초).</summary>
    internal TimeSpan TableEditCheckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 「실행」(설계 7.7·11장). 결과를 상태 줄·메시지 창에 알리고, 성공이면 다시 열어 편집을 끄고, 중간 실패면 남은 편집만 남긴다(TE-5).
    /// </summary>
    public async Task<TableEditRunResult?> RunTableEditAsync(CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke(); // 칸에 쓰던 글까지 넣은 문장을 실행한다(미리보기 = 실행)
        if (TableEdit is not { } edit || Target is not { } target || _session is not { } basis || !CanRunTableEdit)
        {
            return null;
        }

        if ((Exec ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService) is not { } exec)
        {
            StatusText = TableEditText.T("실행 서비스가 없다", "No execution service");
            return null;
        }

        // DDL(암묵 커밋)이라 활성 SQL 작업 공간이 같은 접속이면 그 세션으로 보낸다(다른 작업 공간의 미커밋을 확정하지 않게, 열 순서 바꾸기와 같다).
        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        var confirm = Confirm ?? ((message, token) => SqlEditor.SqlEditorHost.Current.Dialogs.ConfirmAsync(message, token));
        var runner = new TableEditRunner(exec)
        {
            Policy = TableEditPolicy ?? _services?.GetService(typeof(IExecutionPolicy)) as IExecutionPolicy,
            Interaction = TableEditInteraction ?? _services?.GetService(typeof(IExecutionInteraction)) as IExecutionInteraction,
            Checks = TableEditChecks ?? DbmsServices.For<ITableEditChecks>(_services, session),
            CheckTimeout = TableEditCheckTimeout,
            ConfirmWarnings = (findings, token) => confirm(
                TableEditText.T("실행 전 검사", "Pre-run check") + Environment.NewLine + Environment.NewLine
                + CheckFindingText.FormatAll(findings, edit.Dialect.PurgesOnDrop) + Environment.NewLine + Environment.NewLine
                + TableEditText.T("그래도 실행할까요?", "Run anyway?"), token),
            ConfirmPendingCommit = token => confirm(TableEditText.T(
                "이 세션에 커밋하지 않은 변경이 있습니다. DDL 을 실행하면 그 변경도 커밋됩니다. 계속할까요?",
                "This session has uncommitted changes. Running DDL commits them too. Continue?"), token),
        };

        IsTableEditRunning = true;
        try
        {
            var result = await runner.RunAsync(session, target, edit, ct);
            string? note = null;
            if (result.Changed)
            {
                // 정리(다시 읽기·다시 계산)는 취소와 상관없이 끝까지 한다 — 반영된 문장이 편집에 남아 다시 나가지 않게.
                note = await AfterAppliedAsync(basis, target, edit, result, CancellationToken.None);
            }

            // 검사 줄은 편집 아래에 남긴다(다시 계산·다시 열기 뒤에 둬야 지워지지 않는다). 다 끝나 편집이 꺼졌으면 비운다.
            TableEditFindings = IsTableEditing ? result.Findings : [];
            StatusText = note is null ? result.Message : result.Message + " · " + note;
            Log(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            StatusText = TableEditText.T("취소했다", "Cancelled");
            return null;
        }
        finally
        {
            IsTableEditRunning = false;
        }
    }

    /// <summary>문장이 하나라도 반영됐다: 캐시를 무효화하고, 다 됐으면 다시 열고(편집 끔), 아니면 다시 읽어 남은 편집만 남긴다.</summary>
    private async Task<string?> AfterAppliedAsync(ISession session, DbObjectRef target, TableEditSession edit, TableEditRunResult result, CancellationToken ct)
    {
        var meta = ResolveMeta();
        if (result.Outcome == TableEditRunOutcome.Succeeded)
        {
            // 다 반영됐다: 무엇이 실패하든 편집은 끈다(같은 문장이 다시 나가지 않게). 다시 열기가 안 되면 상세 표만 낡는다.
            EndTableEdit();
            try
            {
                await InvalidateAsync(session, target, ct);
                if (meta is not null)
                {
                    await OpenAsync(session, target, IsDdlSelected, ct); // 열 순서 바꾸기의 Applied → OpenAsync 와 같다(설계 7.1)
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _detailStale = true;
                return TableEditText.T("상세를 다시 읽지 못했다: ", "could not reload the detail: ") + ex.Message;
            }

            return null;
        }

        // 중간 실패(TE-5): 다시 읽은 원본 위에 아직 반영되지 않은 편집만 남긴다. 상세 표는 편집을 끌 때 다시 읽는다.
        try
        {
            if (meta is null)
            {
                throw new InvalidOperationException(TableEditText.T("메타데이터 제공자가 없다", "no metadata provider"));
            }

            var table = await ReadTableFreshAsync(meta, session, target, ct);
            var newOriginal = edit.Engine.FromTable(table, edit.Dialect);
            var remaining = TableDraftRebase.Rebase(edit.Original, edit.Draft, result.Script, result.SucceededCount, newOriginal);
            edit.Reset(newOriginal, remaining);
            _detailStale = true;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 지금 서버 구조를 모르므로 남은 문장을 계산할 수 없다: 새로 고침 전에는 실행을 막는다(반영된 문장을 다시 보내지 않게).
            TableEditNeedsRefresh = true;
            _detailStale = true;
            return TableEditText.T("다시 읽지 못해 실행을 막았다(새로 고침 하세요): ", "could not reload; run is blocked until you refresh: ") + ex.Message;
        }
    }

    private async Task InvalidateAsync(ISession session, DbObjectRef target, CancellationToken ct)
    {
        if (InvalidateTableCache is { } invalidate)
        {
            await invalidate(session, target, ct);
        }
        else if (_services?.GetService(typeof(Oranda.Core.Storage.IMetadataCache)) is Oranda.Core.Storage.IMetadataCache cache)
        {
            await cache.InvalidateAsync(session.DbKey, [target], ct);
        }
    }

    /// <summary>결과를 메시지 창에 남긴다(실패는 오류, 설계 7.1).</summary>
    private void Log(TableEditRunResult result)
    {
        if (_services?.GetService(typeof(IEventBus)) is not IEventBus bus || result.Outcome is TableEditRunOutcome.Nothing or TableEditRunOutcome.Declined)
        {
            return;
        }

        var kind = result.Outcome switch
        {
            TableEditRunOutcome.Succeeded => MessageKind.Info,
            TableEditRunOutcome.Blocked => MessageKind.Warning,
            _ => MessageKind.Error,
        };
        var title = Target is { } t ? $"{t.Owner}.{t.Name}: " : string.Empty;
        var log = ExecutionLog.For(bus);
        log.AddMessage(kind, title + result.Message);
        foreach (var f in result.Findings.Where(f => f.Severity == CheckSeverity.Warning))
        {
            log.AddMessage(MessageKind.Warning, title + CheckFindingText.Format(f, TableEdit?.Dialect.PurgesOnDrop == true)); // 개발 ⚠ 는 창이 없으니 여기에 남긴다(TE-20)
        }
    }

    /// <summary>편집을 끌 때 중간 실패로 상세 표가 낡았으면 다시 연다.</summary>
    private async Task ReopenIfStaleAsync(CancellationToken ct)
    {
        if (_detailStale && _session is { } session && Target is { } target)
        {
            _detailStale = false;
            await OpenAsync(session, target, IsDdlSelected, ct);
        }
    }

    // ---- 가상 식 미리보기(설계 7.6) -------------------------------------------------------

    /// <summary>
    /// 가상 식을 고친 뒤(칸을 벗어남): 기존 테이블은 <c>SELECT 식, 쓰인 열 … WHERE ROWNUM &lt;= 5</c> 로 계산해 한 줄로 보이고, 새 테이블은 문법만 확인한다.
    /// 식이 아직 없는 새 열을 쓰면 「실행 뒤에 확인」. 계산이 실패하면 그 오류를 보인다(실행 전 검사에서 ✖).
    /// </summary>
    public async Task PreviewVirtualExpressionAsync(string rowKey, CancellationToken ct = default)
    {
        _virtualPreviewCts?.Cancel();
        _virtualPreviewCts = null; // 앞 계산의 취소가 새 글을 덮지 않게
        if (TableEdit is not { } edit || edit.Draft.Column(rowKey) is not { IsVirtual: true } column || _session is not { } session || Target is not { } target)
        {
            VirtualPreviewText = string.Empty;
            return;
        }

        var expression = column.VirtualExpression!;
        var label = TableEditCellText.Show(column.Name) + " = " + expression;
        var referenced = ReferencedColumns(edit.Draft, expression, rowKey);
        if (referenced.Any(c => edit.IsNewRow(c.RowKey)) && !edit.IsNewTable)
        {
            VirtualPreviewText = label + "  │  " + TableEditText.T("아직 없는 새 열을 써서 실행 뒤에 확인합니다", "uses a new column — checked after running");
            return;
        }

        if ((TableEditChecks ?? DbmsServices.For<ITableEditChecks>(_services, session)) is not { } checks)
        {
            VirtualPreviewText = label + "  │  " + TableEditText.T("미리보기 구현이 없다", "no preview for this DBMS");
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _virtualPreviewCts = cts;
        cts.CancelAfter(TableEditCheckTimeout);
        VirtualPreviewText = label + "  │  " + TableEditText.T("계산 중…", "evaluating…");
        try
        {
            string text;
            if (edit.IsNewTable)
            {
                var error = await checks.ValidateExpressionAsync(session, edit.Draft.Owner, [.. edit.Draft.Columns.Where(c => !c.IsDropped && c.RowKey != rowKey)], expression, cts.Token);
                text = error is null ? "✔ " + TableEditText.T("식 문법 확인됨", "expression is valid") : "✖ " + error;
            }
            else
            {
                var preview = await checks.PreviewExpressionAsync(session, target, expression, [.. referenced.Select(c => c.Name)], 5, cts.Token);
                text = preview.Error is { } e ? "✖ " + e : FormatPreview(preview);
            }

            if (ReferenceEquals(_virtualPreviewCts, cts))
            {
                VirtualPreviewText = label + "  │  " + text;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (ReferenceEquals(_virtualPreviewCts, cts))
            {
                VirtualPreviewText = label + "  │  ⚠ " + TableEditText.T("확인 못 함(시간 초과)", "not checked (timed out)");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            if (ReferenceEquals(_virtualPreviewCts, cts))
            {
                VirtualPreviewText = label + "  │  ✖ " + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_virtualPreviewCts, cts))
            {
                _virtualPreviewCts = null;
            }
        }
    }

    /// <summary>미리보기 줄: <c>QTY=2 UNIT_PRICE=1500 → 3000 │ …</c>(첫 칸 = 식 결과, 나머지 = 쓰인 열).</summary>
    internal static string FormatPreview(ExpressionPreview preview)
    {
        if (preview.Rows.Count == 0)
        {
            return TableEditText.T("행이 없어 계산할 값이 없습니다", "no rows to evaluate");
        }

        return string.Join("  │  ", preview.Rows.Select(row =>
        {
            var inputs = string.Join(" ", preview.Columns.Skip(1).Select((c, i) => $"{c}={(i + 1 < row.Count ? row[i + 1] ?? "NULL" : "?")}"));
            var value = row.Count > 0 ? row[0] ?? "NULL" : "?";
            return (inputs.Length > 0 ? inputs + " → " : "→ ") + value;
        }));
    }

    /// <summary>식에 쓰인 이 테이블 열(따옴표 없는 이름은 대소문자 무시, 따옴표 이름은 그대로). 자기 자신·지울 열은 뺀다.</summary>
    internal static IReadOnlyList<ColumnDraft> ReferencedColumns(TableDraft draft, string expression, string selfKey)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(expression, "\"([^\"]+)\"|([A-Za-z_][A-Za-z0-9_$#]*)"))
        {
            words.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.ToUpperInvariant());
        }

        return [.. draft.Columns.Where(c => !c.IsDropped && c.RowKey != selfKey && words.Contains(c.Name))];
    }

    partial void OnTableEditStateChanged()
    {
        OnPropertyChanged(nameof(CanRunTableEdit));
        OnPropertyChanged(nameof(RunIsDefault));
        TableEditFindings = []; // 편집이 바뀌면 지난 검사 줄은 맞지 않는다
        if (TableEdit is null)
        {
            _virtualPreviewCts?.Cancel();
            _virtualPreviewCts = null;
            VirtualPreviewText = string.Empty;
        }
    }

    private void OnVirtualExpressionEdited(object? sender, string rowKey) => _ = PreviewVirtualExpressionAsync(rowKey);
}
