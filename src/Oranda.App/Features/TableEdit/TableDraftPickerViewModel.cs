using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Schema.TableEdit;

namespace Oranda.App.Features.TableEdit;

/// <summary>「만들던 새 테이블…」 목록 한 줄.</summary>
public sealed class TableDraftPickerItem(TableDraftEntry entry)
{
    public TableDraftEntry Entry { get; } = entry;

    /// <summary>이름(아직 안 썼으면 「(이름 없음)」).</summary>
    public string Title => Entry.Name.Length > 0 ? $"{Entry.Owner}.{Entry.Name}" : $"{Entry.Owner}." + TableEditText.T("(이름 없음)", "(no name)");

    /// <summary>고친 때(이 PC 시각).</summary>
    public string UpdatedText => Entry.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    /// <summary>어떻게 남았나: 닫기에서 「남겨 두기」 한 것 / 끝난 앱이 열어 둔 채 남긴 것.</summary>
    public string StateText => Entry.OpenBy is null
        ? TableEditText.T("남겨 둠", "Kept")
        : TableEditText.T("끝난 앱에서 열려 있던 것", "Left open by an app that ended");
}

/// <summary>
/// 「만들던 새 테이블…」 고르기(설계 18.7, TE-43): 이 접속·소유자의 남겨 둔 것·끝난 앱이 남긴 것(최근 순)을 보이고 「열기」·「지우기」.
/// 지우기는 목록을 읽은 뒤 누가 쥐었으면 하지 않는다(<see cref="TableDraftLibrary.DeleteAsync"/>).
/// </summary>
public sealed partial class TableDraftPickerViewModel : ObservableObject
{
    private readonly Func<TableDraftEntry, CancellationToken, Task<bool>> _delete;

    public TableDraftPickerViewModel(string owner, IReadOnlyList<TableDraftEntry> entries, Func<TableDraftEntry, CancellationToken, Task<bool>> delete)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(delete);
        Owner = owner;
        _delete = delete;
        foreach (var e in entries)
        {
            Items.Add(new TableDraftPickerItem(e));
        }

        SelectedItem = Items.FirstOrDefault();
    }

    public string Owner { get; }

    public ObservableCollection<TableDraftPickerItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanDelete))]
    private TableDraftPickerItem? _selectedItem;

    /// <summary>지우는 중(두 번 눌러도 한 번만).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    private bool _isDeleting;

    /// <summary>「지우기」를 누를 수 있다(고른 줄이 있고 지우는 중이 아님).</summary>
    public bool CanDelete => SelectedItem is not null && !IsDeleting;

    [ObservableProperty]
    private string _statusText = string.Empty;

    public bool HasSelection => SelectedItem is not null;

    /// <summary>「열기」로 고른 것. 고르지 않고 닫으면 null.</summary>
    public TableDraftEntry? Chosen { get; private set; }

    /// <summary>창을 닫으라는 알림(열기를 고른 뒤).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>「열기」: 고른 줄로 끝낸다.</summary>
    public void OpenSelected()
    {
        if (SelectedItem is { } item)
        {
            Chosen = item.Entry;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>「지우기」: 고른 줄을 저장소에서 지우고 목록에서 뺀다. 그새 다른 곳에서 열었거나 바뀌었으면 남기고 알린다.</summary>
    public async Task DeleteSelectedAsync(CancellationToken ct = default)
    {
        if (SelectedItem is not { } item || IsDeleting)
        {
            return;
        }

        IsDeleting = true;
        try
        {
            if (!await _delete(item.Entry, ct))
            {
                StatusText = TableEditText.T("다른 탭이나 다른 Oranda 가 그새 열어 지우지 않았습니다", "Another tab or Oranda window opened it meanwhile; not deleted");
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            StatusText = TableEditText.T("지우지 못했습니다: ", "Could not delete: ") + ex.Message;
            return;
        }
        finally
        {
            IsDeleting = false;
        }

        var at = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(at, Items.Count - 1)];
        StatusText = TableEditText.T($"지웠습니다: {item.Title}", $"Deleted: {item.Title}");
    }
}
