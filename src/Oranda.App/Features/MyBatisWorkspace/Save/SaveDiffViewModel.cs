using System.Globalization;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Language.MyBatis;

namespace Oranda.App.Features.MyBatisWorkspace.Save;

/// <summary>검사 상자 한 줄(시안 SaveDiff 「✓ XML 형식」). <see cref="IconKey"/> = <c>ok</c>·<c>warn</c>·<c>error</c>(셸 아이콘 키).</summary>
public sealed record SaveDiffCheckLine(string IconKey, string Text);

/// <summary>왼쪽 목록의 문장 하나: 체크와 「수정 · +5 −1」 요약. 체크를 바꾸면 창이 검사 상자·diff 를 다시 그린다.</summary>
public sealed class SaveDiffItemViewModel : ViewModelBase
{
    private readonly SaveDiffViewModel _owner;
    private bool _isChecked = true;

    internal SaveDiffItemViewModel(SaveDiffViewModel owner, MapperSaveItem item)
    {
        _owner = owner;
        Item = item;
    }

    public MapperSaveItem Item { get; }

    /// <summary>목록에 쓰는 id. id 를 바꾼 문장은 「옛 → 새」, 지운 문장은 지운 id.</summary>
    public string Id => Item.Kind switch
    {
        MapperSaveItemKind.Deleted => Item.Key,
        MapperSaveItemKind.Renamed => Item.Key + " → " + Item.CurrentId,
        _ => Item.CurrentId,
    };

    /// <summary>「수정 · +5 −1」·「새 문장」·「지움 · −8」·「id 바꿈」.</summary>
    public string Summary => Item.Kind switch
    {
        MapperSaveItemKind.Added => UiText.Get("MyBatis.save.item.added", "새 문장"),
        MapperSaveItemKind.Deleted => Format("MyBatis.save.item.deleted", "지움 · −{1}"),
        MapperSaveItemKind.Renamed => Format("MyBatis.save.item.renamed", "id 바꿈 · +{0} −{1}"),
        _ => Format("MyBatis.save.item.modified", "수정 · +{0} −{1}"),
    };

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _owner.OnItemCheckedChanged();
            }
        }
    }

    private string Format(string key, string fallback) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), Item.Added, Item.Removed);
}

/// <summary>
/// 「바뀌는 부분」 창의 상태(design.md 5.1 의 5번, 시안 SaveDiff). 창 없이 시험할 수 있게 논리는 여기에 둔다:
/// 체크를 바꾸면 <see cref="SaveDiffRequest.Check"/> 를 체크한 항목으로 다시 불러 검사 상자를 갱신하고, 저장 가능 여부·결과를 만든다.
/// </summary>
public sealed class SaveDiffViewModel : ViewModelBase
{
    private readonly SaveDiffRequest _request;
    private readonly Dictionary<SaveDiffItemViewModel, int> _headerRows = new(ReferenceEqualityComparer.Instance);
    private bool _sideBySide;
    private bool _showEveryTime;
    private SaveDiffItemViewModel? _selected;
    private MapperWriteCheck _lastCheck = new(null, [], false);
    private IReadOnlyList<SaveDiffCheckLine> _checks = [];
    private IReadOnlyList<MapperDiffRow> _rows = [];
    private int _selectedRowIndex = -1;
    private bool _starting = true;

    public SaveDiffViewModel(SaveDiffRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _showEveryTime = request.ShowEveryTime;
        Items = [.. request.Items.Select(i => new SaveDiffItemViewModel(this, i))];
        _selected = Items.Count > 0 ? Items[0] : null;
        _starting = false;
        Recompute();
    }

    public IReadOnlyList<SaveDiffItemViewModel> Items { get; }

    /// <summary>머리 「EmpMapper.xml 에 저장」.</summary>
    public string Title => Format("MyBatis.save.dialog.title", "{0} 에 저장", _request.FileName);

    public static string Description => UiText.Get("MyBatis.save.dialog.description", "바뀐 문장만 파일에 쓰고 나머지는 그대로 둡니다.");

    public string UnchangedText => Format("MyBatis.save.dialog.unchanged", "바뀌지 않은 문장 {0}개", _request.UnchangedCount);

    public static string ChecksHeading => UiText.Get("MyBatis.save.dialog.checks", "저장 전 검사");

    public static string DiffHeading => UiText.Get("MyBatis.save.dialog.diff", "바뀌는 부분 (git diff 와 같은 모양)");

    /// <summary>고른 문장: 오른쪽 diff 가 그 문장의 머리 줄로 간다.</summary>
    public SaveDiffItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                UpdateSelectedRow();
            }
        }
    }

    /// <summary>나란히 보기(거짓 = 한 줄 보기).</summary>
    public bool IsSideBySide
    {
        get => _sideBySide;
        set
        {
            if (SetProperty(ref _sideBySide, value))
            {
                OnPropertyChanged(nameof(IsOneLine));
                RebuildRows();
            }
        }
    }

    public bool IsOneLine
    {
        get => !_sideBySide;
        set => IsSideBySide = !value;
    }

    /// <summary>「저장할 때마다 이 창 보기」.</summary>
    public bool ShowEveryTime
    {
        get => _showEveryTime;
        set => SetProperty(ref _showEveryTime, value);
    }

    /// <summary>체크한 항목에 대한 마지막 검사 결과.</summary>
    public MapperWriteCheck LastCheck => _lastCheck;

    public IReadOnlyList<SaveDiffCheckLine> Checks => _checks;

    /// <summary>오른쪽 diff 의 줄들: 체크한 문장마다 머리 줄 + diff 줄.</summary>
    public IReadOnlyList<MapperDiffRow> Rows => _rows;

    /// <summary>고른 문장의 머리 줄이 <see cref="Rows"/> 의 몇 번째인지(체크를 껐으면 -1). 바뀌면 창이 그 줄로 스크롤한다.</summary>
    public int SelectedRowIndex => _selectedRowIndex;

    public IReadOnlyList<MapperSaveItem> CheckedItems => [.. Items.Where(i => i.IsChecked).Select(i => i.Item)];

    public int CheckedCount => Items.Count(i => i.IsChecked);

    /// <summary>저장 단추를 켠다: 쓸 수 있는 검사 결과이고 체크한 항목이 있다.</summary>
    public bool CanSave => _lastCheck.CanWrite && CheckedCount > 0;

    /// <summary>저장 단추 글 「문장 2개 저장」.</summary>
    public string SaveText => Format("MyBatis.save.dialog.save", "문장 {0}개 저장", CheckedCount);

    /// <summary>저장할 수 있을 때만 결과(체크한 항목 + 확인란 값). 아니면 null.</summary>
    public SaveDiffResult? BuildResult() => CanSave ? new SaveDiffResult(CheckedItems, _showEveryTime) : null;

    internal void OnItemCheckedChanged()
    {
        if (!_starting)
        {
            Recompute();
        }
    }

    // 체크가 바뀔 때마다: 검사를 다시 부르고, diff 와 단추를 다시 만든다.
    private void Recompute()
    {
        var chosen = CheckedItems;
        try
        {
            _lastCheck = _request.Check(chosen);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            _lastCheck = new MapperWriteCheck(ex.Message, [], false);
        }

        _checks = BuildChecks(_lastCheck);
        OnPropertyChanged(nameof(LastCheck));
        OnPropertyChanged(nameof(Checks));
        OnPropertyChanged(nameof(CheckedItems));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(SaveText));
        RebuildRows();
    }

    private void RebuildRows()
    {
        var rows = new List<MapperDiffRow>();
        _headerRows.Clear();
        foreach (var item in Items.Where(i => i.IsChecked))
        {
            _headerRows[item] = rows.Count;
            rows.AddRange(MapperDiffRow.Section(HeaderOf(item.Item), item.Item.Diff, _sideBySide));
        }

        _rows = rows;
        OnPropertyChanged(nameof(Rows));
        UpdateSelectedRow(raise: false);
    }

    private void UpdateSelectedRow(bool raise = true)
    {
        _selectedRowIndex = _selected is not null && _headerRows.TryGetValue(_selected, out var at) ? at : -1;
        if (raise)
        {
            OnPropertyChanged(nameof(SelectedRowIndex));
        }
    }

    // 「@@ selectEmpList · 줄 24–41 @@」·「@@ insertEmp · 새 문장 @@」.
    private static string HeaderOf(MapperSaveItem item) => item.Kind == MapperSaveItemKind.Added
        ? Format("MyBatis.save.hunk.new", "@@ {0} · 새 문장 @@", item.CurrentId)
        : Format("MyBatis.save.hunk", "@@ {0} · 줄 {1}–{2} @@", item.Kind == MapperSaveItemKind.Deleted ? item.Key : item.CurrentId, item.FirstLine, item.LastLine);

    private List<SaveDiffCheckLine> BuildChecks(MapperWriteCheck check)
    {
        var lines = new List<SaveDiffCheckLine>
        {
            check.XmlError is null
                ? new SaveDiffCheckLine("ok", UiText.Get("MyBatis.save.check.xml", "XML 형식"))
                : new SaveDiffCheckLine("error", Format("MyBatis.save.check.xmlError", "XML 형식이 깨집니다: {0}", check.XmlError)),
            check.DuplicateIds.Count == 0
                ? new SaveDiffCheckLine("ok", UiText.Get("MyBatis.save.check.ids", "문장 id 겹침 없음"))
                : new SaveDiffCheckLine("error", Format("MyBatis.save.duplicateIds", "문장 id 가 겹칩니다: {0}", string.Join(", ", check.DuplicateIds))),
            check.ChangedOnDisk
                ? new SaveDiffCheckLine("warn", UiText.Get("MyBatis.save.check.diskChanged", "연 뒤로 파일이 밖에서 바뀜"))
                : new SaveDiffCheckLine("ok", UiText.Get("MyBatis.save.check.disk", "연 뒤로 파일이 밖에서 안 바뀜")),
            _request.CombosChecked is { } combos
                ? new SaveDiffCheckLine("ok", Format("MyBatis.save.check.combosOn", "조합 검사: {0}", combos))
                : new SaveDiffCheckLine("warn", UiText.Get("MyBatis.save.check.combosOff", "조합 검사 안 함 (선택)")),
        };
        return lines;
    }

    private static string Format(string key, string fallback, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, UiText.Get(key, fallback), args);
}
