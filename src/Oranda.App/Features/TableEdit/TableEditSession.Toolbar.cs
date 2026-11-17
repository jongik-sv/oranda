using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>편집 도구줄(＋ － ↑ ↓ ↺)이 쓰는 고른 줄과 페이지별 동작(설계 7.2~7.4, 시안 화면 0 A·③·④).</summary>
public sealed partial class TableEditSession
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveUp), nameof(CanMoveDown))]
    private ColumnRowViewModel? _selectedColumn;

    [ObservableProperty]
    private ConstraintRowViewModel? _selectedConstraint;

    [ObservableProperty]
    private IndexRowViewModel? _selectedIndex;

    /// <summary>지금 보이는 편집 페이지(객체 상세가 구역을 고를 때 맞춘다). 편집 페이지가 아니면 null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsColumnsPage), nameof(IsConstraintsPage), nameof(IsIndexesPage), nameof(IsDescriptionPage), nameof(IsPartitionsPage))]
    private TableEditPage? _currentPage;

    public bool IsColumnsPage => CurrentPage == TableEditPage.Columns;

    public bool IsConstraintsPage => CurrentPage == TableEditPage.Constraints;

    public bool IsIndexesPage => CurrentPage == TableEditPage.Indexes;

    public bool IsDescriptionPage => CurrentPage == TableEditPage.Description;

    public bool IsPartitionsPage => CurrentPage == TableEditPage.Partitions;

    /// <summary>형식 칸 목록(방언, 설계 7.2).</summary>
    public IReadOnlyList<string> TypeNames => Dialect.TypeNames;

    /// <summary>고른 줄(페이지별).</summary>
    public TableEditRowViewModel? SelectedRow(TableEditPage page) => page switch
    {
        TableEditPage.Columns => SelectedColumn,
        TableEditPage.Constraints => SelectedConstraint,
        TableEditPage.Indexes => SelectedIndex,
        _ => null,
    };

    /// <summary>「＋」을 쓸 수 있다(설명 페이지·제한된 테이블은 아님).</summary>
    public bool CanAdd(TableEditPage page) => page switch
    {
        TableEditPage.Columns => AllowsColumnAddDrop,
        TableEditPage.Constraints or TableEditPage.Indexes => !IsRestricted,
        _ => false,
    };

    public bool CanMoveUp => SelectedColumn is { } r && CanMoveColumn(r.Key, -1);

    public bool CanMoveDown => SelectedColumn is { } r && CanMoveColumn(r.Key, 1);

    /// <summary>「＋」: 열·인덱스는 새 줄, 제약은 <paramref name="kind"/>(기본 CHECK). 새 줄을 고른다.</summary>
    public TableEditRowViewModel? AddRow(TableEditPage page, ConstraintKind kind = ConstraintKind.Check)
    {
        switch (page)
        {
            case TableEditPage.Columns:
                return SelectedColumn = AddColumn() ?? SelectedColumn;
            case TableEditPage.Constraints:
                return SelectedConstraint = AddConstraint(kind) ?? SelectedConstraint;
            case TableEditPage.Indexes:
                return SelectedIndex = AddIndex() ?? SelectedIndex;
            default:
                return null;
        }
    }

    /// <summary>「－」: 고른 줄 지우기 토글(새 줄은 뺀다).</summary>
    public void DropSelected(TableEditPage page)
    {
        if (SelectedRow(page) is { } row)
        {
            ToggleDrop(row.Key);
            RestoreSelection(page, row.Key);
        }
    }

    /// <summary>「↺」: 고른 줄을 원본대로.</summary>
    public void RevertSelected(TableEditPage page)
    {
        if (SelectedRow(page) is { } row)
        {
            Revert(row.Key);
            RestoreSelection(page, row.Key);
        }
    }

    /// <summary>「↑」「↓」: 고른 새 열 옮기기.</summary>
    public void MoveSelected(int delta)
    {
        if (SelectedColumn is { } row)
        {
            MoveColumn(row.Key, delta);
            SelectedColumn = Columns.FirstOrDefault(r => r.Key == row.Key);
            OnPropertyChanged(nameof(CanMoveUp));
            OnPropertyChanged(nameof(CanMoveDown));
        }
    }

    private void RestoreSelection(TableEditPage page, string key)
    {
        switch (page)
        {
            case TableEditPage.Columns:
                SelectedColumn = Columns.FirstOrDefault(r => r.Key == key);
                break;
            case TableEditPage.Constraints:
                SelectedConstraint = Constraints.FirstOrDefault(r => r.Key == key);
                break;
            case TableEditPage.Indexes:
                SelectedIndex = Indexes.FirstOrDefault(r => r.Key == key);
                break;
        }
    }
}
