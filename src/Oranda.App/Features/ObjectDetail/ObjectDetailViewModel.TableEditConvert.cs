using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Oranda.App.Features.TableEdit;
using Oranda.Core.ChangeBundle;
using Oranda.Core.Execution;
using Oranda.Core.Schema.TableEdit;
using Oranda.Core.Schema.TableEdit.Conversion;

namespace Oranda.App.Features.ObjectDetail;

/// <summary>
/// 「변환해서 바꾸기…」(테이블 편집기 설계 18.9): 검사 ✖ 「데이터가 있어 형식을 바꿀 수 없음」 줄 옆 단추가 열 형식 자동 변환 창(<see cref="ColumnConversionWindow"/>)을 연다.
/// 창이 문장을 반영했으면(성공·중간 실패 모두) 테이블을 다시 읽어 나머지 편집을 그 위에 옮긴다(변환한 열의 형식 변경은 반영됐으므로 빠진다).
/// </summary>
public sealed partial class ObjectDetailViewModel
{
    /// <summary>시험용: 변환 검사 읽기(기본 = 세션 DBMS 의 <see cref="IColumnConversionChecks"/>).</summary>
    internal IColumnConversionChecks? ConversionChecks { get; set; }

    /// <summary>시험용: 창 띄우기(기본 = 주 창 위 모달). 창이 닫힐 때 끝나는 작업.</summary>
    internal Func<ColumnConversionViewModel, Task>? ShowConversion { get; set; }

    /// <summary>이 줄에서 「변환해서 바꾸기…」를 쓸 수 있나: 원본에 있고 편집에서 형식이 바뀐 열.</summary>
    internal bool CanConvert(string rowKey) => TableEdit is { } edit && edit.Original.Column(rowKey) is not null
        && edit.Draft.Column(rowKey) is { IsDropped: false } && !IsTableEditRunning && !TableEditNeedsRefresh && _session is { Profile.ReadOnly: false };

    /// <summary>창을 연다. 열었으면 true(창이 닫힐 때까지 기다린다).</summary>
    public async Task<bool> OpenConversionAsync(string rowKey, CancellationToken ct = default)
    {
        TableEdit?.FlushPendingInput?.Invoke();
        if (TableEdit is not { } edit || !CanConvert(rowKey) || _session is not { } basis || Target is not { } target || ResolveMeta() is not { } meta)
        {
            return false;
        }

        var session = ConnectionManager.SessionRouter.ForTool(_services, basis) ?? basis;
        Oranda.Core.Metadata.TableInfo table;
        try
        {
            table = await ReadTableFreshAsync(meta, session, target, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            StatusText = TableEditText.T("테이블을 읽지 못했다: ", "Could not read the table: ") + ex.Message;
            return false;
        }

        // 계획은 편집을 켤 때의 원본으로 만든다: 그새 서버가 바뀌었으면(다른 세션의 제약 추가 등) 3단계 CASCADE 가 모르는 것을 지울 수 있어 열지 않는다.
        if (!edit.Engine.Diff(edit.Original, edit.Engine.FromTable(table, edit.Dialect), edit.Dialect).IsEmpty)
        {
            TableEditNeedsRefresh = true;
            StatusText = TableEditText.T("편집을 켠 뒤 서버의 테이블 구조가 바뀌었다. 새로 고침 한 뒤 다시 하세요", "The table changed on the server since editing started. Refresh and try again");
            return false;
        }

        var confirm = Confirm ?? ((message, token) => SqlEditor.SqlEditorHost.Current.Dialogs.ConfirmAsync(message, token));
        var services = new ColumnConversionServices
        {
            Exec = Exec ?? _services?.GetService(typeof(IExecutionService)) as IExecutionService,
            Policy = TableEditPolicy ?? _services?.GetService(typeof(IExecutionPolicy)) as IExecutionPolicy,
            Interaction = TableEditInteraction ?? _services?.GetService(typeof(IExecutionInteraction)) as IExecutionInteraction,
            Checks = TableEditChecks ?? DbmsServices.For<ITableEditChecks>(_services, session),
            Conversions = ConversionChecks ?? DbmsServices.For<IColumnConversionChecks>(_services, session),
            Bundles = ChangeBundles ?? _services?.GetService(typeof(IChangeBundleService)) as IChangeBundleService,
            OpenEditor = DdlEditorOpener ?? OpenInNewEditor,
            PickFile = PickScriptFile ?? ((suggested, token) => SqlEditor.SqlEditorHost.Current.Dialogs.PickSaveFileAsync(suggested, token)),
            Confirm = confirm,
            Today = Today,
            CheckTimeout = TableEditCheckTimeout,
        };

        ColumnConversionViewModel vm;
        try
        {
            vm = new ColumnConversionViewModel(session, edit, rowKey, table, services);
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
            return false;
        }

        await (ShowConversion ?? ShowConversionWindowAsync)(vm);
        if (vm.Applied)
        {
            await AfterConversionAsync(basis, target, edit, vm);
        }

        return true;
    }

    private static async Task ShowConversionWindowAsync(ColumnConversionViewModel vm)
    {
        var window = new ColumnConversionWindow { DataContext = vm };
        if ((Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow is { } owner)
        {
            await window.ShowDialog(owner).ConfigureAwait(true);
        }
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task.ConfigureAwait(true);
        }
    }

    /// <summary>
    /// 변환이 끝난 뒤 남은 편집: 바꾼 열(최종 이름)과 그 열을 쓰는 원래 제약·인덱스는 변환이 다시 만든 서버 상태 그대로 두고(남은 편집 없음),
    /// 나머지 편집만 Core <see cref="TableDraftRebase"/> 로 새 원본 위에 옮긴다. 새 원본에만 있는 줄(다시 만든 서버 이름 제약 등)은 바뀌지 않은 줄로 둔다 — 지울 줄로 보이지 않게.
    /// </summary>
    internal static TableDraft ConversionRemaining(TableEditSession edit, ColumnConversionViewModel conversion, TableDraft newOriginal)
    {
        var key = conversion.RowKey;
        var finalName = conversion.Target.Name;
        bool Touches(ConstraintDraft c) => c.ColumnKeys.Contains(key);
        bool TouchesIndex(IndexDraft i) => i.Columns.Any(c => c.ColumnKey == key);
        var old = edit.Original;
        var oldAdjusted = old with
        {
            Columns = [.. old.Columns.Select(c => c.RowKey == key ? c with { Name = finalName } : c)],
            Constraints = [.. old.Constraints.Where(c => !Touches(c))],
            Indexes = [.. old.Indexes.Where(i => !TouchesIndex(i))],
        };
        var draftAdjusted = edit.Draft with
        {
            Columns = [.. edit.Draft.Columns.Select(c => c.RowKey == key ? oldAdjusted.Column(key)! : c)],
            Constraints = [.. edit.Draft.Constraints.Where(c => !(old.Constraint(c.RowKey) is { } o && Touches(o)))],
            Indexes = [.. edit.Draft.Indexes.Where(i => !(old.Index(i.RowKey) is { } o && TouchesIndex(o)))],
        };
        var rebased = TableDraftRebase.Rebase(oldAdjusted, draftAdjusted, TableEditScript.Empty, 0, newOriginal);
        var converted = newOriginal.Columns.FirstOrDefault(c => c.Name == finalName);
        return rebased with
        {
            // 바꾼 열 = 서버가 지금 가진 정의(남은 편집 없음). 이름으로 맞춘 줄을 새 원본의 그 줄로 바꾼다.
            Columns = [.. rebased.Columns.Select(c => converted is not null && c.RowKey == converted.RowKey ? converted : c),
                       .. newOriginal.Columns.Where(c => rebased.Column(c.RowKey) is null)],
            Constraints = [.. rebased.Constraints, .. newOriginal.Constraints.Where(c => rebased.Constraint(c.RowKey) is null)],
            Indexes = [.. rebased.Indexes, .. newOriginal.Indexes.Where(i => rebased.Index(i.RowKey) is null)],
        };
    }

    /// <summary>변환이 서버를 바꿨다: 캐시를 무효화하고 다시 읽어, 반영되지 않은 나머지 편집만 남긴다(이름으로 맞춤 — Core <see cref="TableDraftRebase"/>).</summary>
    private async Task AfterConversionAsync(Oranda.Core.Sessions.ISession session, Oranda.Core.Metadata.DbObjectRef target, TableEditSession edit, ColumnConversionViewModel conversion)
    {
        var succeeded = conversion.Succeeded;
        try
        {
            if (ResolveMeta() is not { } meta)
            {
                return;
            }

            var table = await ReadTableFreshAsync(meta, session, target, CancellationToken.None);
            var newOriginal = edit.Engine.FromTable(table, edit.Dialect);
            // 중간에 멈췄으면 서버 상태(임시 열·지운 옛 열)를 편집 모델로 옮길 수 없다 — 다시 읽은 상태에서 다시 시작한다(남은 단계는 스크립트로).
            var remaining = succeeded ? ConversionRemaining(edit, conversion, newOriginal) : newOriginal;
            edit.Reset(newOriginal, remaining);
            edit.SetPartitions(table);
            _detailStale = true;
            StatusText = succeeded
                ? TableEditText.T("형식 변환을 마쳤다 — 남은 편집만 아래에 보인다(그 열과 그 열의 제약·인덱스는 변환이 다시 만든 상태)",
                                  "The type conversion finished — only the remaining edits are shown (that column and its constraints/indexes are as the conversion rebuilt them)")
                : TableEditText.T("형식 변환이 중간에 멈춰 편집을 다시 읽은 상태로 돌렸다 — 「남은 단계 스크립트」로 이어 한 뒤 다시 고치세요",
                                  "The type conversion stopped midway; the edit was reset to the reloaded table — finish with the remaining script, then edit again");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TableEditNeedsRefresh = true;
            _detailStale = true;
            StatusText = TableEditText.T("변환 뒤 다시 읽지 못해 실행을 막았다(새로 고침 하세요): ", "Could not reload after the conversion; run is blocked until you refresh: ") + ex.Message;
        }
    }
}
