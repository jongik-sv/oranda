using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.Access;

/// <summary>접속 목록 한 줄: 이름·환경 라벨·켬/끔 스위치·설명.</summary>
public sealed partial class AccessRow : ObservableObject
{
    private readonly AccessText _t;

    internal AccessRow(ConnectionProfile profile, AccessText text)
    {
        _profile = profile;
        _t = text;
        _isOn = AccessChoice.IsOn(profile);
    }

    /// <summary>마지막으로 읽거나 저장한 접속.</summary>
    public ConnectionProfile Profile
    {
        get => _profile;
        internal set
        {
            _profile = value;
            OnPropertyChanged(nameof(IsChanged));
        }
    }

    private ConnectionProfile _profile;

    public string Name => Profile.Name;

    public bool IsProduction => Profile.Environment == ConnectionEnvironment.Production;

    public string EnvironmentLabel => _t.Environment(Profile.Environment);

    public string SwitchName => _t.SwitchName(Name);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Description), nameof(IsChanged))]
    private bool _isOn;

    public string Description => !IsOn ? _t.OffDescription : IsProduction ? _t.ProductionDescription : _t.OnDescription;

    /// <summary>스위치가 저장된 상태와 다르다.</summary>
    public bool IsChanged => IsOn != AccessChoice.IsOn(Profile);

    internal void Refresh()
    {
        OnPropertyChanged(nameof(EnvironmentLabel));
        OnPropertyChanged(nameof(SwitchName));
        OnPropertyChanged(nameof(Description));
    }
}
