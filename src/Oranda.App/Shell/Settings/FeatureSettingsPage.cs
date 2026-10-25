using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Features;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 창 「기능」 탭의 모델(TSK-07-04, PRD 12, TRD 10.7.6): Role 6개 고르기·기능 25개 세부·기본값으로 되돌리기·재시작 안내.
/// 화면 문구와 묶음·기능 목록은 모두 <see cref="FeatureCatalog"/>·<see cref="FeatureTexts"/> 에서 만든다(하드코딩 없음).
/// 확인란 상태는 <see cref="FeatureCatalog.Resolve"/> 와 같은 규칙(Role 기본 + 직접 켬 − 직접 끔, 조회 전용·Requires 로 못 켜는 것은 끔)이고,
/// 저장 값(<see cref="ToSettings"/>)은 Role 기본값과 다른 기능만 Enabled/Disabled 에 남긴다. 알 수 없는 id 는 지우지 않고 그대로 돌려 쓴다.
/// </summary>
public sealed partial class FeatureSettingsPage : ObservableObject, IDisposable
{
    private readonly IShellText _text;
    private readonly IFeatureGate? _gate;
    private readonly FeaturesSettings _initial;
    private readonly List<string> _unknownRoles;
    private readonly List<string> _unknownEnabled;
    private readonly List<string> _unknownDisabled;
    private bool _recomputing;

    /// <param name="initial">설정 창을 열 때의 <c>features</c> 섹션.</param>
    /// <param name="text">화면 글자 조회.</param>
    /// <param name="gate">재시작 안내(<see cref="IFeatureGate.PendingRestart"/>)를 읽을 게이트. null 이면 안내를 내지 않는다.</param>
    public FeatureSettingsPage(FeaturesSettings? initial, IShellText text, IFeatureGate? gate = null)
    {
        _text = text;
        _gate = gate;
        _initial = initial ?? new FeaturesSettings();

        // 아무것도 고른 적 없으면 기본 Role(SQL 개발자)이 골라진 「Role 로 고르기」로 보인다(D-013). 손대지 않고 저장하면 설정은 그대로다.
        var effective = FeatureCatalog.EffectiveRoles(_initial);
        _useRoles = effective is not null;
        IsDefaultRole = FeatureCatalog.IsUnchosen(_initial);
        _showDefaultRoleNotice = IsDefaultRole;

        var roles = effective ?? _initial.Roles ?? [];
        _unknownRoles = [.. roles.Where(r => !FeatureCatalog.IsKnownRole(r)).Distinct(StringComparer.Ordinal)];
        _unknownEnabled = [.. _initial.Enabled.Where(id => !FeatureCatalog.IsKnownFeature(id)).Distinct(StringComparer.Ordinal)];
        _unknownDisabled = [.. _initial.Disabled.Where(id => !FeatureCatalog.IsKnownFeature(id)).Distinct(StringComparer.Ordinal)];

        Roles = [.. FeatureCatalog.Roles.Select(r => new FeatureRoleItem(this, r, text, roles.Contains(r.Id, StringComparer.Ordinal)))];

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

    // ---- 글자 ----

    public string TabTitle => _text.Lookup(FeatureTexts.SettingsTab);

    public string RolesTitle => _text.Lookup(FeatureTexts.SettingsRoles);

    public string DetailsTitle => _text.Lookup(FeatureTexts.SettingsDetails);

    public string ModeAllText => _text.Lookup(FeatureTexts.ModeAll);

    public string ModeRolesText => _text.Lookup(FeatureTexts.ModeRoles);

    public string ResetText => _text.Lookup(FeatureTexts.ResetToRoleDefaults);

    public string ViewerNoticeText => _text.Lookup(FeatureTexts.ViewerNotice);

    public string PackNoticeText => _text.Lookup(FeatureTexts.PackNotice);

    public string NoRoleNoticeText => _text.Lookup(FeatureTexts.NoRoleNotice);

    public string DefaultRoleNoticeText =>
        string.Format(CultureInfo.CurrentCulture, _text.Lookup(FeatureTexts.DefaultRoleNotice), DefaultRoleName(_text));

    /// <summary>기본 Role(D-013) 의 화면 이름. 시작 화면 「지금 … 메뉴입니다」 줄도 쓴다.</summary>
    internal static string DefaultRoleName(IShellText text) =>
        string.Join(", ", FeatureCatalog.DefaultRoles.Select(id => FeatureCatalog.FindRole(id) is { } r ? text.Lookup(r.NameKey) : id));

    // ---- 상태 ----

    /// <summary>Role 확인란 6개(<see cref="FeatureCatalog.Roles"/> 순서).</summary>
    public IReadOnlyList<FeatureRoleItem> Roles { get; }

    /// <summary>기능 묶음 5개(<see cref="FeatureGroup"/> 순서).</summary>
    public IReadOnlyList<FeatureGroupItem> Groups { get; }

    /// <summary>기능 25개(묶음 순서로 펼친 목록).</summary>
    public IReadOnlyList<FeatureItem> Items { get; }

    /// <summary>true = 「Role 로 고르기」, false = 「모든 기능 쓰기」(저장하면 <c>allFeatures</c> = true, <c>roles</c> = null).</summary>
    [NotifyPropertyChangedFor(nameof(UseAllFeatures), nameof(RolesEditable))]
    [ObservableProperty]
    private bool _useRoles;

    /// <summary>「모든 기능 쓰기」 라디오(<see cref="UseRoles"/> 의 반대).</summary>
    public bool UseAllFeatures
    {
        get => !UseRoles;
        set
        {
            if (value)
            {
                UseRoles = false;
            }
        }
    }

    /// <summary>창을 열 때 아무것도 고른 적 없어 기본 Role(SQL 개발자)을 보이는 중이었다(D-013, 시험·화면용).</summary>
    public bool IsDefaultRole { get; }

    /// <summary>기본 Role 을 보이는 중이라는 안내 띠(D-013). 무엇이든 바꾸면 사라진다(저장하면 직접 고른 것이 된다).</summary>
    [ObservableProperty]
    private bool _showDefaultRoleNotice;

    /// <summary>Role 확인란을 고칠 수 있나(Role 로 고르기일 때).</summary>
    public bool RolesEditable => UseRoles;

    /// <summary>조회 전용 Role 하나만 골랐다: 탭 위 안내와 쓰기 전용 기능 흐림.</summary>
    [ObservableProperty]
    private bool _isViewerOnly;

    /// <summary>「Role 로 고르기」인데 Role 을 하나도 안 골랐다: 공통 기능만 켜진다는 한 줄(D-4).</summary>
    [ObservableProperty]
    private bool _showNoRoleNotice;

    /// <summary>튜너 Role 을 골랐다: 라이선스 팩 선언 안내 한 줄(D-012).</summary>
    [ObservableProperty]
    private bool _showPackNotice;

    /// <summary>기본값과 다르게 직접 켜거나 끈 기능이 있다(「되돌리기」를 켠다).</summary>
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    [ObservableProperty]
    private bool _hasOverrides;

    /// <summary>재시작해야 적용되는 기능 안내 한 줄. 없으면 빈 글자.</summary>
    [NotifyPropertyChangedFor(nameof(HasRestartBanner))]
    [ObservableProperty]
    private string _restartBannerText = string.Empty;

    public bool HasRestartBanner => RestartBannerText.Length > 0;

    /// <summary>이 탭에서 사용자가 무엇이든 바꿨다(안 바꿨으면 저장 때 설정을 건드리지 않는다).</summary>
    public bool IsDirty { get; private set; }

    partial void OnUseRolesChanged(bool value)
    {
        MarkDirty();
        Recompute();
    }

    // ---- 사용자 동작 ----

    internal void OnRoleToggled()
    {
        MarkDirty();
        Recompute();
    }

    /// <summary>기능 확인란을 눌렀다: 바람이 Role 기본과 같아지면 직접 표시를 지운다(최소 기록).</summary>
    internal void OnFeatureToggled(FeatureItem item, bool wanted)
    {
        item.Desired = wanted == item.RoleDefault ? null : wanted;
        MarkDirty();
        Recompute();
    }

    /// <summary>모든 기능의 직접 켬·끔을 지우고 Role 기본값으로 되돌린다(고른 Role 은 그대로, 이 버전이 모르는 id 도 그대로).</summary>
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
            ShowPackNotice = UseRoles && Roles.Any(r => r.Id == RoleIds.Tuner && r.IsChecked);

            foreach (var role in Roles)
            {
                role.Refresh();
            }

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
        }
        finally
        {
            _recomputing = false;
        }
    }

    private string NameOf(string featureId) =>
        FeatureCatalog.Find(featureId) is { } f ? _text.Lookup(f.NameKey) : featureId;

    private List<string>? SelectedRoleIds() =>
        UseRoles ? [.. Roles.Where(r => r.IsChecked).Select(r => r.Id), .. _unknownRoles] : null;

    /// <summary>
    /// 화면에서 고른 값을 저장 형태로 옮긴다. 「모든 기능 쓰기」면 allFeatures = true·roles = null(D-013), 아니면 고른 Role(카탈로그 순서).
    /// Enabled = Role 기본에서 꺼져 있는데 직접 켠 것, Disabled = 기본에서 켜져 있는데 직접 끈 것.
    /// </summary>
    public FeaturesSettings ToSettings()
    {
        var roles = SelectedRoleIds();
        var defaults = FeatureCatalog.RoleDefaults(roles);
        var enabled = Items.Where(i => i.Desired == true && !defaults.Contains(i.Id)).Select(i => i.Id).Concat(_unknownEnabled);
        var disabled = Items.Where(i => i.Desired == false && defaults.Contains(i.Id)).Select(i => i.Id).Concat(_unknownDisabled);
        return new FeaturesSettings { AllFeatures = roles is null, Roles = roles, Enabled = [.. enabled], Disabled = [.. disabled] };
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

        var names = FeatureIds.All.Where(pending.Contains).Concat(pending.Where(p => !FeatureIds.All.Contains(p))).Select(NameOf);
        RestartBannerText = string.Format(CultureInfo.CurrentCulture, _text.Lookup(FeatureTexts.RestartBanner), string.Join(", ", names));
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

/// <summary>기능 한 개의 표시 상태. Role 기본과 같으면 <see cref="RoleDefault"/>.</summary>
public enum FeatureState
{
    RoleDefault,
    Enabled,
    Disabled,
}

/// <summary>Role 확인란 한 줄(이름·한 줄 설명).</summary>
public sealed class FeatureRoleItem : ObservableObject
{
    private readonly FeatureSettingsPage _page;
    private bool _isChecked;

    internal FeatureRoleItem(FeatureSettingsPage page, RoleDefinition role, IShellText text, bool isChecked)
    {
        _page = page;
        Id = role.Id;
        Name = text.Lookup(role.NameKey);
        Description = text.Lookup(role.DescriptionKey);
        _isChecked = isChecked;
    }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _page.OnRoleToggled();
            }
        }
    }

    /// <summary>Role 로 고르기일 때만 고칠 수 있다.</summary>
    public bool CanEdit => _page.UseRoles;

    internal void Refresh() => OnPropertyChanged(nameof(CanEdit));
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
