using System.Collections.Specialized;
using System.ComponentModel;
using Oranda.App.Features.SqlEditor;

namespace Oranda.App.Features.MyBatisWorkspace.Workspace;

// MyBatisWorkspaceSession 의 일부: 문장 탭 추적 · 작업 공간 이벤트(MyBatisWorkspaceSession.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisWorkspaceSession
{
    private void Track(MyBatisStatementEntry entry, SqlEditorViewModel tab)
    {
        entry.Tab = tab;
        tab.IsReadOnly = !entry.IsEditable;
        tab.TextChanged += OnTabTextChanged;
        tab.IsDirty = entry.IsModified;
    }

    private void Untrack(MyBatisStatementEntry entry)
    {
        if (entry.Tab is { } tab)
        {
            tab.TextChanged -= OnTabTextChanged;
            entry.StoredText = tab.Text;
            entry.Tab = null;
        }
    }

    private void OnTabTextChanged(object? sender, EventArgs e)
    {
        if (sender is SqlEditorViewModel tab && EntryOf(tab) is { } entry)
        {
            // 탭에서 id 속성을 직접 고치거나 id 바꾸기를 실행 취소하면 목록 id 도 따라간다. 쓸 수 없는 id(입력 중·겹침)면 마지막 id 를 둔다.
            if (entry.IsEditable && MyBatisStatementText.TryFindIdValue(tab.Text, out var start, out var length)
                && tab.Text.AsSpan(start, length) is var span && !span.SequenceEqual(entry.Id)
                && span.ToString() is var typed && ValidateId(typed, entry) is null)
            {
                entry.Id = typed;
                tab.Title = typed;
            }

            tab.IsDirty = entry.IsModified; // 탭 VM 은 글이 바뀌면 늘 dirty 로 둔다 → 기준과 같아지면 다시 내린다
            if (!_batching)
            {
                RaiseStateChanged();
            }
        }
    }

    /// <summary>탭 글을 <paramref name="text"/> 로: 같은 앞뒤는 두고 바뀐 가운데만 한 번 교체한다(캐럿·접기가 덜 흔들리고 실행 취소 1단계).</summary>
    private static void ReplaceChanged(SqlEditorViewModel tab, string text)
    {
        var before = tab.Text;
        if (string.Equals(before, text, StringComparison.Ordinal))
        {
            return;
        }

        int max = Math.Min(before.Length, text.Length), prefix = 0, suffix = 0;
        while (prefix < max && before[prefix] == text[prefix])
        {
            prefix++;
        }

        while (suffix < max - prefix && before[before.Length - 1 - suffix] == text[text.Length - 1 - suffix])
        {
            suffix++;
        }

        tab.Document.Replace(prefix, before.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));
    }

    private void OnWorkspaceTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_syncingTabs)
        {
            return;
        }

        bool closed = false;
        foreach (var tab in e.OldItems?.OfType<SqlEditorViewModel>() ?? [])
        {
            // 탭 순서 바꾸기(Move)도 OldItems 에 그 탭이 온다: 작업 공간에 남아 있으면 그대로 잇는다.
            if (!_workspace.Tabs.Contains(tab) && EntryOf(tab) is { } entry)
            {
                Untrack(entry); // 탭을 닫아도 고침은 남는다(design.md 2.2)
                closed = true;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _entries.Where(x => x.Tab is not null && !_workspace.Tabs.Contains(x.Tab)).ToList())
            {
                Untrack(entry);
                closed = true;
            }
        }

        if (closed)
        {
            RaiseStateChanged(); // 「매퍼 쿼리」 목록의 열림 표시가 바로 풀리게
        }
    }

    private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SqlWorkspaceViewModel.MyBatis) && !ReferenceEquals(_workspace.MyBatis, this))
        {
            Detach(); // 작업 공간을 닫으면 호스트가 세션을 뗀다
        }
        else if (e.PropertyName == nameof(SqlWorkspaceViewModel.ActiveTab))
        {
            // 고른 문장은 활성 문장 탭을 따라간다(문장 창에서 고른 뒤 다른 탭으로 가면 그 탭의 문장).
            _selected = _workspace.ActiveTab is { } tab && EntryOf(tab) is { } entry ? entry : _selected;
            _noneSelected = false;
            RaiseStateChanged();
        }
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
