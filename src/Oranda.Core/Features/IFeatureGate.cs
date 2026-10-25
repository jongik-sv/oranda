namespace Oranda.Core.Features;

/// <summary>
/// 기능 게이트(TRD 10.4). 지금 켜진 기능과 등록 항목의 켬 여부를 답한다. 구현 = App <c>Shell/Features/FeatureGate</c>
/// (TSK-07-01 은 모두 켬 틀, TSK-07-02 가 TRD 10.5 해석). 시험은 <c>Oranda.Testing.FakeFeatureGate</c>.
/// 꼬리표 규칙은 <see cref="FeatureCatalog.FeatureFor(string?, Type)"/>, 해석 1~4단계는 <see cref="FeatureCatalog.Resolve"/>.
/// </summary>
public interface IFeatureGate
{
    /// <summary>지금 실제로 켜졌나(TRD 10.5). 모르는 id 는 켬으로 본다(공통 취급).</summary>
    bool IsEnabled(string featureId);

    /// <summary>명령이 켜졌나. <paramref name="handlerType"/> = <c>[OrandaCommand]</c> 처리기 형.</summary>
    bool IsCommandEnabled(string commandId, Type handlerType);

    /// <summary>도구 창·문서 패널이 켜졌나. <paramref name="viewModelType"/> = 패널 ViewModel 형.</summary>
    bool IsPanelEnabled(string panelId, Type viewModelType);

    /// <summary>상태 칸·시작 작업이 켜졌나. <paramref name="registeredType"/> = 항목 형·구현 형.</summary>
    bool IsTypeEnabled(Type registeredType);

    /// <summary>Role 이 조회 전용 하나뿐(D-006). 참이면 정책 관문이 쓰기 문장을 거부하고 <c>readOnlySession</c> 키가 참이 된다.</summary>
    bool ReadOnlyRole { get; }

    /// <summary>재시작해야 반영되는 기능 id(StartupBound 기능의 시작 때 값과 지금 값이 다를 때).</summary>
    IReadOnlyCollection<string> PendingRestart { get; }

    /// <summary>다시 해석한 뒤 낸다. 스레드 보장 없음 — 구독자가 UI 스레드로 넘긴다.</summary>
    event EventHandler? Changed;
}
