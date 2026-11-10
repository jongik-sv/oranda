using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Metadata;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>「테이블 찾기」 목록의 원본 줄 하나(계정의 표·시노님과 테이블 명).</summary>
public sealed record TableFinderSource(string Owner, string Name, string ObjectType, string? Comment);

/// <summary>「테이블 찾기」 목록 한 줄. 체크는 창 전체의 「고른 표」 집합과 이어진다(검색을 바꿔도 남는다).</summary>
public sealed partial class TableFinderRow : ObservableObject
{
    private readonly TableFinderViewModel _owner;
    private bool _isChecked;

    internal TableFinderRow(TableFinderViewModel owner, TableFinderSource source, bool isAdded)
    {
        _owner = owner;
        Source = source;
        IsAdded = isAdded;
        _isChecked = owner.IsPicked(source);
    }

    public TableFinderSource Source { get; }

    public string Name => Source.Name;

    public string? Comment => Source.Comment;

    public bool IsSynonym => string.Equals(Source.ObjectType, "SYNONYM", StringComparison.Ordinal);

    public string TypeText => IsSynonym ? "시노님" : "테이블";

    /// <summary>이미 작업의 규칙에 있는 표(흐리게, 체크할 수 없다).</summary>
    public bool IsAdded { get; }

    public bool CanCheck => !IsAdded;

    /// <summary>이름 가운데 검색 값과 맞는 앞·맞는 곳·뒤(굵게·바탕색으로 보인다). 맞는 곳이 없으면 앞에 이름 전체.</summary>
    public string NameBefore { get; private set; } = string.Empty;

    public string NameMatch { get; private set; } = string.Empty;

    public string NameAfter { get; private set; } = string.Empty;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (IsAdded || !SetProperty(ref _isChecked, value))
            {
                return;
            }

            _owner.SetPicked(Source, value);
        }
    }

    /// <summary>화면의 체크만 바꾼다(고른 집합은 부른 쪽이 바꿨다).</summary>
    internal void SetCheckedSilently(bool value) => SetProperty(ref _isChecked, value, nameof(IsChecked));

    internal void Highlight(int start, int length)
    {
        if (start < 0 || length <= 0 || start + length > Name.Length)
        {
            (NameBefore, NameMatch, NameAfter) = (Name, string.Empty, string.Empty);
        }
        else
        {
            (NameBefore, NameMatch, NameAfter) = (Name[..start], Name.Substring(start, length), Name[(start + length)..]);
        }

        OnPropertyChanged(nameof(NameBefore));
        OnPropertyChanged(nameof(NameMatch));
        OnPropertyChanged(nameof(NameAfter));
    }
}

/// <summary>
/// 데이터 복사 「테이블 찾기」 팝업: 계정의 표(·시노님)를 검색 값으로 걸러 보이고, 여러 개를 체크해 한 번에 규칙으로 더한다.
/// 검색 칸이 비면 목록을 늘어놓지 않는다(표가 많을 수 있다). 이름·테이블 명(주석)에서 대소문자 없이 찾고, <c>*</c> 는 와일드카드(이름 전체와 맞춘다).
/// 고른 표는 검색·계정을 바꿔도 남는다.
/// </summary>
public sealed partial class TableFinderViewModel : ObservableObject
{
    /// <summary>한 번에 보이는 줄 수 상한(넘으면 검색을 더 좁히라고 알린다).</summary>
    public const int MaxVisible = 500;

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<TableFinderSource>>> _load;
    private readonly Func<string, string, bool> _isAdded;
    private readonly Dictionary<string, IReadOnlyList<TableFinderSource>> _byOwner = new(CopyOwners.OwnerComparer);
    private readonly Dictionary<string, TableFinderSource> _picked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _loadErrors = new(CopyOwners.OwnerComparer);
    private CancellationTokenSource? _loading;

    /// <param name="owners">계정 목록(콤보).</param>
    /// <param name="defaultOwner">처음 고른 계정(작업의 기본 원본 계정).</param>
    /// <param name="load">계정 하나의 표·시노님과 테이블 명을 읽는다.</param>
    /// <param name="isAdded">(계정, 표)가 이미 작업 규칙에 있는가.</param>
    public TableFinderViewModel(IReadOnlyList<string> owners, string defaultOwner,
        Func<string, CancellationToken, Task<IReadOnlyList<TableFinderSource>>> load, Func<string, string, bool> isAdded)
    {
        ArgumentNullException.ThrowIfNull(owners);
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _isAdded = isAdded ?? throw new ArgumentNullException(nameof(isAdded));
        Owners = owners.Contains(defaultOwner, CopyOwners.OwnerComparer) || string.IsNullOrEmpty(defaultOwner) ? owners : [defaultOwner, .. owners];
        _selectedOwner = Owners.FirstOrDefault(o => CopyOwners.SameOwner(o, defaultOwner)) ?? (Owners.Count > 0 ? Owners[0] : string.Empty);
    }

    public IReadOnlyList<string> Owners { get; }

    [ObservableProperty]
    private string _selectedOwner;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private bool _includeSynonyms = true;

    [ObservableProperty]
    private bool _isLoading;

    public ObservableCollection<TableFinderRow> Rows { get; } = [];

    /// <summary>위 요약 줄: 「APP 계정 · 1,284개 중 5개 일치 · 2개 고름」.</summary>
    [ObservableProperty]
    private string _summaryText = string.Empty;

    /// <summary>목록 대신 보일 안내(검색 칸이 빔·일치 없음·읽는 중). 없으면 빈 글.</summary>
    [ObservableProperty]
    private string _emptyText = string.Empty;

    public bool HasEmptyText => EmptyText.Length > 0;

    partial void OnEmptyTextChanged(string value) => OnPropertyChanged(nameof(HasEmptyText));

    /// <summary>모든 검색·계정에서 고른 표 수.</summary>
    public int PickedCount => _picked.Count;

    public bool CanAdd => _picked.Count > 0;

    public string AddButtonText => $"선택한 {PickedCount.ToString(CultureInfo.InvariantCulture)}개 추가";

    public string PickedText => $"검색을 바꿔도 고른 테이블은 유지됩니다 · 고른 테이블 {PickedCount.ToString(CultureInfo.InvariantCulture)}개";

    /// <summary>보이는 줄 가운데 체크할 수 있는 것을 모두 골랐으면 true, 하나도 안 골랐으면 false, 일부면 null.</summary>
    public bool? AllVisibleChecked
    {
        get
        {
            var candidates = Rows.Where(r => r.CanCheck).ToList();
            if (candidates.Count == 0)
            {
                return false;
            }

            var picked = candidates.Count(r => r.IsChecked);
            return picked == 0 ? false : picked == candidates.Count ? true : null;
        }
        set
        {
            var check = value ?? false;
            foreach (var row in Rows.Where(r => r.CanCheck))
            {
                SetPickedCore(row.Source, check);
                row.SetCheckedSilently(check);
            }

            PickedChanged();
        }
    }

    public bool CanCheckAll => Rows.Any(r => r.CanCheck);

    /// <summary>고른 표(계정·이름·유형). 시노님은 더할 때 실제 표로 푼다.</summary>
    public IReadOnlyList<DbObjectRef> PickedTables => [.. _picked.Values.Select(s => new DbObjectRef(s.Owner, s.Name, s.ObjectType))];

    /// <summary>창이 열릴 때: 고른 계정의 목록을 읽는다.</summary>
    public Task LoadAsync(CancellationToken ct = default) => LoadOwnerAsync(SelectedOwner, ct);

    partial void OnSelectedOwnerChanged(string value) => _ = LoadOwnerAsync(value, default);

    partial void OnFilterTextChanged(string value) => Refilter();

    partial void OnIncludeSynonymsChanged(bool value) => Refilter();

    private async Task LoadOwnerAsync(string owner, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(owner) || _byOwner.ContainsKey(owner))
        {
            Refilter();
            return;
        }

        _loading?.Cancel();
        var cts = _loading = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IsLoading = true;
        Refilter();
        try
        {
            var items = await _load(owner, cts.Token).ConfigureAwait(true);
            _byOwner[owner] = [.. items.OrderBy(i => i.Name, StringComparer.Ordinal)];
            _loadErrors.Remove(owner);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e)
        {
            _byOwner.Remove(owner);
            _loadErrors[owner] = $"{owner} 계정의 테이블을 읽지 못했습니다: {e.Message}";
            if (ReferenceEquals(cts, _loading))
            {
                IsLoading = false;
                Rows.Clear();
                EmptyText = $"{owner} 계정의 테이블을 읽지 못했습니다: {e.Message}";
            }

            return;
        }

        if (ReferenceEquals(cts, _loading))
        {
            IsLoading = false;
            Refilter();
        }
    }

    /// <summary>검색 값으로 지금 계정의 목록을 다시 거른다.</summary>
    private void Refilter()
    {
        Rows.Clear();
        _lastMatch = null;
        var owner = SelectedOwner;
        if (IsLoading || !_byOwner.TryGetValue(owner, out var all))
        {
            SummaryText = $"{owner} 계정";
            // 읽기에 실패한 계정은 검색 값을 바꿔도 이유를 계속 보인다(계정을 다시 고르면 다시 읽는다).
            EmptyText = IsLoading ? "테이블 목록을 읽는 중…" : _loadErrors.GetValueOrDefault(owner) ?? string.Empty;
            NotifyRows();
            return;
        }

        var pool = IncludeSynonyms ? all : [.. all.Where(s => !string.Equals(s.ObjectType, "SYNONYM", StringComparison.Ordinal))];
        var total = pool.Count.ToString("N0", CultureInfo.InvariantCulture);
        var query = FilterText.Trim();
        if (query.Length == 0)
        {
            SummaryText = $"{owner} 계정 · 전체 {total}개";
            EmptyText = $"이름을 입력하면 걸러 보입니다 — 전체 {total}개";
            NotifyRows();
            return;
        }

        var matcher = Matcher.Create(query);
        var hits = pool.Select(s => (Source: s, Match: matcher.Match(s))).Where(h => h.Match.Hit).ToList();
        foreach (var (source, match) in hits.Take(MaxVisible))
        {
            var row = new TableFinderRow(this, source, _isAdded(source.Owner, source.Name));
            row.Highlight(match.Start, match.Length);
            Rows.Add(row);
        }

        _lastMatch = (owner, pool.Count, hits.Count);
        SummaryText = MatchSummary(owner, pool.Count, hits.Count);
        EmptyText = hits.Count == 0 ? $"「{query}」 와 맞는 테이블이 없습니다" : string.Empty;
        NotifyRows();
    }

    private void NotifyRows()
    {
        OnPropertyChanged(nameof(AllVisibleChecked));
        OnPropertyChanged(nameof(CanCheckAll));
    }

    private static string Key(TableFinderSource s) => $"{s.Owner}\u0001{s.Name}\u0001{s.ObjectType}";

    internal bool IsPicked(TableFinderSource source) => _picked.ContainsKey(Key(source));

    internal void SetPicked(TableFinderSource source, bool picked)
    {
        SetPickedCore(source, picked);
        PickedChanged();
    }

    private void SetPickedCore(TableFinderSource source, bool picked)
    {
        if (picked)
        {
            _picked[Key(source)] = source;
        }
        else
        {
            _picked.Remove(Key(source));
        }
    }

    private void PickedChanged()
    {
        OnPropertyChanged(nameof(PickedCount));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(PickedText));
        OnPropertyChanged(nameof(AllVisibleChecked));
        if (_lastMatch is { } m)
        {
            SummaryText = MatchSummary(m.Owner, m.Total, m.Hits);
        }
    }

    // 마지막으로 거른 결과(계정, 전체 수, 일치 수). 체크가 바뀌면 요약 줄의 「고름」 수만 다시 쓴다.
    private (string Owner, int Total, int Hits)? _lastMatch;

    private string MatchSummary(string owner, int total, int hits) =>
        $"{owner} 계정 · {total.ToString("N0", CultureInfo.InvariantCulture)}개 중 {hits.ToString("N0", CultureInfo.InvariantCulture)}개 일치 · "
        + $"{Rows.Count(r => r.IsChecked).ToString(CultureInfo.InvariantCulture)}개 고름"
        + (hits > MaxVisible ? $" · 처음 {MaxVisible}개만 보입니다(검색을 좁혀 주세요)" : string.Empty);

    /// <summary>검색 값 하나의 맞춤 규칙. 와일드카드가 없으면 이름·테이블 명에 「포함」, 있으면 이름 전체와 맞춘다(테이블 명은 포함).</summary>
    internal sealed class Matcher
    {
        private readonly string _text;
        private readonly Regex? _pattern;

        private Matcher(string text, Regex? pattern)
        {
            _text = text;
            _pattern = pattern;
        }

        public static Matcher Create(string query)
        {
            var text = query.Trim();
            if (!text.Contains('*', StringComparison.Ordinal) && !text.Contains('%', StringComparison.Ordinal))
            {
                return new Matcher(text, null);
            }

            var sb = new StringBuilder("^");
            foreach (var ch in text)
            {
                sb.Append(ch is '*' or '%' ? ".*" : Regex.Escape(ch.ToString()));
            }

            var bare = text.Replace("*", string.Empty, StringComparison.Ordinal).Replace("%", string.Empty, StringComparison.Ordinal);
            return new Matcher(bare, new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)));
        }

        public (bool Hit, int Start, int Length) Match(TableFinderSource s)
        {
            if (_pattern is not null)
            {
                if (_pattern.IsMatch(s.Name))
                {
                    var at = _text.Length == 0 ? -1 : s.Name.IndexOf(_text, StringComparison.OrdinalIgnoreCase);
                    return (true, at, at < 0 ? 0 : _text.Length);
                }

                return (_text.Length > 0 && (s.Comment?.Contains(_text, StringComparison.OrdinalIgnoreCase) ?? false), -1, 0);
            }

            var index = s.Name.IndexOf(_text, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return (true, index, _text.Length);
            }

            return (s.Comment?.Contains(_text, StringComparison.OrdinalIgnoreCase) ?? false, -1, 0);
        }
    }
}
