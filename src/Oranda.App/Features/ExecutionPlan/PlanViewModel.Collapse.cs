using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 트리 접기·펼치기(p4). <see cref="AllRows"/> 는 모든 단계(Id 순), <see cref="PlanViewModel.Rows"/> 는 지금 보이는 줄이다.
/// 접힌 줄 아래의 하위 단계는 <c>Rows</c> 에서 빠지지만 순서(c-order)·안내선·가장 비싼 단계 표시는 각 줄이 그대로 갖고 있다.
/// 그래프 탭의 자동 접기(<c>_collapsed</c>)와는 상태를 따로 둔다: 그래프는 크기 때문에 자동으로 접고, 트리는 사용자가 접는다.
/// 새 계획(<see cref="Apply"/>)은 모두 펼친 상태로 시작한다.
/// </summary>
public sealed partial class PlanViewModel
{
    private List<PlanRowItem> _allRows = [];

    /// <summary>접기와 상관없이 모든 단계(Id 순).</summary>
    public IReadOnlyList<PlanRowItem> AllRows => _allRows;

    /// <summary>접을 수 있는 줄(펼쳐 있는 부모)이 있다.</summary>
    [ObservableProperty]
    private bool _hasCollapsible;

    /// <summary>접혀 있는 줄이 있다.</summary>
    [ObservableProperty]
    private bool _hasCollapsed;

    /// <summary>줄의 접기·펼치기를 바꾼다(자식이 없으면 아무 일도 없다).</summary>
    public void ToggleRow(PlanRowItem row)
    {
        ArgumentNullException.ThrowIfNull(row);
        SetCollapsed(row, !row.IsCollapsed);
    }

    /// <summary>줄의 하위 트리를 접거나 편다.</summary>
    public void SetCollapsed(PlanRowItem row, bool collapsed)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!row.HasChildren || row.IsCollapsed == collapsed)
        {
            return;
        }

        row.IsCollapsed = collapsed;
        RefreshVisibility();
    }

    /// <summary>부모가 있는 모든 줄을 접는다(뿌리 한 줄만 남는다).</summary>
    public void CollapseAll()
    {
        foreach (var row in _allRows)
        {
            if (row.HasChildren)
            {
                row.IsCollapsed = true;
            }
        }

        RefreshVisibility();
    }

    /// <summary>접힌 줄을 모두 편다.</summary>
    public void ExpandAll()
    {
        foreach (var row in _allRows)
        {
            row.IsCollapsed = false;
        }

        RefreshVisibility();
    }

    /// <summary>← 키: 펼쳐 있으면 접고, 이미 접혔거나 자식이 없으면 부모를 고른다. 처리했으면 true.</summary>
    public bool CollapseOrSelectParent()
    {
        if (SelectedRow is not { } row)
        {
            return false;
        }

        if (row.HasChildren && !row.IsCollapsed)
        {
            SetCollapsed(row, true);
            return true;
        }

        if (ParentOf(row) is { } parent)
        {
            SelectedRow = parent;
            return true;
        }

        return false;
    }

    /// <summary>→ 키: 접혀 있으면 편다. 처리했으면 true.</summary>
    public bool ExpandSelected()
    {
        if (SelectedRow is { IsCollapsed: true } row)
        {
            SetCollapsed(row, false);
            return true;
        }

        return false;
    }

    /// <summary>그 단계가 접힌 부모 밑에 숨어 있으면 부모들을 펴서 보이게 한다(그래프에서 고를 때).</summary>
    internal void RevealRow(int stepId)
    {
        var index = _allRows.FindIndex(r => r.Id == stepId);
        if (index < 0)
        {
            return;
        }

        var changed = false;
        for (var ancestor = ParentOf(_allRows[index]); ancestor is not null; ancestor = ParentOf(ancestor))
        {
            if (ancestor.IsCollapsed)
            {
                ancestor.IsCollapsed = false;
                changed = true;
            }
        }

        if (changed)
        {
            RefreshVisibility();
        }
    }

    /// <summary>새 계획을 넣은 뒤: 접힘 값(모두 펼침)을 바탕으로 숨은 단계 수·단추 상태를 다시 구한다.</summary>
    private void UpdateCollapseState() => RefreshVisibility();

    private PlanRowItem? ParentOf(PlanRowItem row)
    {
        var index = _allRows.IndexOf(row);
        for (var i = index - 1; i >= 0; i--)
        {
            if (_allRows[i].Depth < row.Depth)
            {
                return _allRows[i];
            }
        }

        return null;
    }

    /// <summary>접힘 상태에 맞춰 <see cref="PlanViewModel.Rows"/> 를 맞추고(없어질 줄만 빼고 생길 줄만 넣는다) 접힌 줄의 숨은 단계 수를 갱신한다.</summary>
    private void RefreshVisibility()
    {
        var desired = new List<PlanRowItem>(_allRows.Count);
        var hideBelow = -1;
        foreach (var row in _allRows)
        {
            if (hideBelow >= 0 && row.Depth > hideBelow)
            {
                continue;
            }

            hideBelow = -1;
            desired.Add(row);
            if (row.IsCollapsed && row.HasChildren)
            {
                hideBelow = row.Depth;
            }
        }

        var keep = new HashSet<PlanRowItem>(desired);

        // 고른 줄이 숨겨지면 그 줄을 감춘 가장 가까운 보이는 부모로 옮긴다(목록에서 빠지기 전에, 화면이 선택을 비우지 못하게).
        if (SelectedRow is { } selected && !keep.Contains(selected))
        {
            var target = ParentOf(selected);
            while (target is not null && !keep.Contains(target))
            {
                target = ParentOf(target);
            }

            SelectedRow = target;
        }

        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
            {
                Rows.RemoveAt(i);
            }
        }

        for (var j = 0; j < desired.Count; j++)
        {
            if (j >= Rows.Count || !ReferenceEquals(Rows[j], desired[j]))
            {
                Rows.Insert(j, desired[j]);
            }
        }

        for (var i = 0; i < _allRows.Count; i++)
        {
            var row = _allRows[i];
            if (!row.HasChildren)
            {
                continue;
            }

            var count = 0;
            var hot = false;
            for (var k = i + 1; k < _allRows.Count && _allRows[k].Depth > row.Depth; k++)
            {
                count++;
                hot |= _allRows[k].IsHottest;
            }

            row.HiddenCount = count;
            row.HiddenHottest = row.IsCollapsed && hot;
        }

        HasCollapsed = _allRows.Exists(r => r.IsCollapsed);
        HasCollapsible = _allRows.Exists(r => r.HasChildren && !r.IsCollapsed);
    }
}
