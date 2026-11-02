using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 실행 계획이 SQL 탭마다 따로 있어도(탭마다 <see cref="PlanViewModel"/> 하나) 앱 전체가 함께 쓰는 값:
/// DBMS_XPLAN 형식 옵션 5개, 트리 열 폭, 비교 기준으로 고정한 계획. 탭 A 에서 고정하고 탭 B 의 계획과 비교하는 것이 튜닝 전후 비교의 용도다.
/// 고정한 계획은 계획 사본이라 그 계획을 만든 탭이 닫혀도 남는다.
/// </summary>
public sealed partial class PlanShared : ObservableObject
{
    [ObservableProperty]
    private string _baseFormat = "ALLSTATS";

    [ObservableProperty]
    private bool _lastOnly = true;

    [ObservableProperty]
    private bool _peekedBinds;

    [ObservableProperty]
    private bool _alias;

    [ObservableProperty]
    private bool _outline;

    /// <summary>트리 표 칸 폭. 서비스로 만들면 앱 데이터 폴더의 <c>plan-columns.json</c> 에 남고, 아니면 메모리만 쓴다.</summary>
    public PlanColumnWidths Columns { get; init; } = new();

    /// <summary>비교 기준으로 고정한 계획(없으면 null).</summary>
    public CorePlan? PinnedPlan { get; private set; }

    /// <summary>고정한 계획 설명(어느 탭의 계획인지 포함, 없으면 빈 문자열).</summary>
    public string PinnedText { get; private set; } = string.Empty;

    public bool HasPinned => PinnedPlan is not null;

    /// <summary>고정이 바뀌었다(고정·해제). 탭마다 비교를 다시 하라는 신호.</summary>
    public event EventHandler? PinChanged;

    public void Pin(CorePlan plan, string text)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PinnedPlan = plan;
        PinnedText = text;
        PinChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Unpin()
    {
        PinnedPlan = null;
        PinnedText = string.Empty;
        PinChanged?.Invoke(this, EventArgs.Empty);
    }
}
