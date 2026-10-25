using System;
using System.Collections.Generic;
using Oranda.Core.Features;

namespace Oranda.App.Shell.Features;

/// <summary>
/// <see cref="IFeatureGate"/> 의 틀(TSK-07-01): 모든 기능 켬, 조회 전용 아님, 재시작 대기 없음, <see cref="Changed"/> 를 내지 않는다.
/// 그래서 게이트를 등록해도 지금 동작이 그대로다. 실제 해석(TRD 10.5)·꼬리표 캐시·<c>SettingsChanged("features")</c> 구독·
/// <c>feature.&lt;id&gt;</c> 문맥 키는 TSK-07-02 가 이 파일을 바꿔 넣는다(해석 1~4단계는 <see cref="FeatureCatalog.Resolve"/>).
/// </summary>
public sealed class FeatureGate : IFeatureGate
{
    private static readonly IReadOnlyCollection<string> NoPending = [];

    public bool ReadOnlyRole => false;

    public IReadOnlyCollection<string> PendingRestart => NoPending;

    // 틀은 바뀌지 않으므로 구독만 받고 내지 않는다.
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public bool IsEnabled(string featureId)
    {
        ArgumentNullException.ThrowIfNull(featureId);
        return true;
    }

    public bool IsCommandEnabled(string commandId, Type handlerType)
    {
        ArgumentNullException.ThrowIfNull(commandId);
        ArgumentNullException.ThrowIfNull(handlerType);
        return true;
    }

    public bool IsPanelEnabled(string panelId, Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(panelId);
        ArgumentNullException.ThrowIfNull(viewModelType);
        return true;
    }

    public bool IsTypeEnabled(Type registeredType)
    {
        ArgumentNullException.ThrowIfNull(registeredType);
        return true;
    }
}
