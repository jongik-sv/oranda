using System;
using Oranda.Core.Features;

namespace Oranda.App.Features.ExecutionPlan;

/// <summary>
/// 계획 창의 기능 끔(TSK-07-06, D-004): <c>planAdvanced</c> 가 꺼지면 비교·그래프·보고서 보기, 고정·비교·해제 단추, 커서 옵션(형식) 단추를 숨긴다.
/// 트리·텍스트 보기와 sql_id 칸(예상·실제 계획)은 공통이라 그대로다. 화면 안 튜닝 단추(통계·자식 커서·보고서·도우미)는 명령이 레지스트리에 있을 때만 보인다
/// (레지스트리가 꺼진 기능의 명령을 거르고 <c>CommandsChanged</c> 를 내면 <see cref="RefreshCommandButtons"/> 가 바로 다시 본다).
/// </summary>
public sealed partial class PlanViewModel
{
    private IFeatureGate? _features;

    /// <summary>기능 게이트(셸은 DI 의 것, 시험은 넣는다). 없으면 모든 기능 켬.</summary>
    public IFeatureGate? Features
    {
        get => _features;
        set
        {
            if (ReferenceEquals(_features, value))
            {
                return;
            }

            if (_features is not null)
            {
                _features.Changed -= OnFeaturesChanged;
            }

            _features = value;
            if (value is not null)
            {
                value.Changed += OnFeaturesChanged;
            }

            RefreshFeatures();
        }
    }

    /// <summary>계획 고급(비교·고정·커서 옵션·그래프·SQL Monitor 보고서)을 쓸 수 있는가.</summary>
    public bool IsAdvancedEnabled => _features?.IsEnabled(FeatureIds.PlanAdvanced) ?? true;

    /// <summary>고정한 계획 표시(고급이 꺼지면 숨김 — 고정·비교 단추가 없으므로).</summary>
    public bool ShowPinnedChip => HasPinned && IsAdvancedEnabled;

    /// <summary>「튜닝 보고서…」 단추를 보일지(명령이 등록돼 있을 때만).</summary>
    public bool HasTuningReportCommand => Has(Tuning.Report.TuningReportNavigation.CommandId);

    /// <summary>게이트가 바뀌었다: 보기·단추를 다시 본다. 꺼진 고급 보기를 보고 있었으면 트리로 돌아간다.</summary>
    public void RefreshFeatures()
    {
        OnPropertyChanged(nameof(IsAdvancedEnabled));
        OnPropertyChanged(nameof(ShowPinnedChip));
        if (!IsAdvancedEnabled && SelectedTabIndex is CompareTabIndex or GraphTabIndex or ReportTabIndex)
        {
            SelectedTabIndex = 0;
        }

        if (!IsAdvancedEnabled && HasPinned)
        {
            Unpin(); // 고정·비교를 못 쓰는데 상태 줄이 비교 결과를 이어 말하지 않게 고정을 푼다
        }

        RefreshCommandButtons();
    }

    private void OnFeaturesChanged(object? sender, EventArgs e) => PostToUi(RefreshFeatures);

    partial void OnHasPinnedChanged(bool value) => OnPropertyChanged(nameof(ShowPinnedChip));
}
