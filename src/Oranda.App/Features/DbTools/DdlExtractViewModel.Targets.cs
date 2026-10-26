using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Localization;
using Oranda.App.Features.SchemaBrowser;
using Oranda.Core.Metadata;

namespace Oranda.App.Features.DbTools;

/// <summary>대상 목록이 지금 어떤 화면인지(오른쪽 칸 「대상」 탭).</summary>
public enum DdlTargetState
{
    /// <summary>접속이 없다.</summary>
    NoConnection,

    /// <summary>스키마 칸이 비었다.</summary>
    NeedOwner,

    /// <summary>유형을 하나도 안 골랐다.</summary>
    NeedType,

    /// <summary>처음 불러오는 중(보이는 줄이 아직 없다).</summary>
    Loading,

    /// <summary>목록이 있다.</summary>
    Ready,

    /// <summary>조건에 맞는 객체가 없다.</summary>
    Empty,

    /// <summary>조회가 실패했다.</summary>
    Error,

    /// <summary>검색 제공자가 없어 미리 볼 수 없다(추출은 조건 전체로 한다).</summary>
    Unavailable,
}

/// <summary>대상 표의 한 줄. 체크하면 추출 대상에 들어간다.</summary>
public sealed partial class DdlTargetItem : ObservableObject
{
    internal DdlTargetItem(ObjectSearchRow row)
    {
        Row = row;
        _isChecked = true;
    }

    public ObjectSearchRow Row { get; }

    public DbObjectRef Ref => Row.Ref;

    public string ObjectType => Row.Ref.ObjectType;

    public string Name => Row.Ref.Name;

    public string Status => Row.Status;

    /// <summary>VALID 가 아니면(INVALID·상태를 모르는 값) 강조한다.</summary>
    public bool IsInvalid => !string.Equals(Row.Status, "VALID", StringComparison.Ordinal) && Row.Status.Length > 0;

    /// <summary>마지막 DDL 시각(<c>yyyy-MM-dd HH:mm</c>). 모르면 빈 글자.</summary>
    public string LastDdl => Row.LastDdlTime?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? string.Empty;

    [ObservableProperty]
    private bool _isChecked;
}

public sealed partial class DdlExtractViewModel
{
    /// <summary>한 번에 보여 주는 대상의 최대 개수. 넘으면 앞쪽만 보이고 필터를 좁히라고 안내한다.</summary>
    public const int MaxTargets = 5000;

    private CancellationTokenSource? _refreshCts;
    private bool _bulkChecking;
    private int _checkedCount;

    /// <summary>시험용: 입력이 멈춘 뒤 대상을 다시 조회하기까지 기다리는 시간(ms).</summary>
    public int TargetDebounceMilliseconds { get; set; } = 300;

    /// <summary>가장 최근에 시작한 대상 조회(시험이 끝을 기다린다). 취소된 조회는 조용히 끝난다.</summary>
    internal Task PendingRefresh { get; private set; } = Task.CompletedTask;

    /// <summary>시험용: 대상 조회 제공자를 직접 넣는다. 없으면 서비스에서 처음 쓸 때 꺼낸다.</summary>
    internal ISchemaAdminProvider? Admin { get; set; }

    /// <summary>화면에 보이는 대상. 새 조회가 끝나면 목록을 통째로 바꿔 끼운다(줄 단위로 넣고 빼지 않아 화면이 튀지 않는다).</summary>
    [ObservableProperty]
    private IReadOnlyList<DdlTargetItem> _targets = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTargetPlaceholder))]
    [NotifyPropertyChangedFor(nameof(ShowTargetRows))]
    [NotifyPropertyChangedFor(nameof(TargetMessage))]
    [NotifyPropertyChangedFor(nameof(TargetHint))]
    private DdlTargetState _targetState = DdlTargetState.NoConnection;

    /// <summary>true 면 앞 목록을 그대로 두고 새로 조회하는 중이다.</summary>
    [ObservableProperty]
    private bool _isLoadingTargets;

    /// <summary>조건에 맞는 객체가 <see cref="MaxTargets"/> 를 넘어 일부만 보인다.</summary>
    [ObservableProperty]
    private bool _targetsTruncated;

    [ObservableProperty]
    private string _targetError = string.Empty;

    /// <summary>오른쪽 칸의 탭(0 = 대상, 1 = 추출 결과). 추출을 시작하면 1 로 넘어간다.</summary>
    [ObservableProperty]
    private int _selectedTab;

    public bool ShowTargetRows => TargetState == DdlTargetState.Ready;

    public bool ShowTargetPlaceholder => TargetState != DdlTargetState.Ready;

    public bool HasResults => Results.Count > 0;

    public int TargetCount => Targets.Count;

    public int CheckedCount => _checkedCount;

    /// <summary>탭 머리글(「대상 (12)」, 일부만 보이면 「대상 (5000+)」).</summary>
    public string TargetsHeader => TargetState == DdlTargetState.Ready
        ? UiText.Format("DdlExtract.tab.targetsCount", "대상 ({0})", TargetsTruncated ? MaxTargets.ToString(CultureInfo.InvariantCulture) + "+" : Targets.Count.ToString(CultureInfo.InvariantCulture))
        : UiText.Get("DdlExtract.tab.targets", "대상");

    public string ResultsHeader => Results.Count == 0
        ? UiText.Get("DdlExtract.tab.results", "추출 결과")
        : UiText.Format("DdlExtract.tab.resultsCount", "추출 결과 ({0})", Results.Count);

    /// <summary>「대상 n개 중 m개 선택」. 목록이 없으면 빈 글자.</summary>
    public string TargetSummary => TargetState != DdlTargetState.Ready
        ? string.Empty
        : TargetsTruncated
            ? UiText.Format("DdlExtract.summaryMore", "대상 {0}개 중 {1}개 선택 (더 있음)", Targets.Count, CheckedCount)
            : UiText.Format("DdlExtract.summary", "대상 {0}개 중 {1}개 선택", Targets.Count, CheckedCount);

    /// <summary>표 머리글 체크: 모두 선택 = true, 하나도 없음 = false, 일부 = null.</summary>
    public bool? AllChecked
    {
        get => Targets.Count == 0 ? false : _checkedCount == Targets.Count ? true : _checkedCount == 0 ? false : null;
        set
        {
            // 일부만 골라 둔 상태(null)에서 누르면 비우지 않고 모두 선택한다.
            var target = value ?? true;
            if (value == false && AllChecked is null)
            {
                target = true;
            }

            SetAllChecked(target);
        }
    }

    /// <summary>목록 자리에 보이는 한 줄 설명(목록이 없을 때).</summary>
    public string TargetMessage => TargetState switch
    {
        DdlTargetState.NoConnection => UiText.Get("DdlExtract.noConnection", "접속이 없습니다"),
        DdlTargetState.NeedOwner => UiText.Get("DdlExtract.needOwner", "스키마를 입력하세요"),
        DdlTargetState.NeedType => UiText.Get("DdlExtract.needType", "유형을 하나 이상 고르세요"),
        DdlTargetState.Loading => UiText.Get("DdlExtract.loading", "대상을 불러오는 중…"),
        DdlTargetState.Empty => UiText.Get("DdlExtract.empty", "조건에 맞는 객체가 없습니다"),
        DdlTargetState.Error => UiText.Get("DdlExtract.error", "대상을 불러오지 못했습니다"),
        DdlTargetState.Unavailable => UiText.Get("DdlExtract.unavailable", "대상을 미리 볼 수 없습니다"),
        _ => string.Empty,
    };

    /// <summary>다음에 할 일(목록이 없을 때). 오류면 서버 메시지를 덧붙인다.</summary>
    public string TargetHint => TargetState switch
    {
        DdlTargetState.NoConnection => UiText.Get("DdlExtract.noConnectionHint", "접속한 뒤 이 창을 열면 대상 객체가 여기에 보입니다."),
        DdlTargetState.NeedOwner => UiText.Get("DdlExtract.needOwnerHint", "왼쪽 「스키마」 칸에 소유자를 쓰면 대상 객체가 여기에 보입니다."),
        DdlTargetState.NeedType => UiText.Get("DdlExtract.needTypeHint", "왼쪽에서 추출할 유형을 하나 이상 체크하세요."),
        DdlTargetState.Loading => string.Empty,
        DdlTargetState.Empty => InvalidOnly
            ? UiText.Get("DdlExtract.emptyInvalidHint", "「INVALID 객체만」을 끄거나 이름 필터를 비워 보세요.")
            : UiText.Get("DdlExtract.emptyHint", "이름 필터를 비우거나 유형을 늘려 보세요."),
        DdlTargetState.Error => TargetError + "\n" + UiText.Get("DdlExtract.errorHint", "새로 고침(F5)으로 다시 시도하세요."),
        DdlTargetState.Unavailable => UiText.Get("DdlExtract.unavailableHint", "「추출」은 왼쪽 조건에 맞는 객체 전부를 추출합니다."),
        _ => string.Empty,
    };

    partial void OnOwnerChanged(string value) => ScheduleRefresh();

    partial void OnNameTextChanged(string value) => ScheduleRefresh();

    partial void OnInvalidOnlyChanged(bool value) => ScheduleRefresh();

    partial void OnTargetsChanged(IReadOnlyList<DdlTargetItem> value) => RaiseTargetCounters();

    partial void OnTargetsTruncatedChanged(bool value) => RaiseTargetCounters();

    partial void OnTargetStateChanged(DdlTargetState value)
    {
        RaiseTargetCounters();
        ShowTargetStateInStatus();
    }

    partial void OnTargetErrorChanged(string value) => OnPropertyChanged(nameof(TargetHint));

    private void HookTypeChoices()
    {
        foreach (var choice in TypeChoices)
        {
            choice.PropertyChanged += OnTypeChoiceChanged;
        }

        Results.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ResultsHeader));
            OnPropertyChanged(nameof(HasResults));
        };
    }

    private void OnTypeChoiceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DdlTypeChoice.IsSelected))
        {
            ScheduleRefresh();
        }
    }

    private void RaiseTargetCounters()
    {
        OnPropertyChanged(nameof(TargetCount));
        OnPropertyChanged(nameof(CheckedCount));
        OnPropertyChanged(nameof(AllChecked));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(TargetsHeader));
    }

    private void OnTargetCheckedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkChecking || e.PropertyName != nameof(DdlTargetItem.IsChecked) || sender is not DdlTargetItem item)
        {
            return;
        }

        _checkedCount += item.IsChecked ? 1 : -1;
        RaiseTargetCounters();
        ShowTargetSummaryInStatus();
    }

    private void SetAllChecked(bool value)
    {
        _bulkChecking = true;
        try
        {
            foreach (var t in Targets)
            {
                t.IsChecked = value;
            }
        }
        finally
        {
            _bulkChecking = false;
        }

        _checkedCount = value ? Targets.Count : 0;
        RaiseTargetCounters();
        ShowTargetSummaryInStatus();
    }

    /// <summary>목록이 없는 상태(접속 없음·빈 결과·오류 …)가 되면 아래 상태 줄이 옛 요약을 들고 있지 않게 안내 글로 바꾼다.</summary>
    private void ShowTargetStateInStatus()
    {
        if (!IsBusy && TargetState is DdlTargetState.NoConnection or DdlTargetState.NeedOwner or DdlTargetState.NeedType
            or DdlTargetState.Empty or DdlTargetState.Error)
        {
            StatusText = TargetState == DdlTargetState.Error ? TargetMessage + " — " + TargetError : TargetMessage;
        }
    }

    /// <summary>추출 중이 아닐 때 아래 상태 줄에 「대상 n개 중 m개 선택」을 보인다.</summary>
    private void ShowTargetSummaryInStatus()
    {
        if (!IsBusy && TargetState == DdlTargetState.Ready)
        {
            StatusText = TargetSummary;
        }
    }

    /// <summary>체크한 대상(보이는 순서 그대로).</summary>
    public IReadOnlyList<DbObjectRef> CheckedObjects => [.. Targets.Where(t => t.IsChecked).Select(t => t.Ref)];

    /// <summary>지금 조회하지 않고 입력이 멈추기를 기다렸다가 대상을 다시 조회한다.</summary>
    private void ScheduleRefresh() => StartRefresh(TargetDebounceMilliseconds);

    /// <summary>기다리지 않고 지금 대상을 다시 조회한다(F5·새로 고침). 끝날 때까지 기다리는 작업을 돌려준다.</summary>
    public Task RefreshTargetsAsync()
    {
        StartRefresh(0);
        return PendingRefresh;
    }

    private void StartRefresh(int delayMs)
    {
        _refreshCts?.Cancel();

        // 조회할 수 없는 입력이면 앞 조회를 버리고 안내 화면으로.
        if (Session is not { } session)
        {
            ShowNoTargets(DdlTargetState.NoConnection);
            return;
        }

        if (string.IsNullOrWhiteSpace(Owner))
        {
            ShowNoTargets(DdlTargetState.NeedOwner);
            return;
        }

        var types = TypeChoices.Where(t => t.IsSelected).Select(t => t.Name).ToArray();
        if (types.Length == 0)
        {
            ShowNoTargets(DdlTargetState.NeedType);
            return;
        }

        var admin = ResolveAdmin();
        if (admin is null)
        {
            ShowNoTargets(DdlTargetState.Unavailable);
            return;
        }

        var query = new ObjectSearchQuery
        {
            Owner = Owner.Trim().ToUpperInvariant(),
            ObjectTypes = types,
            NamePattern = SchemaFilter.ToLikePattern(NameText),
            InvalidOnly = InvalidOnly,
            Limit = MaxTargets + 1,
        };
        var cts = _refreshCts = new CancellationTokenSource();
        IsLoadingTargets = true;
        if (Targets.Count == 0)
        {
            TargetState = DdlTargetState.Loading;
        }

        PendingRefresh = RefreshAsync(admin, session, query, delayMs, cts);
    }

    private void ShowNoTargets(DdlTargetState state)
    {
        _refreshCts = null;
        PendingRefresh = Task.CompletedTask;
        IsLoadingTargets = false;
        TargetsTruncated = false;
        ReplaceTargets([]);
        TargetState = state;
    }

    private async Task RefreshAsync(ISchemaAdminProvider admin, Oranda.Core.Sessions.ISession session, ObjectSearchQuery query, int delayMs, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, ct).ConfigureAwait(true);
            }

            // UI 스레드 밖에서 조회하고 줄 만들기까지 마친 뒤, 결과를 한 번에 바꿔 끼운다.
            var items = await Task.Run(async () =>
            {
                var rows = await admin.SearchObjectsAsync(session, query, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return rows
                    .OrderBy(r => TypeOrder(r.Ref.ObjectType))
                    .ThenBy(r => r.Ref.ObjectType, StringComparer.Ordinal)
                    .ThenBy(r => r.Ref.Name, StringComparer.Ordinal)
                    .Take(MaxTargets + 1)
                    .Select(r => new DdlTargetItem(r))
                    .ToList();
            }, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            var truncated = items.Count > MaxTargets;
            if (truncated)
            {
                items.RemoveRange(MaxTargets, items.Count - MaxTargets);
            }

            TargetsTruncated = truncated;
            ReplaceTargets(items);
            TargetState = items.Count == 0 ? DdlTargetState.Empty : DdlTargetState.Ready;
            IsLoadingTargets = false;
            ShowTargetSummaryInStatus();
        }
        catch (OperationCanceledException)
        {
            // 새 입력이 오면 앞 조회는 버린다.
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            TargetsTruncated = false;
            ReplaceTargets([]);
            TargetError = ex.Message.ReplaceLineEndings(" ");
            TargetState = DdlTargetState.Error;
            IsLoadingTargets = false;
        }
    }

    private void ReplaceTargets(List<DdlTargetItem> items)
    {
        foreach (var old in Targets)
        {
            old.PropertyChanged -= OnTargetCheckedChanged;
        }

        foreach (var item in items)
        {
            item.PropertyChanged += OnTargetCheckedChanged;
        }

        _checkedCount = items.Count;
        Targets = items;
        RaiseTargetCounters();
    }

    private static readonly Dictionary<string, int> TypeRank = DefaultTypes.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i, StringComparer.Ordinal);

    private static int TypeOrder(string type) => TypeRank.GetValueOrDefault(type, int.MaxValue);

    private ISchemaAdminProvider? ResolveAdmin() => Admin ??= (ISchemaAdminProvider?)_services?.GetService(typeof(ISchemaAdminProvider));
}
