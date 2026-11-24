using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>수준 단추 하나(끔·메타데이터·계획·조회 실행·조회+행 값). 운영의 「조회+행 값」 처럼 고를 수 없으면 꺼져 있다(점선).</summary>
public sealed partial class AccessLevelButton : ObservableObject
{
    internal AccessLevelButton(AccessRow row, AiAccessLevel level)
    {
        Row = row;
        Level = level;
    }

    public AccessRow Row { get; }

    public AiAccessLevel Level { get; }

    public string Label => Row.Text.LevelName(Level);

    /// <summary>이 환경에서 고를 수 있다.</summary>
    public bool IsEnabled => Level <= AiAccessPolicy.MaxLevel(Row.Profile.Environment);

    /// <summary>지금 고른 수준.</summary>
    public bool IsSelected => Row.Level == Level;

    /// <summary>화면 읽기 이름(「dev-erp 계획」).</summary>
    public string AutomationName => Row.Name + " " + Label;

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>
/// 「AI 가 볼 수 있는 접속」 한 줄(ai-settings-one-place 2026-11-24): 이름·환경 표·수준 단추 5개. 설정 「AI」 탭 ①과 도우미 「AI 를 쓸 접속」 단계가 같은 줄을 쓴다.
/// 값은 접속의 AI 허용 수준 하나(Oranda 안의 AI·밖의 AI 에이전트가 함께 따른다). 운영 접속의 수준을 직접 올리면 접속 이름을 다시 입력해야 저장된다(접속 편집과 같은 규칙).
/// 예전 켬/끔(<see cref="IsOn"/>)은 그대로 쓸 수 있다: 켬 = 환경 기본 수준, 끔 = 끔.
/// </summary>
public sealed partial class AccessRow : ObservableObject
{
    internal AccessRow(ConnectionProfile profile, AccessText text)
    {
        _profile = profile;
        Text = text;
        _level = AiAccessPolicy.Effective(profile);
        _maskSensitive = AiAccessPolicy.MasksSensitiveColumns(profile);
        Buttons = [.. new[] { AiAccessLevel.Off, AiAccessLevel.Metadata, AiAccessLevel.Plan, AiAccessLevel.Query, AiAccessLevel.QueryRows }.Select(l => new AccessLevelButton(this, l))];
    }

    internal AccessText Text { get; }

    /// <summary>마지막으로 읽거나 저장한 접속.</summary>
    public ConnectionProfile Profile
    {
        get => _profile;
        internal set
        {
            _profile = value;
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(NeedsName));
            OnPropertyChanged(nameof(SavedLevel));
            OnPropertyChanged(nameof(SavedMaskSensitive));
        }
    }

    private ConnectionProfile _profile;

    public string Name => Profile.Name;

    public bool IsProduction => Profile.Environment == ConnectionEnvironment.Production;

    public string EnvironmentLabel => Text.Environment(Profile.Environment);

    public string SwitchName => Text.SwitchName(Name);

    /// <summary>수준 단추 5개(끔부터 조회+행 값까지).</summary>
    public IReadOnlyList<AccessLevelButton> Buttons { get; }

    /// <summary>운영 줄 옆 한 줄(「운영은 「조회 실행」까지」). 그 밖은 빈 글.</summary>
    public string LimitNote => IsProduction ? Text.ProductionLimit : string.Empty;

    /// <summary>화면에서 고른 수준. 저장된 값은 <see cref="SavedLevel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOn), nameof(Description), nameof(IsChanged), nameof(NeedsName))]
    private AiAccessLevel _level;

    /// <summary>운영 수준을 직접 올릴 때 다시 입력한 접속 이름.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsName))]
    private string _confirmName = string.Empty;

    /// <summary>
    /// 민감 데이터 가리기(D-069, 모든 줄의 확인란 — 개발·시험도 운영 데이터를 복사해 쓸 수 있다): 켜면 전화·이메일·이름 같은 민감 컬럼 값을 가려서 보낸다. 끄면 비밀번호·주민번호·카드·계좌·주소·인증값만 가린다.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(IsChanged), nameof(NeedsName), nameof(MaskOffWarningVisible))]
    private bool _maskSensitive;

    /// <summary>저장된 민감 가림 값.</summary>
    public bool SavedMaskSensitive => AiAccessPolicy.MasksSensitiveColumns(Profile);

    public string MaskSensitiveLabel => Text.MaskSensitiveLabel;

    public string MaskOffWarning => Text.MaskOffWarning;

    /// <summary>가림을 끈 상태(경고 글을 보인다).</summary>
    public bool MaskOffWarningVisible => !MaskSensitive;

    /// <summary>저장된 실효 수준.</summary>
    public AiAccessLevel SavedLevel => AiAccessPolicy.Effective(Profile);

    /// <summary>
    /// 켬/끔(도우미의 예전 스위치·시험): 켬 = 끔이 아님. 끄면 끔, 켜면 저장된 수준(저장된 값이 끔이면 환경 기본 수준) — 껐다 다시 켜도 직접 고른 수준이 남는다.
    /// </summary>
    public bool IsOn
    {
        get => Level != AiAccessLevel.Off;
        set
        {
            if (value != IsOn)
            {
                Level = !value ? AiAccessLevel.Off
                    : SavedLevel != AiAccessLevel.Off ? SavedLevel
                    : AiAccessPolicy.DefaultLevel(Profile.Environment);
            }
        }
    }

    public string Description => Text.LevelDescription(Level, IsProduction, MaskSensitive);

    /// <summary>고른 수준이나 민감 가림이 저장된 값과 다르다.</summary>
    public bool IsChanged => Level != SavedLevel || MaskSensitive != SavedMaskSensitive;

    /// <summary>저장하려면 접속 이름을 다시 입력해야 한다(운영 수준을 직접 올림, 이름이 아직 맞지 않음).</summary>
    public bool NeedsName => Level != SavedLevel && AiConnectionAccessRules.Check(Profile, Level, ConfirmName) == AiAccessChange.NeedsNameConfirmation;

    public string ConfirmPlaceholder => Text.ConfirmPlaceholder(Name);

    /// <summary>수준 단추를 눌렀다(고를 수 없는 수준은 무시).</summary>
    [RelayCommand]
    private void Choose(AiAccessLevel level)
    {
        if (level <= AiAccessPolicy.MaxLevel(Profile.Environment))
        {
            Level = level;
        }
    }

    partial void OnLevelChanged(AiAccessLevel value)
    {
        foreach (var button in Buttons)
        {
            button.Refresh();
        }
    }

    /// <summary>다른 곳에서 저장된 값으로 맞춘다(바꾸는 중이 아닐 때).</summary>
    internal void Reset(ConnectionProfile saved)
    {
        Profile = saved;
        Level = AiAccessPolicy.Effective(saved);
        MaskSensitive = AiAccessPolicy.MasksSensitiveColumns(saved);
        ConfirmName = string.Empty;
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(EnvironmentLabel));
        OnPropertyChanged(nameof(SwitchName));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(LimitNote));
        OnPropertyChanged(nameof(ConfirmPlaceholder));
        OnPropertyChanged(nameof(MaskSensitiveLabel));
        OnPropertyChanged(nameof(MaskOffWarning));
        foreach (var button in Buttons)
        {
            button.Refresh();
        }
    }
}
