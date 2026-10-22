using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Oranda.App.Features.ObjectDetail;

public partial class ObjectDetailView : UserControl, Oranda.App.Shell.Docking.IFocusableDocumentView
{
    private ObjectDetailViewModel? _vm;

    public ObjectDetailView()
    {
        InitializeComponent();
        CopyButton.Click += async (_, _) =>
        {
            if (_vm is not null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(_vm.DdlText);
            }
        };
        ReorderColumnsButton.Click += async (_, _) => await OpenReorderDialogAsync();
        // 누른 줄의 선택이 끝난 뒤에 읽는다(읽은 결과가 위 표 칸을 고쳐 목록을 다시 지으면 진행 중인 클릭 선택이 사라진다).
        SectionTable.CurrentRowChanged += (_, row) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _ = _vm?.SelectKeyRowAsync(row));
        Tabs.SelectionChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.IsDdlSelected = Tabs.SelectedItem == DdlTab;
            }
        };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
            }

            _vm = DataContext as ObjectDetailViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnVmChanged;
                Sync();
                SyncKeyPane();
            }
        };
    }

    /// <summary>문서로 포커스를 옮길 때(스키마 브라우저에서 두 번 누르기·Enter) 구역 목록에 준다(방향키로 열·인덱스… 를 바로 고른다).</summary>
    public bool FocusContent()
    {
        if (!SectionList.IsEffectivelyVisible)
        {
            return Tabs.Focus();
        }

        // 목록 자체보다 고른 구역 줄에 준다(방향키가 바로 먹는다). 구역을 아직 못 읽었으면 false — 잠깐 뒤 다시 불린다.
        return SectionList.SelectedItem is { } selected && SectionList.ContainerFromItem(selected) is { } row && row.Focus();
    }

    /// <summary>「열 순서 바꾸기…」: 대화상자를 열고, 방식 ① 이 끝나면 상세를 새로 읽는다.</summary>
    private async Task OpenReorderDialogAsync()
    {
        if (_vm is not { Services: { } services, Session: { } session, Target: { } target } vm)
        {
            return;
        }

        await Oranda.App.Features.ColumnReorder.ColumnReorderLauncher.OpenAsync(
            services, session, target, (_, _) => _ = vm.OpenAsync(session, target, vm.IsDdlSelected), CancellationToken.None);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        Sync();
        if (e.PropertyName == nameof(ObjectDetailViewModel.HasKeyColumns))
        {
            SyncKeyPane();
        }
    }

    private (GridLength Top, GridLength Bottom) _keyPaneHeights = (new GridLength(3, GridUnitType.Star), new GridLength(2, GridUnitType.Star));

    /// <summary>열 표 칸 높이: 처음엔 위 표와 3:2, 사용자가 끌어 바꾼 높이는 구역을 오가도 그대로, 숨길 때 0.</summary>
    private void SyncKeyPane()
    {
        var rows = SectionGrid.RowDefinitions;
        if (_vm?.HasKeyColumns != true)
        {
            if (rows[2].Height.Value > 0)
            {
                _keyPaneHeights = (rows[0].Height, rows[2].Height); // 끌어 바꾼 높이를 다음에 그대로 쓴다
            }

            rows[0].Height = new GridLength(1, GridUnitType.Star);
            rows[2].Height = new GridLength(0);
        }
        else if (rows[2].Height.Value == 0)
        {
            rows[0].Height = _keyPaneHeights.Top;
            rows[2].Height = _keyPaneHeights.Bottom;
        }
    }

    private void Sync()
    {
        if (_vm is null)
        {
            return;
        }

        var wanted = _vm.IsDdlSelected ? DdlTab : (TabItem)Tabs.Items[0]!;
        if (Tabs.SelectedItem != wanted)
        {
            Tabs.SelectedItem = wanted;
        }
    }
}
