using System.Globalization;
using Avalonia.Threading;
using Oranda.App.Features.Localization;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;

namespace Oranda.App.Features.MyBatisWorkspace.Statements;

// MyBatisStatementsViewModel 의 일부: 목록 짓기 · 표시 고치기(MyBatisStatementsViewModel.cs 에서 나눔, 동작 불변).
public sealed partial class MyBatisStatementsViewModel
{
    // ---- 목록 짓기 · 표시 고치기 ----------------------------------------------------------------

    private void Rebuild()
    {
        if (_session is null)
        {
            return;
        }

        var entries = _session.VisibleEntries.ToList();
        _shown = entries;
        _groups.Clear();
        _current = _session.ActiveEntry;
        var rows = new List<MyBatisStatementNode>(entries.Count + GroupOrder.Length);
        foreach (var kind in GroupOrder)
        {
            var members = entries.Where(e => e.Kind == kind && MyBatisQueryFilter.Matches(EntryTitle(e), _terms) && PassesStateFilter(e)).ToList();
            if (members.Count == 0)
            {
                continue;
            }

            var group = new MyBatisStatementNode(GroupTitle(kind), kind, entry: null)
            {
                IsExpanded = IsFiltering || !_collapsed.Contains(kind), // 거르는 동안은 맞은 것이 보이게 모두 펼친다(기억한 접힘은 그대로)
                ExpandedChangedCallback = OnGroupExpandedChanged,
            };
            group.Title = GroupTitleFor(kind, members.Count);
            foreach (var entry in members)
            {
                var node = new MyBatisStatementNode(string.Empty, kind, entry) { Highlight = _appliedSearch };
                RefreshNode(node);
                RefreshBadges(node);
                group.Children.Add(node);
            }

            _groups.Add(group);
            rows.Add(group);
            if (group.IsExpanded)
            {
                rows.AddRange(group.Children);
            }
        }

        MutateRows(() => _rows.ReplaceAll(rows));
        ShowsNoMatch = IsFiltering && rows.Count == 0;
        RefreshUsage();
        UpdateCounts();
    }

    /// <summary>「열린 것만」·「변경된 것만」 거르기(둘 다 켜면 둘 다 맞아야 한다).</summary>
    private bool PassesStateFilter(MyBatisStatementEntry entry) =>
        (!ShowOpenOnly || entry.IsOpen) && (!ShowChangedOnly || IsChanged(entry));

    /// <summary>변경 = 저장 안 한 고침(새 쿼리 포함)이 있거나 xml 에 저장해 연 때와 달라졌다.</summary>
    private static bool IsChanged(MyBatisStatementEntry entry) => entry.IsModified || entry.IsSavedChange;

    private bool IsStateFiltering => ShowOpenOnly || ShowChangedOnly;

    /// <summary>상태 거르기 중이면 지금 목록에 있는 요소가 거르기에 맞는 요소와 같은가(탭을 닫거나 고침을 되돌리면 빠진다).</summary>
    private bool StateMembersUnchanged()
    {
        if (!IsStateFiltering || _session is null)
        {
            return true;
        }

        var listed = new HashSet<MyBatisStatementEntry>(_groups.SelectMany(g => g.Children).Select(n => n.Entry!), ReferenceEqualityComparer.Instance);
        int passing = 0;
        foreach (var entry in _session.VisibleEntries)
        {
            if (!MyBatisQueryFilter.Matches(EntryTitle(entry), _terms) || !PassesStateFilter(entry))
            {
                continue;
            }

            if (!listed.Contains(entry))
            {
                return false;
            }

            passing++;
        }

        return passing == listed.Count;
    }

    /// <summary>목록 위 「열림 n」·「변경 n」 단추의 수(거르기·검색과 상관없이 매퍼 전체).</summary>
    private void UpdateCounts()
    {
        int open = 0, unsaved = 0, saved = 0;
        foreach (var entry in _session?.VisibleEntries ?? [])
        {
            if (entry.IsOpen)
            {
                open++;
            }

            if (entry.IsModified)
            {
                unsaved++;
            }
            else if (entry.IsSavedChange)
            {
                saved++;
            }
        }

        OpenCount = open;
        UnsavedCount = unsaved;
        SavedCount = saved;
        ChangedCount = unsaved + saved;
        OpenFilterText = string.Format(CultureInfo.CurrentCulture, T("filter.open"), open);
        ChangedFilterText = string.Format(CultureInfo.CurrentCulture, T("filter.changed"), unsaved + saved);
        OpenFilterToolTip = string.Format(CultureInfo.CurrentCulture, T("filter.openTip"), open);
        ChangedFilterToolTip = string.Format(CultureInfo.CurrentCulture, T("filter.changedTip"), unsaved, saved);
    }

    /// <summary>상태 칸 툴팁: 그 행에 있는 표시마다 한 줄, 끝에 탭 열림·지금 탭(표시가 없으면 툴팁 없음).</summary>
    private void UpdateStatusToolTip(MyBatisStatementNode node)
    {
        var lines = new List<string>();
        if (node.NewText.Length > 0)
        {
            lines.Add(T("tip.new"));
        }
        else if (node.ModifiedText.Length > 0)
        {
            lines.Add(T("tip.modified"));
        }

        if (node.Entry is { IsSavedChange: true })
        {
            lines.Add(T("tip.saved"));
        }

        if (node.AmbiguousText.Length > 0)
        {
            lines.Add(T("tip.ambiguous"));
        }

        if (node.KeepText.Length > 0)
        {
            lines.Add(T("tip.keep"));
        }

        foreach (var badge in node.Badges)
        {
            lines.Add(badge.IsError ? string.Format(CultureInfo.CurrentCulture, T("tip.combo"), badge.ShortText) : badge.ToolTip ?? badge.Text);
        }

        if (node.UsageNumber.Length > 0)
        {
            lines.Add(node.UsageNumber == "0"
                ? T("tip.unused")
                : string.Format(CultureInfo.CurrentCulture, T("tip.usage"), node.UsageNumber));
        }

        if (node.IsCurrent)
        {
            lines.Add(T("tip.current"));
        }
        else if (node.IsOpen)
        {
            lines.Add(T("tip.open"));
        }

        var tip = lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        if (!string.Equals(tip, node.StatusToolTip, StringComparison.Ordinal))
        {
            node.StatusToolTip = tip;
        }
    }

    private static string EntryTitle(MyBatisStatementEntry entry) => entry.Id ?? $"<{entry.Name}>";

    /// <summary>거르는 중이면 묶음 머리에 맞은 수를 붙인다.</summary>
    private string GroupTitleFor(MyBatisStatementKind kind, int matched) =>
        IsFiltering ? string.Create(CultureInfo.InvariantCulture, $"{GroupTitle(kind)} ({matched})") : GroupTitle(kind);

    private void RefreshNodes()
    {
        _current = _session?.ActiveEntry;
        foreach (var group in _groups)
        {
            foreach (var node in group.Children)
            {
                RefreshNode(node);
                if (!string.Equals(node.BadgedId, node.Entry!.Id, StringComparison.Ordinal))
                {
                    RefreshBadges(node);
                }
            }
        }

        UpdateCounts();
    }

    /// <summary>요소 노드의 표시 글(탭 열림·지금 탭·수정·저장함·새 문장·편집 불가·그대로 둠)을 맞춘다. 값이 같으면 알림이 나가지 않는다.</summary>
    private void RefreshNode(MyBatisStatementNode node)
    {
        var entry = node.Entry!;
        bool modified = entry.IsModified;
        node.Title = entry.Id ?? $"<{entry.Name}>";
        node.IsOpen = entry.IsOpen;
        node.IsCurrent = entry.IsOpen && ReferenceEquals(entry, _current);
        node.ModifiedText = modified && !entry.IsNew ? T("mark.modified") : string.Empty;
        node.NewText = entry.IsNew ? T("mark.new") : string.Empty;
        node.SavedText = !modified && entry.IsSavedChange ? T("mark.saved") : string.Empty;
        node.AmbiguousText = entry.IsAmbiguous ? T("mark.ambiguous") : string.Empty;
        node.KeepText = entry.CanOpen ? string.Empty : T("mark.keep");
        node.RowOpacity = entry.CanOpen ? 1 : 0.6;
        node.ToolTipText = entry.IsAmbiguous ? T("tip.ambiguous") : entry.CanOpen ? null : T("tip.keep");
        UpdateStatusToolTip(node);
    }

    private void RefreshBadges(MyBatisStatementNode node)
    {
        var entry = node.Entry!;
        node.BadgedId = entry.Id;
        var badges = new List<MyBatisStatementBadgeView>();
        if (_session is not null && entry.Id is { } id && entry.CanOpen)
        {
            foreach (var provider in _providers)
            {
                foreach (var badge in provider.BadgesFor(_session, id))
                {
                    badges.Add(new MyBatisStatementBadgeView(badge.Text, badge.ToolTip, badge.Kind));
                }
            }
        }

        if (!node.Badges.SequenceEqual(badges))
        {
            node.Badges = badges;
        }

        UpdateStatusToolTip(node);
    }

    /// <summary>sql 조각 노드의 「n곳에서 씀」. 매퍼 전체 글을 훑으므로 글자를 칠 때는 잠깐 기다렸다가 한 번만 한다.</summary>
    private void RefreshUsage()
    {
        _usageTimer?.Stop();
        if (_session is null)
        {
            return;
        }

        var counts = _session.UsageCounts(); // 매퍼를 한 번만 훑는다(조각 수 × 글 길이가 되지 않게)
        foreach (var group in _groups.Where(g => g.Kind == MyBatisStatementKind.Sql))
        {
            foreach (var node in group.Children)
            {
                node.UsageText = node.Entry!.Id is { } id ? UsageText(counts.GetValueOrDefault(id)) : string.Empty;
                node.UsageNumber = node.Entry!.Id is { } uid ? counts.GetValueOrDefault(uid).ToString(CultureInfo.CurrentCulture) : string.Empty;
                UpdateStatusToolTip(node);
            }
        }
    }

    private string UsageText(int count) =>
        count == 0 ? T("mark.unused") : string.Format(CultureInfo.CurrentCulture, T("mark.usage"), count);

    private void ScheduleUsage()
    {
        if (!_groups.Any(g => g.Kind == MyBatisStatementKind.Sql))
        {
            return;
        }

        if (_usageTimer is null)
        {
            _usageTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _usageTimer.Tick += (_, _) => RefreshUsage();
        }

        _usageTimer.Stop();
        _usageTimer.Start();
    }

    private void OnGroupExpandedChanged(MyBatisStatementNode group)
    {
        if (group.IsExpanded)
        {
            _collapsed.Remove(group.Kind);
        }
        else
        {
            _collapsed.Add(group.Kind);
        }

        int header = _rows.IndexOf(group);
        if (header < 0)
        {
            return;
        }

        if (group.IsExpanded)
        {
            MutateRows(() => _rows.InsertRange(header + 1, group.Children));
            SyncSelection(); // 접혀 있는 동안 목록에서 빠졌던 고른 문장을 다시 보인다
        }
        else
        {
            MutateRows(() =>
            {
                _rows.RemoveRange(header + 1, group.Children.Count);
                if (SelectedNode is { IsGroup: false } picked && picked.Kind == group.Kind)
                {
                    SelectedNode = null; // 보이지 않는 줄은 목록 선택에서 빠진다(세션의 고른 문장은 그대로)
                }
            });
        }
    }

    /// <summary>
    /// 줄 목록을 바꾼다. 화면이 줄을 빼면서 목록 선택을 비우는 것(양방향 연결)은 사용자가 고른 것이 아니므로 세션에 알리지 않는다.
    /// </summary>
    private void MutateRows(Action change)
    {
        bool was = _syncing;
        _syncing = true;
        try
        {
            change();
        }
        finally
        {
            _syncing = was;
        }
    }

    /// <summary>세션의 고른 문장을 목록에 맞춘다(활성 탭을 따라 바뀐 경우 포함). 세션에 다시 알리지 않는다.</summary>
    private void SyncSelection()
    {
        if (_session is null)
        {
            return;
        }

        var picked = _session.SelectedEntry;
        MyBatisStatementNode? target = null;
        if (picked is not null)
        {
            target = _groups.SelectMany(g => g.Children).FirstOrDefault(n => ReferenceEquals(n.Entry, picked));
            if (target is null || _groups.FirstOrDefault(g => g.Kind == target.Kind) is not { IsExpanded: true })
            {
                UpdateCommandStates();
                return; // 목록에 없거나 접힌 묶음 안인 문장은 목록 선택을 건드리지 않는다
            }
        }
        else if (SelectedNode is not { IsGroup: false })
        {
            UpdateCommandStates();
            return;
        }

        if (!ReferenceEquals(SelectedNode, target))
        {
            _syncing = true;
            try
            {
                SelectedNode = target;
            }
            finally
            {
                _syncing = false;
            }
        }

        UpdateCommandStates();
    }
}
