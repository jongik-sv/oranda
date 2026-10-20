using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.ViewModels;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.Navigation;

/// <summary>DB 객체로 이동 목록의 한 줄.</summary>
public sealed class QuickOpenItem(DbObjectRef target)
{
    public DbObjectRef Target { get; } = target;

    /// <summary>「소유자.이름」.</summary>
    public string Display => $"{Target.Owner}.{Target.Name}";

    public string TypeText => Target.ObjectType;

    /// <summary>스키마 브라우저와 같은 유형 아이콘.</summary>
    public string IconKey => SchemaNode.IconForType(Target.ObjectType);
}

/// <summary>
/// DB 객체로 이동(<c>oranda.nav.quickOpenObject</c>, DataGrip 「Go to Database Object」·SQL Developer 「Find DB Object」) 창의 모델.
/// 객체 목록은 창을 열 때 한 번만 받는다(메타데이터 제공자가 접속별 캐시로 답하고, 캐시가 없으면 전체 목록을 한 번 받아 채운다).
/// 글자를 칠 때마다 DB 를 묻지 않고 받은 목록을 메모리에서 거른다. Enter = 열기, Alt+Enter = 스키마 브라우저에서 찾기.
/// </summary>
public sealed partial class QuickOpenObjectViewModel : ViewModelBase, IDisposable
{
    /// <summary>목록에 올리는 최대 줄 수.</summary>
    public const int MaxResults = 200;

    private static readonly string[] TypeOrder =
    [
        "TABLE", "VIEW", "MATERIALIZED VIEW", "PACKAGE", "PROCEDURE", "FUNCTION", "TYPE", "TRIGGER", "SEQUENCE", "SYNONYM", "INDEX",
    ];

    private readonly Func<CancellationToken, Task<IReadOnlyList<DbObjectRef>>> _load;
    private readonly Func<DbObjectRef, CancellationToken, Task> _open;
    private readonly Func<DbObjectRef, CancellationToken, Task<bool>> _reveal;
    private readonly CancellationTokenSource _life = new();
    private CancellationTokenSource? _debounce;
    private IReadOnlyList<DbObjectRef>? _all;
    private bool _pending;
    private int _generation;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private QuickOpenItem? _selected;

    [ObservableProperty]
    private string _statusText = "객체 목록을 읽는 중…";

    [ObservableProperty]
    private bool _isLoading;

    /// <param name="load">객체 전체 목록을 한 번 받는 방법.</param>
    /// <param name="open">객체를 여는 방법(표·뷰 = 객체 상세, PL/SQL = 소스 편집기).</param>
    /// <param name="reveal">스키마 브라우저에서 찾는 방법.</param>
    public QuickOpenObjectViewModel(
        Func<CancellationToken, Task<IReadOnlyList<DbObjectRef>>> load,
        Func<DbObjectRef, CancellationToken, Task> open,
        Func<DbObjectRef, CancellationToken, Task<bool>> reveal)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(reveal);
        _load = load;
        _open = open;
        _reveal = reveal;
    }

    public ObservableCollection<QuickOpenItem> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    /// <summary>접속 사용자. 그 스키마의 객체가 먼저 나온다.</summary>
    public string? CurrentUser { get; init; }

    /// <summary>입력 뒤 목록을 다시 거르기까지 기다리는 시간(ms). 0 이면 바로(시험).</summary>
    public int DebounceMilliseconds { get; set; } = 80;

    /// <summary>받은 객체 수(읽기 전이면 0).</summary>
    public int ObjectCount => _all?.Count ?? 0;

    /// <summary>열기·찾기 뒤 또는 취소 때 창을 닫아 달라는 요청.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>객체 목록을 받는다. 받는 사이 친 글자는 받은 뒤 바로 거른다.</summary>
    public async Task LoadAsync()
    {
        IsLoading = true;
        StatusText = "객체 목록을 읽는 중…";
        try
        {
            _all = await _load(_life.Token).ConfigureAwait(true);
            OnPropertyChanged(nameof(ObjectCount));
            IsLoading = false;
            Apply(Search(_all, Query, CurrentUser));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "객체 목록을 읽지 못했다: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnQueryChanged(string value) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
        if (_all is null)
        {
            return; // 목록을 받으면 그때 거른다.
        }

        if (DebounceMilliseconds <= 0)
        {
            Apply(Search(_all, Query, CurrentUser));
            return;
        }

        _pending = true;
        var cts = _debounce = new CancellationTokenSource();
        _ = RefreshLaterAsync(_all, Query, ++_generation, cts.Token);
    }

    private async Task RefreshLaterAsync(IReadOnlyList<DbObjectRef> all, string query, int generation, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DebounceMilliseconds, ct).ConfigureAwait(true);
            var user = CurrentUser;
            var found = await Task.Run(() => Search(all, query, user), ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested && generation == _generation)
            {
                Apply(found);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>기다리는 거르기가 있으면 지금 한다(Enter 를 입력 바로 뒤에 눌렀을 때 옛 목록을 열지 않게).</summary>
    private void Flush()
    {
        if (_pending && _all is not null)
        {
            _debounce?.Cancel();
            _generation++;
            Apply(Search(_all, Query, CurrentUser));
        }
    }

    private void Apply(IReadOnlyList<DbObjectRef> found)
    {
        _pending = false;
        Results.Clear();
        foreach (var r in found)
        {
            Results.Add(new QuickOpenItem(r));
        }

        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        StatusText = string.IsNullOrWhiteSpace(Query) ? $"객체 {ObjectCount}개 — 이름 일부를 입력한다(소유자.이름, * 와일드카드)"
            : found.Count == 0 ? "맞는 객체가 없다"
            : found.Count >= MaxResults ? $"처음 {MaxResults}개" : $"{found.Count}개";
    }

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(index + delta, 0, Results.Count - 1)];
    }

    /// <summary>선택한 객체를 연다(표·뷰 = 객체 상세, PL/SQL = 소스). 선택이 없으면 false(창은 그대로).</summary>
    public Task<bool> OpenSelectedAsync() => RunSelectedAsync(reveal: false);

    /// <summary>선택한 객체를 스키마 브라우저에서 찾는다.</summary>
    public Task<bool> RevealSelectedAsync() => RunSelectedAsync(reveal: true);

    private async Task<bool> RunSelectedAsync(bool reveal)
    {
        Flush();
        if (Selected is not { } item)
        {
            return false;
        }

        // 여는 쪽이 문서 탭·도구 창으로 포커스를 옮기므로 창을 먼저 닫는다. 창을 닫아도 여는 일은 끝까지 한다.
        CloseRequested?.Invoke(this, EventArgs.Empty);
        try
        {
            if (reveal)
            {
                return await _reveal(item.Target, CancellationToken.None).ConfigureAwait(true);
            }

            await _open(item.Target, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("객체 '{0}.{1}' 열기 실패: {2}", item.Target.Owner, item.Target.Name, ex);
            return false;
        }
    }

    public void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _life.Cancel();
        _life.Dispose();
        _debounce?.Cancel();
        _debounce?.Dispose();
        _debounce = null;
    }

    // ---- 거르기(순수 함수) ----------------------------------------------------------------

    /// <summary>
    /// 입력에 맞는 객체를 고른다. 이름이 같으면 → 앞부분이 같으면 → 들어 있으면 → 밑줄로 나뉜 단어 머리글자가 차례로 맞으면
    /// (<c>ed</c> → <c>EMP_DEPT</c>) 순서. 같은 점수면 접속 사용자 스키마 → 짧은 이름 → 유형(테이블·뷰·패키지 …) 순.
    /// <c>소유자.이름</c> 으로 스키마를 좁힐 수 있고 <c>*</c>·<c>%</c>·<c>?</c> 는 와일드카드다. 대소문자는 가리지 않는다.
    /// 명세가 함께 있는 본문(PACKAGE BODY·TYPE BODY)은 명세 한 줄로 보인다.
    /// </summary>
    public static IReadOnlyList<DbObjectRef> Search(IReadOnlyList<DbObjectRef> all, string? query, string? currentUser, int limit = MaxResults)
    {
        ArgumentNullException.ThrowIfNull(all);
        var (owner, name) = ParseQuery(query);
        if (owner is null && name.Length == 0)
        {
            return [];
        }

        var wildcard = ToWildcard(name);
        var compact = name.Replace("_", string.Empty, StringComparison.Ordinal);
        var specs = new HashSet<(string, string, string)>();
        foreach (var o in all)
        {
            if (o.ObjectType is "PACKAGE" or "TYPE")
            {
                specs.Add((o.Owner, o.Name, o.ObjectType));
            }
        }

        var hits = new List<(DbObjectRef Ref, int Score)>();
        foreach (var o in all)
        {
            if (o.ObjectType is "PACKAGE BODY" or "TYPE BODY" && specs.Contains((o.Owner, o.Name, o.ObjectType[..^5])))
            {
                continue;
            }

            if (owner is not null && !o.Owner.StartsWith(owner, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var score = name.Length == 0 ? 1 : NameScore(o.Name, name, compact, wildcard);
            if (score >= 0)
            {
                hits.Add((o, score));
            }
        }

        return
        [
            .. hits
                .OrderBy(h => h.Score)
                .ThenBy(h => string.Equals(h.Ref.Owner, currentUser, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(h => h.Ref.Name.Length)
                .ThenBy(h => TypeRank(h.Ref.ObjectType))
                .ThenBy(h => h.Ref.Name, StringComparer.Ordinal)
                .ThenBy(h => h.Ref.Owner, StringComparer.Ordinal)
                .Take(limit)
                .Select(h => h.Ref),
        ];
    }

    /// <summary><c>소유자.이름</c> 을 나눈다. 따옴표는 지운다. 점이 없으면 소유자는 null.</summary>
    internal static (string? Owner, string Name) ParseQuery(string? query)
    {
        var q = (query ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal).Trim();
        var dot = q.LastIndexOf('.');
        return dot switch
        {
            < 0 => (null, q),
            0 => (null, q[1..].Trim()),
            _ => (q[..dot].Trim(), q[(dot + 1)..].Trim()),
        };
    }

    private static Regex? ToWildcard(string name)
    {
        if (name.IndexOfAny(['*', '%', '?']) < 0)
        {
            return null;
        }

        var sb = new StringBuilder("^");
        foreach (var ch in name)
        {
            sb.Append(ch switch
            {
                '*' or '%' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }

        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    }

    private static int NameScore(string candidate, string name, string compact, Regex? wildcard)
    {
        const StringComparison c = StringComparison.OrdinalIgnoreCase;
        if (wildcard is not null)
        {
            try
            {
                return wildcard.IsMatch(candidate) ? 2 : -1;
            }
            catch (RegexMatchTimeoutException)
            {
                return -1;
            }
        }

        if (candidate.Equals(name, c))
        {
            return 0;
        }

        if (candidate.StartsWith(name, c))
        {
            return 1;
        }

        if (candidate.Contains(name, c))
        {
            return 2;
        }

        return compact.Length >= 2 && MatchesWordHeads(candidate, compact) ? 3 : -1;
    }

    /// <summary>입력 글자를 이름의 단어(밑줄·<c>$</c>·<c>#</c> 로 나뉜) 머리부터 차례로 맞출 수 있는지(<c>empde</c>·<c>ed</c> → <c>EMP_DEPT</c>).</summary>
    internal static bool MatchesWordHeads(string candidate, string query)
    {
        var words = candidate.Split(['_', '$', '#'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2 && Match(words, 0, query, 0);

        static bool Match(string[] words, int w, string q, int qi)
        {
            if (qi == q.Length)
            {
                return true;
            }

            for (var j = w; j < words.Length; j++)
            {
                var word = words[j];
                var n = 0;
                while (n < word.Length && qi + n < q.Length && char.ToUpperInvariant(word[n]) == char.ToUpperInvariant(q[qi + n]))
                {
                    n++;
                }

                for (var take = n; take >= 1; take--)
                {
                    if (Match(words, j + 1, q, qi + take))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    private static int TypeRank(string type)
    {
        var i = Array.IndexOf(TypeOrder, type);
        return i < 0 ? TypeOrder.Length : i;
    }
}
