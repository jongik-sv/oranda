using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Grid;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>
/// 데이터 복사 창의 목록 표: 규칙·점검 결과·SQL 미리 보기·테이블별 진행·실패한 행을 조회 결과와 같은 그리드(<see cref="MonitorGrid"/>)로 보인다.
/// 행은 <see cref="MonitorGridBinder{T}"/> 가 채우고, 항목 값이 바뀌어도(단계·건수·규칙 종류) 다시 그린다. 마지막 열(조건·내용·문장·오류·결과)은 남은 폭을 채운다.
/// 머리글과 고정 낱말 칸은 영어 화면에서 문구 표(<see cref="LiteralTranslator"/>)로 바꾼다.
/// </summary>
public partial class DataCopyWindow
{
    private MonitorGridBinder<TableRuleViewModel>? _ruleGrid;
    private MonitorGridBinder<CheckItemViewModel>? _checkGrid;
    private MonitorGridBinder<PreviewRowViewModel>? _sqlGrid;
    private MonitorGridBinder<TableRunRowViewModel>? _runGrid;
    private MonitorGridBinder<FailedRowViewModel>? _failedGrid;

    /// <summary>영어 화면이면 문구 표의 영어로, 아니면(또는 표에 없으면) 그대로.</summary>
    private static string L(string korean) =>
        LiteralTranslator.IsEnglish && LiteralTranslator.Table.TryGetValue(korean, out var english) ? english : korean;

    private static string Text(IValueConverter converter, object? value) =>
        L(converter.Convert(value, typeof(string), null, CultureInfo.CurrentCulture) as string ?? string.Empty);

    private static string OneLine(string text) => string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    private static MonitorColumn<TableRuleViewModel>[] RuleColumns() =>
    [
        new(L("테이블"), ColumnDataKind.Text, r => r.Table),
        new(L("규칙"), ColumnDataKind.Text, r => Text(DataCopyConverters.RuleKindText, r.Kind)),
        new(L("대상 처리"), ColumnDataKind.Text, r => Text(DataCopyConverters.ActionText, r.Action)),
        new(L("조건"), ColumnDataKind.Text, r => L(r.ConditionText)),
    ];

    private static MonitorColumn<CheckItemViewModel>[] CheckColumns() =>
    [
        new(L("수준"), ColumnDataKind.Text, c => L(c.LevelText)),
        new(L("테이블"), ColumnDataKind.Text, c => c.Table),
        new(L("항목"), ColumnDataKind.Text, c => L(c.CodeText)),
        new(L("내용"), ColumnDataKind.Text, c => OneLine(c.Message)),
    ];

    private static string PolicyText(PreviewRowViewModel p) => L(p.IsDenied ? "실행할 수 없음" : p.IsConfirm ? "확인 필요" : "허용");

    private static MonitorColumn<PreviewRowViewModel>[] SqlColumns() =>
    [
        new(L("구분"), ColumnDataKind.Text, p => L(p.SideText)),
        new(L("정책"), ColumnDataKind.Text, PolicyText),
        new(L("문장"), ColumnDataKind.Text, p => OneLine(p.Sql)),
    ];

    private static string ProgressText(TableRunRowViewModel r) =>
        r.HasEstimate ? string.Create(CultureInfo.InvariantCulture, $"{r.Percent:0}%")
        : r.Rows > 0 ? string.Create(CultureInfo.InvariantCulture, $"{r.Rows:N0}") + (LiteralTranslator.IsEnglish ? " rows" : "행")
        : string.Empty;

    private static string ResultText(TableRunRowViewModel r)
    {
        var outcome = r.Outcome is { } o ? Text(DataCopyConverters.OutcomeText, o) : string.Empty;
        return string.IsNullOrEmpty(r.Message) ? outcome : string.IsNullOrEmpty(outcome) ? r.Message : $"{outcome} · {r.Message}";
    }

    /// <summary>테이블별 진행 열. 5 = 건수 비교, 6 = 결과(색 칸 번호).</summary>
    private static MonitorColumn<TableRunRowViewModel>[] RunColumns() =>
    [
        new(L("테이블"), ColumnDataKind.Text, r => r.Table),
        new(L("진행"), ColumnDataKind.Text, ProgressText),
        new(L("단계"), ColumnDataKind.Text, r => L(r.Phase)),
        new(L("원본 건수"), ColumnDataKind.Number, r => r.SourceRows),
        new(L("대상 건수"), ColumnDataKind.Number, r => r.TargetRows),
        new(L("건수 비교"), ColumnDataKind.Text, r => r.CountsMatch switch { true => "✓ " + L("같음"), false => "✗ " + L("다름"), null => string.Empty }),
        new(L("결과"), ColumnDataKind.Text, ResultText),
    ];

    private static MonitorColumn<FailedRowViewModel>[] FailedColumns() =>
    [
        new(L("테이블"), ColumnDataKind.Text, f => f.Table),
        new(L("키"), ColumnDataKind.Text, f => f.KeyText),
        new(L("오류"), ColumnDataKind.Text, f => OneLine(f.Error)),
    ];

    // 뷰모델이 정해지면 다섯 그리드를 목록에 잇는다(한 번만).
    private void HookGrids(DataCopyViewModel vm)
    {
        if (_ruleGrid is not null)
        {
            return;
        }

        var editor = vm.Editor;
        _ruleGrid = Bind(RuleGrid, RuleColumns(), r => r);
        _ruleGrid.Attach(editor.Rules, editor, nameof(RuleEditorViewModel.SelectedRule), () => editor.SelectedRule, r => editor.SelectedRule = r);
        RefreshOnChange(editor.Rules, _ruleGrid.Refresh);
        RuleGrid.ExtraMenuItems = RuleMenuItems;
        RuleGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Delete && editor.SelectedRule is { } rule)
            {
                editor.RemoveRule(rule);
                e.Handled = true;
            }
        };

        var check = vm.Precheck;
        _checkGrid = Bind(CheckGrid, CheckColumns(), c => c);
        _checkGrid.CellEmphasis = (c, col) => col == 0 && c.IsError;
        _checkGrid.CellWarning = (c, col) => col == 0 && c.IsConfirm;
        _checkGrid.Attach(check.Items, check, nameof(PrecheckViewModel.SelectedItem), () => check.SelectedItem, c => check.SelectedItem = c);

        _sqlGrid = Bind(SqlGrid, SqlColumns(), p => p);
        _sqlGrid.CellEmphasis = (p, col) => col == 1 && p.IsDenied;
        _sqlGrid.CellWarning = (p, col) => col == 1 && p.IsConfirm;
        _sqlGrid.Attach(check.Preview, check, nameof(PrecheckViewModel.SelectedPreview), () => check.SelectedPreview, p => check.SelectedPreview = p);

        var run = vm.Run;
        _runGrid = Bind(RunGrid, RunColumns(), r => r.Table);
        _runGrid.CellEmphasis = (r, col) => (col == 5 && r.CountsMatch == false) || (col == 6 && r.Outcome == CopyOutcome.Failed);
        _runGrid.CellWarning = (r, col) => col == 6 && r.Outcome is CopyOutcome.PartiallyFailed or CopyOutcome.Stopped;
        _runGrid.Attach(run.Rows);
        RefreshOnChange(run.Rows, _runGrid.Refresh);

        _failedGrid = Bind(FailedGrid, FailedColumns(), f => f);
        _failedGrid.CellEmphasis = (_, col) => col == 2;
        _failedGrid.Attach(run.FailedRows);
    }

    // 바인더를 만들고, 결과를 바꿔 끼울 때마다 모자란 열을 넓히고 마지막 열이 남은 폭을 채우게 한다(<see cref="GridColumnFit"/>).
    private static MonitorGridBinder<T> Bind<T>(MonitorGrid grid, MonitorColumn<T>[] columns, Func<T, object> key)
        where T : class
    {
        var binder = new MonitorGridBinder<T>(grid, columns, key);
        var fit = new GridColumnFit(grid);
        binder.Rebuilt += (_, _) => fit.Track(grid.Model);
        return binder;
    }

    // 항목의 값이 바뀌면(PropertyChanged) 그리드를 다시 만든다(Refresh 는 한 번에 몰아서 한다). 새로 들어온 항목에도 건다.
    private static void RefreshOnChange<T>(ObservableCollection<T> items, Action refresh) where T : INotifyPropertyChanged
    {
        void Hook(T item) => item.PropertyChanged += (_, _) => refresh();

        foreach (var item in items)
        {
            Hook(item);
        }

        items.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Move && e.NewItems is { } added)
            {
                foreach (T item in added)
                {
                    Hook(item);
                }
            }
        };
    }

    // 규칙 그리드 오른쪽 클릭 메뉴(복사·열 묶음 아래): 테이블 추가·제거·위로·아래로·열 정보 읽기.
    private List<object> RuleMenuItems()
    {
        var editor = Vm?.Editor;
        var picked = editor?.SelectedRule;
        var index = picked is null || editor is null ? -1 : editor.Rules.IndexOf(picked);
        MenuItem Item(string header, string icon, bool enabled, Action run)
        {
            var item = new MenuItem { Header = L(header), IsEnabled = enabled, Icon = Shell.Icons.ShellIcons.Create(icon, 14) };
            item.Click += (_, _) => run();
            return item;
        }

        return
        [
            Item("테이블 추가…", "rowAdd", editor is not null, () => AddTableBox.Focus()),
            Item("규칙 제거", "rowDelete", picked is not null, () => editor!.RemoveRule(picked!)),
            new Separator(),
            Item("위로", "up", index > 0 && !RuleGridSorted, () => MoveSelectedRule(-1)),
            Item("아래로", "down", index >= 0 && index < (editor?.Rules.Count ?? 0) - 1 && !RuleGridSorted, () => MoveSelectedRule(1)),
            new Separator(),
            Item("열 정보 읽기", "columns", picked is not null, () => _ = LoadSelectedColumnsAsync()),
        ];
    }

    private async Task LoadSelectedColumnsAsync()
    {
        if (Vm is { } vm && _rule is { } rule)
        {
            await RunUiAsync(ct => vm.LoadColumnsAsync(rule, ct)).ConfigureAwait(true);
        }
    }

    // 그리드를 머리글로 정렬해 두면 목록 순서를 바꿔도 보이는 순서가 그대로라 위로·아래로가 아무 일도 안 하는 것처럼 보인다.
    private bool RuleGridSorted => RuleGrid.Model is { IsSorted: true };

    private void MoveSelectedRule(int delta)
    {
        if (Vm?.Editor is not { SelectedRule: { } rule } editor)
        {
            return;
        }

        if (RuleGridSorted)
        {
            Vm.StatusText = L("규칙 그리드의 정렬을 풀면 순서를 바꿀 수 있습니다(머리글을 한 번 더 누르면 풀립니다).");
            Vm.StatusIsError = false;
            return;
        }

        var index = editor.Rules.IndexOf(rule);
        var to = index + delta;
        if (index >= 0 && to >= 0 && to < editor.Rules.Count)
        {
            editor.Rules.Move(index, to);
        }
    }
}
