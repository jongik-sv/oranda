using System.Collections.Specialized;
using Avalonia.Controls;

namespace Oranda.App.Features.Execution;

public partial class MessagesView : UserControl
{
    public MessagesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MessagesViewModel vm)
            {
                vm.Messages.CollectionChanged += OnChanged;
            }
        };
    }

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 새 줄이 생기면 맨 아래로 따라간다.
        if (e.Action == NotifyCollectionChangedAction.Add && MessageList.ItemCount > 0)
        {
            MessageList.ScrollIntoView(MessageList.ItemCount - 1);
        }
    }
}
