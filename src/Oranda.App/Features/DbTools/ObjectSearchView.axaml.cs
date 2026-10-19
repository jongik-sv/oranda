using System;
using System.Threading.Tasks;
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
