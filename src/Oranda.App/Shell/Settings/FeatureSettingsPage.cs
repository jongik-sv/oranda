using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 창 「기능」 탭의 모델(TSK-07-04 → TSK-09-08, PRD 13 UX-09~11, TRD 11.4, D-017): 주 업무 라디오 5개 + 덧붙임 확인란 2개(PL/SQL·MyBatis),
/// 맞지 않는 기존 조합은 「지금 조합: …(그대로 두기)」 줄, 기능 25개 세부는 접힌 「고급」 묶음, 재시작 안내, 팩 선언 바로 가기.
/// 주 업무 ↔ 저장 형식 변환은 <see cref="PrimaryWork"/>, 화면 문구와 묶음·기능 목록은 <see cref="FeatureCatalog"/>·<see cref="FeatureTexts"/> 에서 만든다.
/// 확인란 상태는 <see cref="FeatureCatalog.Resolve"/> 와 같은 규칙(Role 기본 + 직접 켬 − 직접 끔, 조회 전용·Requires 로 못 켜는 것은 끔)이고,
/// 저장 값(<see cref="ToSettings"/>)은 Role 기본값과 다른 기능만 Enabled/Disabled 에 남긴다. 라디오·덧붙임을 건드리지 않으면 roles·allFeatures 는 그대로다.
/// 이 버전이 모르는 기능 id 는 지우지 않고 그대로 돌려 쓴다(모르는 Role id 는 「지금 조합」 으로 보존된다).
/// </summary>
public sealed partial class FeatureSettingsPage : ObservableObject, IDisposable
{
    /// <summary>팩 선언 안내 줄의 「접속 관리자 열기」 가 실행하는 명령(D-012, UX-11).</summary>
    public const string ConnectionManagerCommandId = "oranda.conn.manage";

    private readonly IShellText _text;
    private readonly IFeatureGate? _gate;
    private readonly FeaturesSettings _initial;
    private readonly PrimaryWorkChoice _initialChoice;
    private readonly List<string> _unknownEnabled;
    private readonly List<string> _unknownDisabled;
    private PrimaryWorkKind _primary;
    private bool _plsql;
    private bool _myBatis;
    private bool _recomputing;

    /// <param name="initial">설정 창을 열 때의 <c>features</c> 섹션.</param>
    /// <param name="text">화면 글자 조회.</param>
    /// <param name="gate">재시작 안내(<see cref="IFeatureGate.PendingRestart"/>)를 읽을 게이트. null 이면 안내를 내지 않는다.</param>
    public FeatureSettingsPage(FeaturesSettings? initial, IShellText text, IFeatureGate? gate = null)
    {
        _text = text;
        _gate = gate;
        _initial = initial ?? new FeaturesSettings();

        // 아무것도 고른 적 없으면 기본 Role(SQL 개발)이 골라진 채 보인다(D-013). 손대지 않고 저장하면 설정은 그대로다.
        IsDefaultRole = FeatureCatalog.IsUnchosen(_initial);
        _showDefaultRoleNotice = IsDefaultRole;
        _initialChoice = PrimaryWork.FromSettings(_initial);
        _primary = _initialChoice.Primary;
        _plsql = _initialChoice.Plsql;
        _myBatis = _initialChoice.MyBatis;

        _unknownEnabled = [.. _initial.Enabled.Where(id => !FeatureCatalog.IsKnownFeature(id)).Distinct(StringComparer.Ordinal)];
        _unknownDisabled = [.. _initial.Disabled.Where(id => !FeatureCatalog.IsKnownFeature(id)).Distinct(StringComparer.Ordinal)];

        var options = PrimaryWork.Choices.Select(k => new PrimaryWorkOption(this, k, text.Lookup(PrimaryWork.NameKey(k)), Describe(text, k))).ToList();
        if (_initialChoice.Primary == PrimaryWorkKind.Custom)
        {
            // 맞지 않는 기존 조합은 맨 아래 한 줄로 보이고, 고르면 그 조합 그대로 저장된다(D-017).
            options.Add(new PrimaryWorkOption(this, PrimaryWorkKind.Custom, CustomName(text, _initialChoice.CustomRoles), string.Empty));
        }

        PrimaryOptions = options;

        var groups = new List<FeatureGroupItem>();
        foreach (var group in Enum.GetValues<FeatureGroup>())
        {
            var items = FeatureCatalog.Features
                .Where(f => f.Group == group)
                .Select(f => new FeatureItem(this, f, text, DesiredFor(f.Id)))
                .ToList();
            groups.Add(new FeatureGroupItem(text.Lookup(FeatureCatalog.GroupNameKey(group)), items));
        }

        Groups = groups;
        Items = [.. groups.SelectMany(g => g.Items)];

        if (_gate is not null)
        {
            _gate.Changed += OnGateChanged;
        }

        Recompute();
        RefreshPending();
    }

    // 설정에 직접 적힌 값: 끔이 이긴다(TRD 10.5).
    private bool? DesiredFor(string id) =>
        _initial.Disabled.Contains(id, StringComparer.Ordinal) ? false
        : _initial.Enabled.Contains(id, StringComparer.Ordinal) ? true
        : null;

    private static string Describe(IShellText text, PrimaryWorkKind kind) =>
        PrimaryWork.DescriptionKey(kind) is { } key ? text.Lookup(key) : string.Empty;

    /// <summary>「지금 조합: SQL 개발자, DB 튜너(그대로 두기)」. 모르는 id 는 그대로, 빈 목록은 「Role 없음」.</summary>
    internal static string CustomName(IShellText text, IReadOnlyList<string> roles)
    {
        var names = roles.Distinct(StringComparer.Ordinal).Select(id => FeatureCatalog.FindRole(id) is { } r ? text.Lookup(r.NameKey) : id).ToList();
        var list = names.Count == 0 ? text.Lookup(FeatureTexts.PrimaryCustomNone) : string.Join(", ", names);
        return string.Format(CultureInfo.CurrentCulture, text.Lookup(FeatureTexts.PrimaryCustom), list);
    }

    // ---- 글자 ----

    public string TabTitle => _text.Lookup(FeatureTexts.SettingsTab);

    public string PrimaryTitle => _text.Lookup(FeatureTexts.PrimaryTitle);

    public string AddonTitle => _text.Lookup(FeatureTexts.AddonTitle);

    public string PlsqlAddonText => _text.Lookup(FeatureTexts.AddonPlsql);

    public string MyBatisAddonText => _text.Lookup(FeatureTexts.AddonMyBatis);

    /// <summary>덧붙임 확인란 툴팁: 그 Role 의 한 줄 설명.</summary>
    public string PlsqlAddonTip => _text.Lookup(FeatureCatalog.FindRole(RoleIds.PlsqlDev)!.DescriptionKey);

    public string MyBatisAddonTip => _text.Lookup(FeatureCatalog.FindRole(RoleIds.MyBatisDev)!.DescriptionKey);

    public string ResetText => _text.Lookup(FeatureTexts.ResetToRoleDefaults);

    public string ViewerNoticeText => _text.Lookup(FeatureTexts.ViewerNotice);

    public string PackNoticeText => _text.Lookup(FeatureTexts.PackNotice);

    public string PackLinkText => _text.Lookup(FeatureTexts.PackLink);

    /// <summary>링크의 화면 읽기 이름: 「접속 관리자 열기」 만으로는 왜 여는지 모르므로 안내 글을 앞에 붙인다.</summary>
    public string PackLinkName => PackNoticeText + " — " + PackLinkText;

    public string NoRoleNoticeText => _text.Lookup(FeatureTexts.NoRoleNotice);

    public string DefaultRoleNoticeText =>
        string.Format(CultureInfo.CurrentCulture, _text.Lookup(FeatureTexts.DefaultRoleNotice), DefaultRoleName(_text));

    /// <summary>기본 Role(D-013) 의 화면 이름. 시작 화면 「지금 … 메뉴입니다」 줄도 쓴다.</summary>
    internal static string DefaultRoleName(IShellText text) =>
        string.Join(", ", FeatureCatalog.DefaultRoles.Select(id => FeatureCatalog.FindRole(id) is { } r ? text.Lookup(r.NameKey) : id));

    // ---- 상태 ----

    /// <summary>주 업무 라디오(<see cref="PrimaryWork.Choices"/> 5개, 설정이 맞지 않는 조합이면 맨 끝에 「지금 조합」 줄).</summary>
    public IReadOnlyList<PrimaryWorkOption> PrimaryOptions { get; }

    /// <summary>기능 묶음 5개(<see cref="FeatureGroup"/> 순서).</summary>
    public IReadOnlyList<FeatureGroupItem> Groups { get; }

    /// <summary>기능 25개(묶음 순서로 펼친 목록).</summary>
    public IReadOnlyList<FeatureItem> Items { get; }

    /// <summary>
    /// 고른 주 업무. 바꾸면 덧붙임 흐림·기능 확인란·안내가 다시 계산된다. 「지금 조합」(<see cref="PrimaryWorkKind.Custom"/>)은
    /// 창을 열 때 설정이 그 모양이었을 때만 고를 수 있다(다른 때는 무시한다).
    /// </summary>
    public PrimaryWorkKind Primary
    {
        get => _primary;
        set
        {
            if (value == _primary || (value == PrimaryWorkKind.Custom && _initialChoice.Primary != PrimaryWorkKind.Custom))
            {
                return;
            }

            _primary = value;
            OnPropertyChanged();
            MarkDirty();
            Recompute();
        }
    }

    /// <summary>지금 화면 값(<see cref="PrimaryWork.Apply"/> 에 넘기는 모양).</summary>
    public PrimaryWorkChoice Choice =>
        _primary == PrimaryWorkKind.Custom ? _initialChoice : new PrimaryWorkChoice(_primary, _plsql, _myBatis);

    /// <summary>덧붙임을 고칠 수 있나(SQL 개발·튜닝·운영 DBA).</summary>
    public bool AddonsEnabled => PrimaryWork.AllowsAddons(_primary);

    /// <summary>
    /// 「PL/SQL 패키지·프로시저도 고친다」. 모든 기능이면 켜져 보이고(이미 포함), 조회만이면 꺼져 보이고, 지금 조합이면 그 조합에 있는지 보인다 —
    /// 모두 흐리다. 흐린 동안 사용자가 고른 값은 기억했다가 주 업무를 되돌리면 다시 보인다.
    /// </summary>
    public bool PlsqlAddon
    {
        get => AddonShown(_plsql, _initialChoice.Plsql);
        set => SetAddon(ref _plsql, value);
    }

    /// <summary>「MyBatis 매퍼를 쓴다」(<see cref="PlsqlAddon"/> 와 같은 규칙).</summary>
    public bool MyBatisAddon
    {
        get => AddonShown(_myBatis, _initialChoice.MyBatis);
        set => SetAddon(ref _myBatis, value);
    }

    private bool AddonShown(bool chosen, bool custom) => _primary switch
    {
        PrimaryWorkKind.All => true,
        PrimaryWorkKind.Viewer => false,
        PrimaryWorkKind.Custom => custom,
        _ => chosen,
    };

    private void SetAddon(ref bool field, bool value)
    {
        if (!AddonsEnabled || field == value)
        {
            // 흐린 확인란에 묶인 값이 화면과 어긋나지 않게 다시 알린다.
            OnPropertyChanged(nameof(PlsqlAddon));
            OnPropertyChanged(nameof(MyBatisAddon));
            return;
        }

        field = value;
        MarkDirty();
        Recompute();
    }

    /// <summary>덧붙임이 흐린 이유 한 줄(고칠 수 있으면 빈 글자).</summary>
    public string AddonReason => _primary switch
    {
        PrimaryWorkKind.All => _text.Lookup(FeatureTexts.AddonAllIncluded),
        PrimaryWorkKind.Viewer => _text.Lookup(FeatureTexts.AddonViewerBlocked),
        PrimaryWorkKind.Custom => _text.Lookup(FeatureTexts.AddonCustomBlocked),
        _ => string.Empty,
    };

    public bool HasAddonReason => AddonReason.Length > 0;

    /// <summary>창을 열 때 아무것도 고른 적 없어 기본 Role(SQL 개발자)을 보이는 중이었다(D-013, 시험·화면용).</summary>
    public bool IsDefaultRole { get; }

    /// <summary>기본 Role 을 보이는 중이라는 안내 띠(D-013). 무엇이든 바꾸면 사라진다(저장하면 직접 고른 것이 된다).</summary>
    [ObservableProperty]
    private bool _showDefaultRoleNotice;

    /// <summary>조회만(조회 전용 Role 하나)이다: 탭 위 안내와 쓰기 전용 기능 흐림.</summary>
    [ObservableProperty]
    private bool _isViewerOnly;

    /// <summary>지금 조합이 Role 0개(<c>roles: []</c>)다: 공통 기능만 켜진다는 한 줄(TSK-07-10). 화면에서 새로 생기지는 않는다.</summary>
    [ObservableProperty]
    private bool _showNoRoleNotice;

    /// <summary>튜너 Role 이 들어 있다: 라이선스 팩 선언 안내 한 줄 + 「접속 관리자 열기」(D-012).</summary>
    [ObservableProperty]
    private bool _showPackNotice;

    /// <summary>기본값과 다르게 직접 켜거나 끈 기능이 있다(「되돌리기」를 켠다).</summary>
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [ObservableProperty]
    private bool _hasOverrides;

    /// <summary>「고급」 접힘 묶음의 머리: 「기능 하나씩 켜고 끄기(고급) — 켜진 기능 {0}/{1} · 직접 바꾼 것 {2}개」.</summary>
    [ObservableProperty]
    private string _advancedHeader = string.Empty;

    /// <summary>켜진 기능 수(머리의 {0}).</summary>
    public int EnabledCount { get; private set; }

    /// <summary>직접 바꾼 기능 수(머리의 {2}, 배지가 보이는 기능 수).</summary>
    public int OverrideCount { get; private set; }

    /// <summary>「고급」 묶음이 펼쳐졌나. 처음에는 접혀 있다(UX-10).</summary>
    [ObservableProperty]
    private bool _isAdvancedExpanded;

    /// <summary>재시작해야 적용되는 기능 안내 한 줄. 없으면 빈 글자.</summary>
    [NotifyPropertyChangedFor(nameof(HasRestartBanner))]
    [ObservableProperty]
    private string _restartBannerText = string.Empty;

    public bool HasRestartBanner => RestartBannerText.Length > 0;

    /// <summary>이 탭에서 사용자가 무엇이든 바꿨다(안 바꿨으면 저장 때 설정을 건드리지 않는다).</summary>
    public bool IsDirty { get; private set; }

    /// <summary>「접속 관리자 열기」 가 부르는 길. 비우면 <see cref="ShellCommandRunner"/> 로 <see cref="ConnectionManagerCommandId"/> 를 실행한다(시험이 넣는다).</summary>
    public Func<Task>? OpenConnectionManager { get; set; }

    // ---- 사용자 동작 ----

    /// <summary>기능 확인란을 눌렀다: 바람이 Role 기본과 같아지면 직접 표시를 지운다(최소 기록).</summary>
    internal void OnFeatureToggled(FeatureItem item, bool wanted)
    {
        item.Desired = wanted == item.RoleDefault ? null : wanted;
        MarkDirty();
        Recompute();
    }

    /// <summary>모든 기능의 직접 켬·끔을 지우고 기본값으로 되돌린다(고른 주 업무는 그대로, 이 버전이 모르는 id 도 그대로).</summary>
    [RelayCommand(CanExecute = nameof(HasOverrides))]
    private void Reset()
    {
        foreach (var item in Items)
        {
            item.Desired = null;
        }

        MarkDirty();
        Recompute();
    }

    /// <summary>팩 선언 바로 가기(UX-11): 접속 관리자를 연다. 팩은 접속 프로필 편집의 「고급 설정」 에서 고른다.</summary>
    [RelayCommand]
    private Task OpenConnectionManagerLink() =>
        OpenConnectionManager?.Invoke() ?? ShellCommandRunner.RunAsync(ConnectionManagerCommandId);

    private void MarkDirty()
    {
        if (!_recomputing)
        {
            IsDirty = true;
            ShowDefaultRoleNotice = false;
        }
    }

    // ---- 계산 ----

    /// <summary>화면 상태 전체를 지금 고른 값에서 다시 계산한다.</summary>
    private void Recompute()
    {
        _recomputing = true;
        try
        {
            var roles = SelectedRoleIds();
            var defaults = FeatureCatalog.RoleDefaults(roles);
            var draft = ToSettings();
            var effective = FeatureCatalog.Resolve(draft);
            var viewerOnly = FeatureCatalog.IsReadOnlyRole(roles);

            IsViewerOnly = viewerOnly;
            ShowNoRoleNotice = roles is { Count: 0 };
            ShowPackNotice = roles?.Contains(RoleIds.Tuner, StringComparer.Ordinal) == true;

            foreach (var option in PrimaryOptions)
            {
                option.Refresh();
            }

            OnPropertyChanged(nameof(Choice));
            OnPropertyChanged(nameof(AddonsEnabled));
            OnPropertyChanged(nameof(PlsqlAddon));
            OnPropertyChanged(nameof(MyBatisAddon));
            OnPropertyChanged(nameof(AddonReason));
            OnPropertyChanged(nameof(HasAddonReason));

            var any = false;
            foreach (var item in Items)
            {
                var def = defaults.Contains(item.Id);
                var missing = item.Definition.Requires.FirstOrDefault(r => !effective.Contains(r));
                var writesBlocked = viewerOnly && item.Definition.WritesOnly;
                var reason = writesBlocked
                    ? _text.Lookup(FeatureTexts.WritesOnlyBlocked)
                    : missing is null ? string.Empty : string.Format(CultureInfo.CurrentCulture, _text.Lookup(FeatureTexts.RequiresFeature), NameOf(missing));
                item.Update(def, effective.Contains(item.Id), reason);
                any |= item.State != FeatureState.RoleDefault;
            }

            HasOverrides = any;
            EnabledCount = Items.Count(i => i.IsChecked);
            OverrideCount = Items.Count(i => i.HasStateText);
            AdvancedHeader = string.Format(CultureInfo.CurrentCulture, _text.Lookup(FeatureTexts.AdvancedHeader), EnabledCount, Items.Count, OverrideCount);
        }
        finally
        {
            _recomputing = false;
        }
    }

    private string NameOf(string featureId) =>
        FeatureCatalog.Find(featureId) is { } f ? _text.Lookup(f.NameKey) : featureId;

    /// <summary>지금 고른 값의 Role 목록(null = 모든 기능). 지금 조합이면 설정에 있던 목록 그대로(모르는 Role id 포함).</summary>
    private IReadOnlyList<string>? SelectedRoleIds() => PrimaryWork.RolesFor(Choice);

    /// <summary>
    /// 화면에서 고른 값을 저장 형태로 옮긴다. 모든 기능이면 allFeatures = true·roles = null(D-013), 아니면 [주 업무 Role] + 덧붙임(또는 지금 조합 그대로).
    /// Enabled = Role 기본에서 꺼져 있는데 직접 켠 것, Disabled = 기본에서 켜져 있는데 직접 끈 것.
    /// </summary>
    public FeaturesSettings ToSettings()
    {
        var roles = SelectedRoleIds();
        var defaults = FeatureCatalog.RoleDefaults(roles);
        var enabled = Items.Where(i => i.Desired == true && !defaults.Contains(i.Id)).Select(i => i.Id).Concat(_unknownEnabled);
        var disabled = Items.Where(i => i.Desired == false && defaults.Contains(i.Id)).Select(i => i.Id).Concat(_unknownDisabled);
        return new FeaturesSettings { AllFeatures = roles is null, Roles = roles is null ? null : [.. roles], Enabled = [.. enabled], Disabled = [.. disabled] };
    }

    // ---- 재시작 안내 ----

    /// <summary>저장 직후에 부른다: 안내를 다시 읽고 바뀜 표시를 지운다.</summary>
    public void AcceptSaved()
    {
        IsDirty = false;
        RefreshPending();
    }

    /// <summary>게이트가 재시작 대기 기능을 알리면 그 이름으로 안내 한 줄을 만든다.</summary>
    public void RefreshPending()
    {
        var pending = _gate?.PendingRestart;
        if (pending is null || pending.Count == 0)
        {
            RestartBannerText = string.Empty;
            return;
        }

        RestartBannerText = RestartText(_text, pending);
    }

    /// <summary>「재시작하면 적용됩니다: MyBatis 작업 공간, …」(카탈로그 순서, 모르는 id 는 그대로 뒤에). 시작 화면도 쓴다.</summary>
    internal static string RestartText(IShellText text, IReadOnlyCollection<string> pending)
    {
        var names = FeatureIds.All.Where(pending.Contains).Concat(pending.Where(p => !FeatureIds.All.Contains(p)))
            .Select(id => FeatureCatalog.Find(id) is { } f ? text.Lookup(f.NameKey) : id);
        return string.Format(CultureInfo.CurrentCulture, text.Lookup(FeatureTexts.RestartBanner), string.Join(", ", names));
    }

    /// <summary>재시작 대기 기능이 있나(저장 뒤에 창을 열어 둬 안내를 보일지 정한다).</summary>
    public bool HasPendingRestart => _gate is { PendingRestart.Count: > 0 };

    // 게이트 알림은 스레드 보장이 없다: UI 스레드로 넘긴다.
    private void OnGateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            RefreshPending();
        }
        else
        {
            Dispatcher.UIThread.Post(RefreshPending);
        }
    }

    public void Dispose()
    {
        if (_gate is not null)
        {
            _gate.Changed -= OnGateChanged;
        }
    }
}

/// <summary>주 업무 라디오 한 줄(이름·한 줄 설명). 라디오가 켜지면 모델의 <see cref="FeatureSettingsPage.Primary"/> 를 바꾼다.</summary>
public sealed class PrimaryWorkOption : ObservableObject
{
    private readonly FeatureSettingsPage _page;

    internal PrimaryWorkOption(FeatureSettingsPage page, PrimaryWorkKind kind, string name, string description)
    {
        _page = page;
        Kind = kind;
        Name = name;
        Description = description;
    }

    public PrimaryWorkKind Kind { get; }

    public string Name { get; }

    public string Description { get; }

    public bool HasDescription => Description.Length > 0;

    /// <summary>「지금 조합(그대로 두기)」 줄.</summary>
    public bool IsCustom => Kind == PrimaryWorkKind.Custom;

    /// <summary>화면 읽기 이름·툴팁: 이름 + 설명.</summary>
    public string AccessibleName => HasDescription ? $"{Name} - {Description}" : Name;

    /// <summary>라디오 값. 같은 묶음의 다른 라디오가 켜지며 false 가 들어오는 것은 무시한다(모델이 정본).</summary>
    public bool IsChecked
    {
        get => _page.Primary == Kind;
        set
        {
            if (value)
            {
                _page.Primary = Kind;
            }
            else
            {
                OnPropertyChanged();
            }
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>기능 한 개의 표시 상태. Role 기본과 같으면 <see cref="RoleDefault"/>.</summary>
public enum FeatureState
{
    RoleDefault,
    Enabled,
    Disabled,
}

/// <summary>기능 묶음 한 개(제목 + 기능 확인란들).</summary>
public sealed record FeatureGroupItem(string Name, IReadOnlyList<FeatureItem> Items);

/// <summary>기능 확인란 한 줄. 확인란 값은 「실제로 켜지는지」(조회 전용·Requires 로 못 켜면 꺼져 보이고 고칠 수 없다).</summary>
public sealed class FeatureItem : ObservableObject
{
    private readonly FeatureSettingsPage _page;
    private readonly IShellText _text;
    private bool _isChecked;
    private bool _roleDefault;
    private string _blockedReason = string.Empty;

    internal FeatureItem(FeatureSettingsPage page, FeatureDefinition definition, IShellText text, bool? desired)
    {
        _page = page;
        _text = text;
        Definition = definition;
        Name = text.Lookup(definition.NameKey);
        Desired = desired;
    }

    internal FeatureDefinition Definition { get; }

    public string Id => Definition.Id;

    public string Name { get; }

    /// <summary>사용자가 Role 기본과 다르게 정한 값. null = Role 기본을 따른다.</summary>
    internal bool? Desired { get; set; }

    /// <summary>지금 고른 Role 들의 기본 켬 여부.</summary>
    internal bool RoleDefault => _roleDefault;

    /// <summary>실제로 켜지나. 사용자가 누르면 바람이 되고, Role 기본과 같아지면 직접 표시가 지워진다.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (value != _isChecked)
            {
                _page.OnFeatureToggled(this, value);
            }
        }
    }

    public FeatureState State =>
        Desired is null || Desired == _roleDefault ? FeatureState.RoleDefault : Desired == true ? FeatureState.Enabled : FeatureState.Disabled;

    /// <summary>「직접 켬」·「직접 끔」(Role 기본이면 빈 글자). 못 켜는 기능에는 표시하지 않는다.</summary>
    public string StateText => IsBlocked ? string.Empty : State switch
    {
        FeatureState.Enabled => _text.Lookup(FeatureTexts.StateEnabled),
        FeatureState.Disabled => _text.Lookup(FeatureTexts.StateDisabled),
        _ => string.Empty,
    };

    public bool HasStateText => StateText.Length > 0;

    public bool IsStateEnabled => !IsBlocked && State == FeatureState.Enabled;

    public bool IsStateDisabled => !IsBlocked && State == FeatureState.Disabled;

    /// <summary>못 켠다(조회 전용만 고름 또는 필요한 기능이 꺼짐): 흐리게 보이고 고칠 수 없다.</summary>
    public bool IsBlocked => _blockedReason.Length > 0;

    public bool CanEdit => !IsBlocked;

    /// <summary>못 켜는 이유 한 줄. 켤 수 있으면 빈 글자.</summary>
    public string BlockedReason => _blockedReason;

    /// <summary>접근성 이름과 툴팁: 기능 이름 + 상태(또는 못 켜는 이유).</summary>
    public string Description =>
        IsBlocked ? $"{Name} - {_blockedReason}" : HasStateText ? $"{Name} - {StateText}" : $"{Name} - {_text.Lookup(FeatureTexts.StateRoleDefault)}";

    internal void Update(bool roleDefault, bool effective, string blockedReason)
    {
        _roleDefault = roleDefault;
        _isChecked = effective;
        _blockedReason = blockedReason;
        OnPropertyChanged(nameof(IsChecked));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasStateText));
        OnPropertyChanged(nameof(IsStateEnabled));
        OnPropertyChanged(nameof(IsStateDisabled));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(BlockedReason));
        OnPropertyChanged(nameof(Description));
    }
}
