using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;

namespace Oranda.App.Features.DbTools;

/// <summary>객체 검색 문서. 단추는 <see cref="ObjectSearchViewModel"/> 의 동작을 부른다(명령 ID 와 같은 동작).</summary>
public partial class ObjectSearchView : UserControl
{
    public ObjectSearchView()
    {
        InitializeComponent();
        TypeBox.ItemsSource = ObjectSearchViewModel.SearchableTypes;
        SearchButton.Click += (_, _) => Run(vm => vm.SearchAsync());
        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Run(vm => vm.SearchAsync());
            }
        };
        SourceTypeBox.ItemsSource = ObjectSearchViewModel.SourceTypes;
        SourceSearchButton.Click += (_, _) => Run(vm => vm.SearchSourceAsync());
        SourceTextBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Run(vm => vm.SearchSourceAsync());
            }
        };
        // 두 번 누른 줄(빈 곳이면 아무것도 안 한다)을 연다.
        SourceList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is SourceSearchItem item)
            {
                Run(vm => vm.OpenSourceHitAsync(item));
            }
        };
        ColumnSearchButton.Click += (_, _) => Run(vm => vm.SearchColumnsAsync());
        ColumnNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
            {
                Run(vm => vm.SearchColumnsAsync());
            }
        };
        ColumnList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as StyledElement)?.DataContext is ColumnSearchItem item)
            {
                Run(vm => vm.OpenColumnHitAsync(item));
            }
        };
        CompileButton.Click += (_, _) => Run(vm => vm.CompileInvalidAsync());
        RecycleRefreshButton.Click += (_, _) => Run(vm => vm.LoadRecycleBinAsync());
        FlashbackButton.Click += (_, _) => Run(vm => vm.FlashbackAsync());
        PurgeButton.Click += (_, _) => Run(vm => vm.PurgeAsync());
        PurgeAllButton.Click += (_, _) => Run(vm => vm.PurgeAllAsync());
    }

    private void Run(Func<ObjectSearchViewModel, Task> action)
    {
        if (DataContext is ObjectSearchViewModel vm)
        {
            _ = action(vm);
        }
    }
}
