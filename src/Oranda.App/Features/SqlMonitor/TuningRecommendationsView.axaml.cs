using Avalonia.Controls;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Grid;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>SQL 모니터 「튜닝 추천」 탭. 목록은 조회 결과 그리드(<see cref="MonitorGrid"/>)이고 행은 <see cref="MonitorGridBinder{T}"/> 가 채운다.</summary>
public partial class TuningRecommendationsView : UserControl
{
    /// <summary>목록 열(점수 큰 순으로 들어온다 — 머리글을 눌러 다시 정렬할 수 있다).</summary>
    internal static readonly MonitorColumn<TuningRecommendationItem>[] Columns =
    [
        new("점수", ColumnDataKind.Number, i => i.Score),
        new("이유", ColumnDataKind.Text, i => i.ReasonText),
        new("SQL ID", ColumnDataKind.Text, i => i.SqlId),
        new("스키마", ColumnDataKind.Text, i => i.Schema),
        new("실행", ColumnDataKind.Number, i => i.Sql.Executions),
        new("실행당 경과(s)", ColumnDataKind.Number, i => i.ElapsedPerExec),
        new("실행당 읽기", ColumnDataKind.Number, i => i.GetsPerExec),
        new("SQL", ColumnDataKind.Text, i => i.Preview),
    ];

    private readonly MonitorGridBinder<TuningRecommendationItem> _grid;
    private TuningRecommendationsViewModel? _bound;

    public TuningRecommendationsView()
    {
        InitializeComponent();
        _grid = new MonitorGridBinder<TuningRecommendationItem>(RecommendList, Columns, i => i.Key);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TuningRecommendationsViewModel vm && !ReferenceEquals(_bound, vm))
            {
                _bound = vm;
                _grid.AttachStatusLine(StatusLine, vm, () => vm.StatusText);
                _grid.Attach(vm.Items, vm, nameof(TuningRecommendationsViewModel.SelectedItem), () => vm.SelectedItem, i => vm.SelectedItem = i);
            }
        };
        RecRefreshButton.Click += (_, _) => _ = _bound?.RefreshAsync();
        RecAssistantButton.Click += (_, _) => _ = _bound?.OpenInAssistantAsync();
        RecChildCursorsButton.Click += (_, _) => _ = _bound?.OpenChildCursorsAsync();
        RecEditorButton.Click += (_, _) => _ = _bound?.OpenInEditorAsync();
        RecommendList.RowDoubleTapped += (_, _) => _ = _bound?.OpenInEditorAsync();
    }

    /// <summary>시험용: 목록 바인더.</summary>
    internal MonitorGridBinder<TuningRecommendationItem> Binder => _grid;
}
