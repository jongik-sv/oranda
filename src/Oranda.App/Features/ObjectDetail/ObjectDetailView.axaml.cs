using System.ComponentModel;
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

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Sync();

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
