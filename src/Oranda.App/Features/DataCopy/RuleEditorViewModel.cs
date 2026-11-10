using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;

namespace Oranda.App.Features.DataCopy;

/// <summary>접속 고르기 목록의 한 줄. 운영 접속은 대상으로는 비활성이고 이유를 툴팁으로 알린다(설계 10장).</summary>
public sealed record ConnectionOption(ConnectionProfile Profile, bool IsEnabled, string? Tooltip)
{
    public string Display => $"{Profile.Name} ({Profile.User})";
}

/// <summary>상세 칸 「대상 계정」 콤보의 한 줄. 고르면 칸 글자가 <see cref="Owner"/> 가 된다(<see cref="ToString"/>).</summary>
public sealed record TargetOwnerOption(string Owner, string Hint)
{
    /// <summary>그 규칙의 기본 대상(<see cref="RuleEditorViewModel.DefaultTargetFor(TableRuleViewModel)"/>)인지. 고르면 다시 기본 계정을 따른다.</summary>
    public bool IsDefault { get; init; }

    public override string ToString() => Owner;
}

/// <summary>기간 종류 고르기 목록의 한 줄.</summary>
public sealed record PeriodKindOption(PeriodKind Kind, string Display);

/// <summary>직접 지정 기간의 끝 날짜 셈. <paramref name="Inclusive"/> 면 끝 날짜까지 들어간다.</summary>
public sealed record PeriodEndOption(bool Inclusive, string Display);

/// <summary>
/// 규칙 편집 단계(1단계): 원본·대상 접속, 소유자, 기간, 읽기 일관성, 옵션, 테이블별 규칙. 기간은 상대값으로 저장하고
/// 작업을 열 때 오늘 날짜로 다시 계산한 날짜를 항상 화면에 보인다(설계 6.4).
/// </summary>
public sealed partial class RuleEditorViewModel : ViewModelBase
{
    private readonly DataCopyServices _services;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnsupportedPairNotice), nameof(HasUnsupportedPairNotice))]
    private ConnectionOption? _source;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRecreate), nameof(AvailableActions), nameof(TargetReadOnlyNotice), nameof(HasTargetReadOnlyNotice),
        nameof(UnsupportedPairNotice), nameof(HasUnsupportedPairNotice))]
    private ConnectionOption? _target;

    [ObservableProperty]
    private string _sourceOwner = string.Empty;

    // 대상을 고르기 전에 표를 추가해 원본 표 소유자로 임시로 채운 대상 소유자. 대상을 고르면 그 사용자로 바꾼다.
    private string? _provisionalTargetOwner;

    partial void OnTargetChanged(ConnectionOption? value)
    {
        if (_provisionalTargetOwner is { } provisional && value is { Profile.User: { Length: > 0 } user })
        {
            if (string.Equals(TargetOwner, provisional, StringComparison.Ordinal))
            {
                TargetOwner = user.ToUpperInvariant();
            }

            _provisionalTargetOwner = null;
        }

        foreach (var rule in Rules)
        {
            rule.SessionUser = TargetSessionUser;
            rule.CreateBlocked = false; // 다른 대상이면 점검에서 알게 된 「이미 있음·만들 수 없음」은 더 맞지 않는다
            rule.TargetExists = false;
        }

        OnPropertyChanged(nameof(TargetSessionUser));
    }

    // ---- 전체 기본값(규칙·대상 처리·없으면 만들기): 테이블을 더할 때 채우고, 바꾸면 따로 바꾸지 않은 테이블에만 반영한다 ----

    /// <summary>전체 기본값 「규칙」 선택지(자동 = 기간이 있고 기간 열이 있는 테이블은 기간, 아니면 모든 행).</summary>
    public IReadOnlyList<ChoiceItem<DefaultKindChoice>> DefaultKindOptions { get; } =
    [
        new(DefaultKindChoice.Auto, "자동"),
        new(DefaultKindChoice.All, "모든 행"),
        new(DefaultKindChoice.Period, "기간"),
        new(DefaultKindChoice.Exclude, "제외"),
    ];

    /// <summary>전체 기본값 「대상 처리」 선택지(자동 = 규칙 종류별 기본값). 다시 만들기는 테이블마다 정한다.</summary>
    public IReadOnlyList<ChoiceItem<DefaultActionChoice>> DefaultActionOptions { get; } =
    [
        new(DefaultActionChoice.Auto, "자동"),
        new(DefaultActionChoice.DeleteAllInsert, "비우고 넣기"),
        new(DefaultActionChoice.DeleteRangeInsert, "같은 범위 지우고 넣기"),
        new(DefaultActionChoice.AppendOnly, "추가만"),
        new(DefaultActionChoice.MergeByPk, "PK 기준 MERGE"),
    ];

    [ObservableProperty]
    private DefaultKindChoice _defaultKind = DefaultKindChoice.Auto;

    [ObservableProperty]
    private DefaultActionChoice _defaultAction = DefaultActionChoice.Auto;

    /// <summary>전체 기본값 「대상에 테이블이 없으면 만들기」. 모든 환경에서 켠 채 시작한다. 이미 있는 테이블·만들 수 없는 테이블에는 켜지 않는다.</summary>
    [ObservableProperty]
    private bool _defaultCreateIfMissing = true;

    private bool _applyingTableDefaults;

    private static readonly TableDefaults InitialTableDefaults = new(null, null, true);

    // 불러온 작업에 전체 기본값이 없었다(옛 파일).
    private bool _legacyTableDefaults;

    partial void OnDefaultKindChanged(DefaultKindChoice value) => ApplyDefaultsToFollowers();

    partial void OnDefaultActionChanged(DefaultActionChoice value) => ApplyDefaultsToFollowers();

    partial void OnDefaultCreateIfMissingChanged(bool value) => ApplyDefaultsToFollowers();

    /// <summary>저장할 전체 기본값.</summary>
    public TableDefaults CurrentTableDefaults => new(
        DefaultKind switch
        {
            DefaultKindChoice.All => RuleKind.All,
            DefaultKindChoice.Period => RuleKind.Period,
            DefaultKindChoice.Exclude => RuleKind.Exclude,
            _ => null,
        },
        DefaultAction switch
        {
            DefaultActionChoice.DeleteAllInsert => TargetAction.DeleteAllInsert,
            DefaultActionChoice.DeleteRangeInsert => TargetAction.DeleteRangeInsert,
            DefaultActionChoice.AppendOnly => TargetAction.AppendOnly,
            DefaultActionChoice.MergeByPk => TargetAction.MergeByPk,
            _ => null,
        },
        DefaultCreateIfMissing);

    private void SetTableDefaults(TableDefaults defaults)
    {
        _applyingTableDefaults = true; // 불러오기: 규칙에 되돌려 적용하지 않는다(저장된 값 그대로)
        try
        {
            DefaultKind = defaults.Kind switch
            {
                RuleKind.All => DefaultKindChoice.All,
                RuleKind.Period => DefaultKindChoice.Period,
                RuleKind.Exclude => DefaultKindChoice.Exclude,
                _ => DefaultKindChoice.Auto,
            };
            DefaultAction = defaults.Action switch
            {
                TargetAction.DeleteAllInsert => DefaultActionChoice.DeleteAllInsert,
                TargetAction.DeleteRangeInsert => DefaultActionChoice.DeleteRangeInsert,
                TargetAction.AppendOnly => DefaultActionChoice.AppendOnly,
                TargetAction.MergeByPk => DefaultActionChoice.MergeByPk,
                _ => DefaultActionChoice.Auto,
            };
            DefaultCreateIfMissing = defaults.CreateIfMissing;
        }
        finally
        {
            _applyingTableDefaults = false;
        }
    }

    private void ApplyDefaultsToFollowers()
    {
        if (_applyingTableDefaults)
        {
            return;
        }

        foreach (var rule in Rules)
        {
            ApplyDefaultsTo(rule);
        }
    }

    /// <summary>이 테이블에 전체 기본값을 적용한다(따로 바꾼 항목은 건드리지 않는다).</summary>
    private void ApplyDefaultsTo(TableRuleViewModel rule)
    {
        var d = CurrentTableDefaults;
        var kind = d.Kind switch
        {
            null => PeriodChoice.Kind != PeriodKind.None && rule.PeriodColumnOptions.Count > 0 ? RuleKind.Period : RuleKind.All,
            RuleKind.Period => rule.PeriodColumnOptions.Count > 0 ? RuleKind.Period : RuleKind.All, // 기간 열이 없는 테이블은 모든 행
            var k => k,
        };
        rule.ApplyDefaults(kind, d.Action, d.CreateIfMissing);
    }

    /// <summary>「전체 값으로」: 이 테이블의 따로 바꾼 항목을 모두 풀고 전체 기본값으로 되돌린다.</summary>
    public void ResetRuleToDefaults(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.ClearOverrides();
        ApplyDefaultsTo(rule);
    }

    /// <summary>
    /// 사전 점검 결과로 테이블마다 「대상에 이미 있음」·「만들 수 없음」을 적어 둔다. 이미 정한 값은 바꾸지 않고(점검 결과가 흔들리지 않게),
    /// 다음에 전체 값을 적용할 때부터 그 테이블의 「없으면 만들기」를 켜지 않는다.
    /// </summary>
    public void ApplyPrecheckHints(CopyJob job, PrecheckReport report)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(report);
        foreach (var rule in job.Tables.Where(t => t.Kind != RuleKind.Exclude))
        {
            var key = CopyOwners.Key(job, rule);
            var codes = report.Items.Where(i => string.Equals(i.Table, key, StringComparison.Ordinal)).Select(i => i.Code).ToHashSet(StringComparer.Ordinal);
            if (Rules.FirstOrDefault(r => ReferenceEquals(r.LastBuilt, rule)) is not { } vm)
            {
                continue;
            }

            vm.CreateBlocked = codes.Contains(CopyCheckCodes.CreateBlocked) || codes.Contains(CopyCheckCodes.CreateNoPrivilege);
            vm.TargetExists = !vm.CreateBlocked && !codes.Contains("TargetMissing") && !codes.Contains(CopyCheckCodes.CreateTable);
        }
    }

    /// <summary>
    /// 작업의 기본 대상 계정. 표 이름만 적어 더한 표(기본 원본 계정의 표)의 대상 계정 기본값이다.
    /// 바꾸면 옛 기본과 같던 규칙만 따라 바뀐다(<see cref="RebaseRules"/>).
    /// </summary>
    [ObservableProperty]
    private string _targetOwner = string.Empty;

    // 작업을 올리는 동안에는 기본 계정이 바뀌어도 규칙을 따라 바꾸지 않는다(규칙은 저장된 계정 그대로).
    private bool _loading;

    // 사전에서 풀린 계정 이름을 기본 계정 칸에 넣는 중(정리하지 않는다).
    private bool _dictionaryName;

    private void SetDictionaryOwner(Action set)
    {
        _dictionaryName = true;
        try
        {
            set();
        }
        finally
        {
            _dictionaryName = false;
        }
    }

    // 사용자가 적은 기본 계정은 사전 이름으로 정리한다(앞뒤 공백·대문자, 큰따옴표로 감싼 이름만 그대로). 저장된 작업·사전에서 풀린 이름은 그대로 둔다.
    partial void OnSourceOwnerChanged(string value)
    {
        if (!_loading && !_dictionaryName && CopyOwners.NormalizeOwner(value) is var normalized && !string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SetDictionaryOwner(() => SourceOwner = normalized); // 정리한 값은 다시 정리하지 않는다("qa" → qa → QA 가 되지 않게)
            return;
        }

        RebaseRules();
    }

    partial void OnTargetOwnerChanged(string value)
    {
        if (!_loading && !_dictionaryName && CopyOwners.NormalizeOwner(value) is var normalized && !string.Equals(normalized, value, StringComparison.Ordinal))
        {
            SetDictionaryOwner(() => TargetOwner = normalized);
            return;
        }

        RebaseRules();
    }

    /// <summary>
    /// 기본 계정을 바꿀 때 규칙 계정을 맞추는 단 한 곳. 규칙마다 「기본을 따름」 표시(<see cref="TableRuleViewModel.FollowsDefaultSource"/>·
    /// <see cref="TableRuleViewModel.FollowsDefaultTarget"/>)를 보고 따르는 규칙만 바꾼다. 옛 값과 같은지로 고르지 않으므로
    /// 기본 계정을 A→B→A 로 바꾸면 처음과 같아진다(원래 B 에 쓰던 규칙이 A 로 끌려가지 않는다).
    /// 다른 계정 규칙(시노님으로 더한 APP2.ORDERS 등)·사용자가 대상 계정을 고른 규칙은 그대로다.
    /// </summary>
    private void RebaseRules()
    {
        if (_loading)
        {
            return;
        }

        foreach (var rule in Rules)
        {
            var source = rule.FollowsDefaultSource ? SourceOwner : rule.SourceOwner;
            rule.Rebase(source, DefaultTargetFor(rule.FollowsDefaultSource, source));
        }
    }

    /// <summary>기본 대상 계정 칸이 비었으면 기본 원본 계정(저장할 때와 같다).</summary>
    private string EffectiveDefaultTarget => string.IsNullOrWhiteSpace(TargetOwner) ? SourceOwner : TargetOwner;

    // 규칙의 기본 대상(결정 B-1): 원본이 기본 원본 계정을 따르면 작업 기본 대상 계정, 아니면 원본 계정 이름 그대로.
    private string DefaultTargetFor(bool followsSource, string source) => followsSource ? EffectiveDefaultTarget : source;

    /// <summary>규칙의 기본 대상 계정(대상 칸이 비면 쓰는 계정, 「기본 대상」 고르기).</summary>
    public string DefaultTargetFor(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return DefaultTargetFor(rule.FollowsDefaultSource, rule.SourceOwner);
    }

    /// <summary>
    /// 상세 칸 「대상 계정」 콤보에서 사용자가 고르거나 적은 값. 적은 이름은 사전 이름으로 정리한다(<see cref="CopyOwners.NormalizeOwner"/>).
    /// 목록의 「기본 대상」 항목(<see cref="TargetOwnerOption.IsDefault"/>)을 고르거나 칸을 비우면 다시 기본을 따르고, 그 밖에는 따름을 푼다.
    /// </summary>
    public static void ChooseTargetOwner(TableRuleViewModel rule, string? text, TargetOwnerOption? chosen = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (chosen is { } option && string.Equals(option.Owner, text?.Trim(), StringComparison.Ordinal))
        {
            rule.SetFollowedTarget(option.Owner, option.IsDefault);
            return;
        }

        var owner = CopyOwners.NormalizeOwner(text);
        if (string.Equals(owner, rule.TargetOwner, StringComparison.Ordinal))
        {
            return;
        }

        rule.TargetOwner = owner;
    }

    /// <summary>대상 접속 계정(대문자). 대상을 고르지 않았으면 빈 글자.</summary>
    public string TargetSessionUser => Target?.Profile.User is { Length: > 0 } user ? user.ToUpperInvariant() : string.Empty;

    /// <summary>
    /// 상세 칸 「대상 계정」 콤보의 고를 거리: 원본과 같음, 기본 대상, 접속 계정(같은 계정은 한 줄로 합친다). 다른 계정은 직접 적는다.
    /// </summary>
    public IReadOnlyList<TargetOwnerOption> TargetOwnerOptions(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var options = new List<(string Owner, List<string> Hints)>();
        void Add(string owner, string hint)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                return;
            }

            if (options.FirstOrDefault(o => CopyOwners.SameOwner(o.Owner, owner)) is { Owner: not null } found)
            {
                found.Hints.Add(hint);
                return;
            }

            options.Add((owner, [hint]));
        }

        Add(rule.SourceOwner, "원본과 같음");
        Add(EffectiveDefaultTarget, "기본 대상");
        Add(TargetSessionUser, "접속 계정");
        var ruleDefault = DefaultTargetFor(rule);
        return [.. options.Select(o => new TargetOwnerOption(o.Owner, string.Join(" · ", o.Hints.Select(DataCopyText.L)))
        {
            IsDefault = string.Equals(o.Owner, ruleDefault, StringComparison.Ordinal),
        })];
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText), nameof(IsCustomPeriod), nameof(HasPeriod))]
    private PeriodKindOption _periodChoice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _customFrom;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private DateTime? _customTo;

    /// <summary>직접 지정의 끝 날짜를 어떻게 셀지(「미만」 또는 「끝 날짜 포함」). 새 작업과 예전 작업은 「미만」.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private PeriodEndOption _periodEnd;

    /// <summary>전기일이 바뀌는 시각(0~23). DATE·TIMESTAMP 기간 열에만 쓴다. 0 = 자정.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PeriodText))]
    private int _dayStartHour;

    /// <summary>「모든 테이블을 같은 시점 기준으로 읽기」(설계 8.1). 새 작업은 켜져 있다.</summary>
    [ObservableProperty]
    private bool _consistentRead = true;

    [ObservableProperty]
    private int _batchSize = 5000;

    [ObservableProperty]
    private CommitMode _commit = CommitMode.PerTable;

    [ObservableProperty]
    private bool _disableTriggersByDefault;

    [ObservableProperty]
    private int _maxFailedRows = 100;

    [ObservableProperty]
    private long _sequenceGapConfirm = 10_000_000;

    [ObservableProperty]
    private TableRuleViewModel? _selectedRule;

    [ObservableProperty]
    private string _notice = string.Empty;

    private CopyJob? _loaded;

    public RuleEditorViewModel(DataCopyServices services)
    {
        _services = services;
        PeriodKinds =
        [
            new(PeriodKind.None, "기간 없음"),
            new(PeriodKind.LastWeek, "최근 1주"),
            new(PeriodKind.LastMonth, "최근 1달"),
            new(PeriodKind.Last3Months, "최근 3달"),
            new(PeriodKind.PreviousMonth, "지난달"),
            new(PeriodKind.Custom, "직접 지정"),
        ];
        _periodChoice = PeriodKinds[0];
        PeriodEnds = [new(false, "미만"), new(true, "끝 날짜 포함")];
        _periodEnd = PeriodEnds[0];
    }

    public IReadOnlyList<PeriodKindOption> PeriodKinds { get; }

    public IReadOnlyList<PeriodEndOption> PeriodEnds { get; }

    public ObservableCollection<ConnectionOption> SourceOptions { get; } = [];

    public ObservableCollection<ConnectionOption> TargetOptions { get; } = [];

    public ObservableCollection<TableRuleViewModel> Rules { get; } = [];

    public IReadOnlyList<RuleKind> RuleKinds { get; } = [RuleKind.All, RuleKind.Period, RuleKind.ParentKey, RuleKind.Custom, RuleKind.Exclude];

    public IReadOnlyList<CommitMode> CommitModes { get; } = [CommitMode.PerTable, CommitMode.PerBatch, CommitMode.AtEnd];

    public bool IsCustomPeriod => PeriodChoice.Kind == PeriodKind.Custom;

    public bool HasPeriod => PeriodChoice.Kind != PeriodKind.None;

    /// <summary>대상이 개발 접속일 때만 「테이블 다시 만들기」를 고를 수 있다(설계 6.2.3).</summary>
    public bool CanRecreate => Target is { } t && CopyTargetGuard.CanRecreate(t.Profile);

    /// <summary>고른 대상 접속이 읽기 전용이면 복사할 수 없다는 안내. 아니면 빈 글자.</summary>
    public string TargetReadOnlyNotice => Target is { Profile.ReadOnly: true } ? DataCopyText.ReadOnlyTarget : string.Empty;

    public bool HasTargetReadOnlyNotice => TargetReadOnlyNotice.Length > 0;

    /// <summary>
    /// 복사할 수 없는 DBMS 쌍이면 안내(xdb-copy D14). 같은 DBMS 끼리와, 교차 부품이 등록된 Oracle↔Tibero 는 허용한다.
    /// 한쪽을 안 골랐으면 빈 글자.
    /// </summary>
    public string UnsupportedPairNotice => Source is { } s && Target is { } t && !_services.IsPairSupported(s.Profile.Dbms, t.Profile.Dbms)
        ? DataCopyText.UnsupportedPair(DbmsAvailability.Label(s.Profile.Dbms), DbmsAvailability.Label(t.Profile.Dbms))
        : string.Empty;

    public bool HasUnsupportedPairNotice => UnsupportedPairNotice.Length > 0;

    public IReadOnlyList<TargetAction> AvailableActions =>
        CanRecreate
            ? [TargetAction.DeleteAllInsert, TargetAction.DeleteRangeInsert, TargetAction.AppendOnly, TargetAction.MergeByPk, TargetAction.Recreate]
            : [TargetAction.DeleteAllInsert, TargetAction.DeleteRangeInsert, TargetAction.AppendOnly, TargetAction.MergeByPk];

    /// <summary>오늘 날짜로 계산한 기간 범위 글자(예: <c>2026-10-07 ~ 2026-11-07</c>). 끝 날짜는 포함하지 않는다. 기간이 없으면 안내 글.</summary>
    public string PeriodText
    {
        get
        {
            var resolved = ResolvePeriod();
            return resolved is { From: { } from, To: { } to }
                ? string.Create(CultureInfo.InvariantCulture, $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd} 미만")
                : "기간 없음";
        }
    }

    public CopyPeriod Period => new(PeriodChoice.Kind, CustomFrom, CustomTo,
        PeriodChoice.Kind == PeriodKind.Custom && PeriodEnd.Inclusive,
        PeriodChoice.Kind == PeriodKind.None ? 0 : Math.Clamp(DayStartHour, 0, 23));

    public ResolvedPeriod ResolvePeriod() => PeriodCalculator.Resolve(Period, _services.Time.GetLocalNow().DateTime);

    /// <summary>접속 목록을 채운다. 대상 목록의 운영 접속은 선택할 수 없고 이유를 툴팁으로 보인다.</summary>
    public void SetConnections(IReadOnlyList<ConnectionProfile> profiles)
    {
        var keepSource = Source?.Profile;
        var keepTarget = Target?.Profile;
        SourceOptions.Clear();
        TargetOptions.Clear();
        foreach (var profile in profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            SourceOptions.Add(new ConnectionOption(profile, true, profile.ReadOnly ? "읽기 전용 접속" : null));
            var allowed = CopyTargetGuard.IsAllowedTarget(profile);
            TargetOptions.Add(new ConnectionOption(profile, allowed, allowed ? (profile.ReadOnly ? DataCopyText.ReadOnlyTarget : null) : DataCopyText.ProductionTarget));
        }

        // 목록을 다시 채워도 고른 접속은 같은 접속으로 이어 둔다(목록을 비우면 선택이 풀리기 때문).
        Source = keepSource is null ? null : SourceOptions.FirstOrDefault(o => o.Profile.Id == keepSource.Id && o.Profile.Name == keepSource.Name);
        Target = keepTarget is null ? null : TargetOptions.FirstOrDefault(o => o.Profile.Id == keepTarget.Id && o.Profile.Name == keepTarget.Name);
    }

    /// <summary>
    /// 저장된 작업을 편집 화면에 올린다. 접속은 호출한 쪽이 다시 이은 결과(<paramref name="source"/>·<paramref name="target"/>)를 받는다.
    /// 다시 이은 대상이 없으면(운영 차단 포함) 대상을 비우고 알림을 남긴다.
    /// </summary>
    public void Load(CopyJob job, ConnectionProfile? source, ConnectionProfile? target, string notice = "")
    {
        ArgumentNullException.ThrowIfNull(job);
        _loaded = job;
        _provisionalTargetOwner = null;
        Name = job.Name;
        Source = source is null ? null : SourceOptions.FirstOrDefault(o => o.Profile.Id == source.Id && o.Profile.Name == source.Name);
        Target = target is null ? null : TargetOptions.FirstOrDefault(o => o.Profile.Id == target.Id && o.Profile.Name == target.Name);
        _loading = true;
        try
        {
            SourceOwner = job.SourceOwner;
            TargetOwner = job.TargetOwner;
        }
        finally
        {
            _loading = false;
        }

        PeriodChoice = PeriodKinds.First(k => k.Kind == job.Period.Kind);
        CustomFrom = job.Period.From;
        CustomTo = job.Period.To;
        PeriodEnd = PeriodEnds.First(e => e.Inclusive == job.Period.EndInclusive);
        DayStartHour = Math.Clamp(job.Period.DayStartHour, 0, 23);
        ConsistentRead = job.ConsistentRead;
        BatchSize = job.Options.BatchSize;
        Commit = job.Options.Commit;
        DisableTriggersByDefault = job.Options.DisableTriggersByDefault;
        MaxFailedRows = job.Options.MaxFailedRows;
        SequenceGapConfirm = job.Options.SequenceGapConfirm;
        Rules.Clear();
        // 규칙마다 실제 계정을 풀어 둔다(비어 있던 계정 = 작업 기본 계정). 저장할 때는 늘 계정을 적어 저장한다.
        // 「기본을 따름」: 원본 계정이 비었거나 작업 기본 원본 계정이면 따르고, 대상 계정이 비었거나 그 규칙의 기본 대상(MapTarget)이면 따른다.
        _removedOrigins.Clear();
        foreach (var rule in job.Tables)
        {
            var ruleSource = CopyOwners.Source(job, rule);
            var vm = new TableRuleViewModel(rule, ruleSource, CopyOwners.Target(job, rule))
            {
                Origin = rule,
                SessionUser = TargetSessionUser,
                FollowsDefaultSource = string.IsNullOrEmpty(rule.SourceOwner) || string.Equals(rule.SourceOwner, job.SourceOwner, StringComparison.Ordinal),
                FollowsDefaultTarget = string.IsNullOrEmpty(rule.TargetOwner) || string.Equals(rule.TargetOwner, CopyOwners.MapTarget(job, ruleSource), StringComparison.Ordinal),
            };
            vm.DefaultTarget = DefaultTargetFor(vm); // 대상 계정은 저장된 글자 그대로 둔다
            vm.RestoreOverrides(job.TableDefaults is null ? RuleOverrides.All : rule.Overrides); // 옛 파일: 모든 테이블을 따로 바꾼 것으로 연다(저장된 값이 그대로)
            Rules.Add(vm);
        }

        _legacyTableDefaults = job.TableDefaults is null;
        SetTableDefaults(job.TableDefaults ?? InitialTableDefaults);

        SelectedRule = Rules.FirstOrDefault();
        Variables.Clear();
        foreach (var variable in job.Variables)
        {
            Variables.Add(new CopyVariableViewModel(variable));
        }

        Notice = notice;
        OnPropertyChanged(nameof(PeriodText));
    }

    /// <summary>작업 변수(조건에서 <c>:이름</c> 으로 쓴다).</summary>
    public ObservableCollection<CopyVariableViewModel> Variables { get; } = [];

    /// <summary>빈 변수 한 줄을 더한다.</summary>
    public CopyVariableViewModel AddVariable()
    {
        var variable = new CopyVariableViewModel();
        Variables.Add(variable);
        return variable;
    }

    public void RemoveVariable(CopyVariableViewModel variable) => Variables.Remove(variable);

    /// <summary>화면에 없는 필드(마지막 실행·식별자 등)의 기준 작업을 바꾼다. 실행·이름 바꾸기 뒤 옛 값으로 덮어쓰지 않게 한다.</summary>
    /// <remarks>
    /// 새 기준 작업의 규칙이 이 화면이 만든 규칙(<see cref="TableRuleViewModel.LastBuilt"/>)이면 줄마다 그 규칙을 <see cref="TableRuleViewModel.Origin"/> 으로
    /// 다시 잇는다(그래야 다음 저장이 시퀀스 연결을 그 규칙의 새 키로 잇는다).
    /// </remarks>
    public void UpdateBase(CopyJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _loaded = job;
        var tables = new HashSet<TableRule>(job.Tables, ReferenceEqualityComparer.Instance);
        foreach (var rule in Rules)
        {
            if (rule.LastBuilt is { } built && tables.Contains(built))
            {
                rule.Origin = built;
            }
        }
    }

    // 지운 규칙(불러온 작업의 규칙). 그 규칙의 시퀀스 연결도 저장할 때 지운다.
    private readonly HashSet<TableRule> _removedOrigins = new(ReferenceEqualityComparer.Instance);

    /// <summary>화면의 값을 작업 모델로 만든다. 접속 참조는 이름과 사용자명, 이 PC 의 ID 는 힌트로만 담는다(설계 6.3).</summary>
    public CopyJob BuildJob()
    {
        var baseJob = _loaded ?? throw new InvalidOperationException("불러온 작업이 없다.");
        var sourceOwner = SourceOwner;
        var targetOwner = string.IsNullOrWhiteSpace(TargetOwner) ? SourceOwner : TargetOwner;
        var defaults = baseJob with { SourceOwner = sourceOwner, TargetOwner = targetOwner };

        // 전체 기본값이 없던 옛 작업은 전체 값도 테이블별 표시도 건드리지 않은 동안 옛 형식 그대로 둔다(모든 테이블이 따로 바꾼 것이라 값이 같다).
        var legacy = _legacyTableDefaults && CurrentTableDefaults == InitialTableDefaults && Rules.All(r => r.Overrides == RuleOverrides.All);

        // 규칙마다 계정을 적어 저장한다(묶음 B 설계 1장): 비었으면 기본 원본 계정, 대상이 비었으면 그 규칙의 기본 대상(화면에 흐린 글자로 보인 계정).
        TableRule Explicit(TableRuleViewModel vm)
        {
            var rule = vm.ToRule();
            var source = rule.SourceOwner ?? sourceOwner;
            var target = rule.TargetOwner ?? (vm.DefaultTarget.Length > 0 ? vm.DefaultTarget : CopyOwners.MapTarget(defaults, source));
            var built = rule with { SourceOwner = source, TargetOwner = target };
            if (legacy)
            {
                built = built with { Overrides = RuleOverrides.None }; // 옛 형식 그대로 다시 쓴다(열기만 한 작업이 바뀐 것으로 보이지 않게)
            }

            vm.LastBuilt = built;
            return built;
        }

        var pairs = Rules.Select(vm => (Vm: vm, Rule: Explicit(vm))).ToList();
        var tables = pairs.Select(p => p.Rule).ToList();
        var job = defaults with { Tables = tables };

        // 시퀀스 연결의 표는 규칙 키다. 기본 계정을 바꿔 키가 달라진 규칙은 새 키로 잇고, 시퀀스 계정이 그 표의 옛 대상 계정이면(예전 작업은
        // 작업 대상 계정을 적어 두었다) 새 대상 계정으로 함께 옮긴다. 지운 규칙의 시퀀스 연결은 함께 지운다(남기면 점검으로 넘어가지 못한다).
        var sequences = new List<SequenceRule>();
        foreach (var seq in baseJob.Sequences)
        {
            var before = CopyOwners.Find(baseJob, seq.Table);
            if (before is not null && pairs.FirstOrDefault(p => ReferenceEquals(p.Vm.Origin, before)) is { Vm: not null } now)
            {
                var owner = seq.Owner is { Length: > 0 } o && CopyOwners.SameOwner(o, CopyOwners.Target(baseJob, before))
                    ? CopyOwners.Target(job, now.Rule)
                    : seq.Owner;
                sequences.Add(seq with { Table = CopyOwners.Key(job, now.Rule), Owner = owner });
            }
            else if (before is null || !_removedOrigins.Contains(before))
            {
                sequences.Add(seq);
            }
        }
        return baseJob with
        {
            Name = Name,
            Source = Source is { } s ? new ConnectionRef(s.Profile.Name, s.Profile.User, s.Profile.Id) : baseJob.Source,
            Target = Target is { } t ? new ConnectionRef(t.Profile.Name, t.Profile.User, t.Profile.Id) : new ConnectionRef(string.Empty, string.Empty),
            SourceOwner = sourceOwner,
            TargetOwner = targetOwner,
            Period = Period,
            Tables = tables,
            Sequences = sequences,
            Variables = [.. Variables.Select(v => v.ToVariable())],
            TableDefaults = legacy ? null : CurrentTableDefaults,
            Options = new CopyOptions
            {
                BatchSize = BatchSize,
                Commit = Commit,
                DisableTriggersByDefault = DisableTriggersByDefault,
                MaxFailedRows = MaxFailedRows,
                SequenceGapConfirm = SequenceGapConfirm,
            },
            ConsistentRead = ConsistentRead,
        };
    }

    /// <summary>
    /// 점검으로 넘어가기 전 입력 확인. 문제가 있으면 그 이유 글자, 없으면 null.
    /// 대상이 운영 접속이면 여기서도 막는다(선택 목록·재연결·가져오기·실행기와 함께 네 곳 중 하나).
    /// </summary>
    public string? Validate()
    {
        if (Source is null)
        {
            return DataCopyText.SourceMissing;
        }

        if (Target is null)
        {
            return DataCopyText.TargetMissing;
        }

        // 지원하지 않는 DBMS 쌍은 세션을 열기 전에 이유를 알리고 막는다.
        if (UnsupportedPairNotice is { Length: > 0 } unsupportedPair)
        {
            return unsupportedPair;
        }

        if (!CopyTargetGuard.IsAllowedTarget(Target.Profile))
        {
            return DataCopyText.ProductionTarget;
        }

        // 읽기 전용 접속은 정책 관문이 모든 쓰기를 거부하므로, 사전 점검 전에 이유를 알리고 막는다.
        if (Target.Profile.ReadOnly)
        {
            return DataCopyText.ReadOnlyTarget;
        }

        // 규칙 사이 모순: 같은 원본 표를 두 번, 한 대상 표에 두 규칙.
        var job = _loaded is null ? null : BuildJob();
        if (job is not null && CopyOwners.Problems(job) is { Count: > 0 } problems)
        {
            return problems[0];
        }

        // 같은 접속에서 어떤 규칙이 쓰는(지우는) 대상 표가 어떤 규칙이 읽는 원본 표이면 원본 행을 지운다(표 단위, 규칙 사이까지 본다).
        if (job is not null && Source.Profile.Id == Target.Profile.Id && string.Equals(Source.Profile.Name, Target.Profile.Name, StringComparison.Ordinal)
            && CopyOwners.TargetsReadAsSource(job) is { Count: > 0 } overlaps)
        {
            return DataCopyText.SameSourceTables(overlaps.Select(o => o.Table));
        }

        // 「테이블 다시 만들기」는 개발 접속에서만 쓴다. 대상을 바꾸거나 가져온 작업에 남은 값은 여기서 막는다.
        if (!CopyTargetGuard.CanRecreate(Target.Profile) && Rules.FirstOrDefault(r => r.Kind != RuleKind.Exclude && r.Action == TargetAction.Recreate) is { } recreate)
        {
            return DataCopyText.RecreateNotAllowed(recreate.Table);
        }

        // 괄호 밖으로 빠져나가는 조건(주석·세미콜론 포함)은 대상 지우기 범위를 넓히므로 넘어가지 않는다.
        if (Rules.FirstOrDefault(r => r.Kind != RuleKind.Exclude && r.HasConditionError) is { } invalid)
        {
            return DataCopyText.InvalidCondition(invalid.Table, invalid.ConditionError!);
        }

        // 작업 변수: 이름·값이 바르고, 조건에 쓴 :이름 이 모두 변수(또는 :FROM_DT·:TO_DT)여야 한다.
        if (job is not null)
        {
            if (CopyVariables.Problems(job.Variables) is { Count: > 0 } variableProblems)
            {
                return variableProblems[0];
            }

            foreach (var rule in job.Tables.Where(r => r.Kind != RuleKind.Exclude))
            {
                var where = rule.Kind switch
                {
                    RuleKind.Custom => rule.CustomWhere,
                    RuleKind.Period => rule.ExtraWhere,
                    _ => null,
                };
                if (!string.IsNullOrWhiteSpace(where) && CopyVariables.UnknownProblem(where, job.Variables) is { } unknown)
                {
                    return DataCopyText.InvalidCondition(rule.Table, unknown);
                }
            }
        }

        return Rules.Any(r => r.Kind != RuleKind.Exclude) ? null : DataCopyText.NoTables;
    }

    /// <summary>스키마 브라우저 「데이터 복사에 추가」 또는 창의 표 추가. 이미 있는 표면 그 규칙을 선택만 한다.</summary>
    public Task<TableRuleViewModel> AddTableAsync(DbObjectRef table, ISession? sourceSession, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        return AddTableAsync(new CopyTableName(table.Owner, table.Name), sourceSession, ct);
    }

    /// <summary>
    /// 원본에서 풀린 계정·표(<see cref="CopyTableInput.ResolveAsync"/>)를 규칙으로 더한다. 같은 원본 계정·표의 규칙이 있으면(규칙 키가 같으면) 그 규칙을 선택만 한다.
    /// 대상 계정은 결정 B-1: 원본 계정이 기본 원본 계정이면 기본 대상 계정, 아니면 원본 계정 이름 그대로.
    /// </summary>
    public async Task<TableRuleViewModel> AddTableAsync(CopyTableName table, ISession? sourceSession, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (Rules.FirstOrDefault(r => CopyOwners.SameOwner(EffectiveSource(r), table.Owner)
                                      && string.Equals(r.Table, table.Table, StringComparison.Ordinal)) is { } existing)
        {
            SelectedRule = existing;
            return existing;
        }

        if (string.IsNullOrWhiteSpace(SourceOwner))
        {
            SetDictionaryOwner(() => SourceOwner = table.Owner); // 사전에서 풀린 이름: 정리하지 않는다
        }

        // 대상 소유자는 대상 접속의 사용자(스키마)로 시작한다. 대상을 아직 고르지 않았으면(스키마 브라우저에서 새 작업으로 추가) 원본 표 소유자로
        // 임시로 채워 두고, 나중에 대상을 고르면 그 사용자로 바꾼다(그 사이 사용자가 고친 값은 그대로 둔다).
        if (string.IsNullOrWhiteSpace(TargetOwner))
        {
            if (Target is { Profile.User: { Length: > 0 } user })
            {
                TargetOwner = user.ToUpperInvariant();
            }
            else
            {
                SetDictionaryOwner(() => TargetOwner = table.Owner);
                _provisionalTargetOwner = table.Owner;
            }
        }

        // 기본 원본 계정의 표면 원본·대상 모두 기본을 따른다. 다른 계정의 표는 원본은 그대로, 대상은 원본 계정 이름(기본 대상)을 따른다.
        var followsSource = CopyOwners.SameOwner(table.Owner, SourceOwner);
        var target = DefaultTargetFor(followsSource, table.Owner);
        var rule = new TableRuleViewModel(new TableRule(table.Table, RuleKind.All, TargetAction.DeleteAllInsert) { WrittenAs = table.WrittenAs }, table.Owner, target)
        {
            SessionUser = TargetSessionUser,
            FollowsDefaultSource = followsSource,
            FollowsDefaultTarget = true,
            DefaultTarget = target,
        };
        Rules.Add(rule);
        SelectedRule = rule;
        await LoadColumnsAsync(rule, new DbObjectRef(table.Owner, table.Table, "TABLE"), sourceSession, ct).ConfigureAwait(true);

        // 전체 기본값으로 채운다(규칙·대상 처리·없으면 만들기). 자동 규칙은 기간이 정해진 작업에 업무 날짜 열이 있는 테이블이면 기간, 아니면 모든 행.
        ApplyDefaultsTo(rule);
        return rule;
    }

    /// <summary>원본 세션이 열려 있으면 표 열 정보와 부모 표 정보를 읽어 규칙에 넣는다. 못 읽으면 알림만 남기고 규칙은 유지한다.</summary>
    public async Task LoadColumnsAsync(TableRuleViewModel rule, DbObjectRef table, ISession? sourceSession, CancellationToken ct)
    {
        if (sourceSession is null)
        {
            return;
        }

        if (_services.Metadata is not { } metadata)
        {
            Notice = DataCopyText.MetadataMissing;
            return;
        }

        try
        {
            var info = await metadata.GetTableAsync(sourceSession, table, ct).ConfigureAwait(true);
            // 부모 표는 계정.표로 담는다(다른 계정의 같은 이름 표가 섞이지 않게).
            var parents = new Dictionary<string, TableInfo>(StringComparer.Ordinal);
            foreach (var fk in info.Constraints.Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null))
            {
                var parentRef = fk.ReferencedTable!;
                var key = parentRef.Owner + "." + parentRef.Name;
                if (!parents.ContainsKey(key))
                {
                    parents[key] = await metadata.GetTableAsync(sourceSession, parentRef, ct).ConfigureAwait(true);
                }
            }

            rule.SetSource(info, parents);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Notice = $"테이블 {table.Name} 의 열 정보를 읽지 못했습니다: {e.Message}";
        }
    }

    /// <summary>규칙의 원본 계정(비었으면 기본 원본 계정).</summary>
    public string EffectiveSource(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.SourceOwner.Length > 0 ? rule.SourceOwner : SourceOwner;
    }

    /// <summary>규칙을 지운다. 그 규칙에 이은 시퀀스 연결도 함께 지운다(저장할 때, <see cref="BuildJob"/>).</summary>
    public void RemoveRule(TableRuleViewModel rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (Rules.Remove(rule) && rule.Origin is { } origin)
        {
            _removedOrigins.Add(origin);
        }

        if (ReferenceEquals(SelectedRule, rule))
        {
            SelectedRule = Rules.FirstOrDefault();
        }
    }
}

/// <summary>전체 기본값 「규칙」: 자동 = 기간이 있고 기간 열이 있는 테이블은 기간, 아니면 모든 행.</summary>
public enum DefaultKindChoice
{
    Auto,
    All,
    Period,
    Exclude,
}

/// <summary>전체 기본값 「대상 처리」: 자동 = 규칙 종류별 기본값(<see cref="TargetActions.DefaultFor"/>).</summary>
public enum DefaultActionChoice
{
    Auto,
    DeleteAllInsert,
    DeleteRangeInsert,
    AppendOnly,
    MergeByPk,
}
