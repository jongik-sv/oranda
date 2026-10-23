using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

/// <summary>
/// 「문장」 창이 가상화되는 목록(ListBox)에 주는 평평한 줄 목록: 묶음 머리 줄 다음에 그 묶음이 펼쳐져 있으면 항목 줄들.
/// 전체 바꾸기·여러 줄 넣고 빼기를 알림 한 번으로 한다(줄마다 알리면 1MB 매퍼의 요소 수천 개가 줄마다 목록을 다시 계산한다).
/// </summary>
public sealed class MyBatisStatementRows : ObservableCollection<MyBatisStatementNode>
{
    /// <summary>내용을 통째로 바꾼다(알림 1회: Reset).</summary>
    internal void ReplaceAll(IEnumerable<MyBatisStatementNode> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        NotifyChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary><paramref name="index"/> 자리에 여러 줄을 한꺼번에 넣는다(알림 1회: Add).</summary>
    internal void InsertRange(int index, IReadOnlyList<MyBatisStatementNode> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        for (int i = 0; i < items.Count; i++)
        {
            Items.Insert(index + i, items[i]);
        }

        NotifyChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, items.ToList(), index));
    }

    /// <summary><paramref name="index"/> 부터 <paramref name="count"/> 줄을 한꺼번에 뺀다(알림 1회: Remove).</summary>
    internal void RemoveRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        CheckReentrancy();
        var removed = new List<MyBatisStatementNode>(count);
        for (int i = 0; i < count; i++)
        {
            removed.Add(Items[index]);
            Items.RemoveAt(index);
        }

        NotifyChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, index));
    }

    private void NotifyChanged(NotifyCollectionChangedEventArgs args)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(args);
    }
}
