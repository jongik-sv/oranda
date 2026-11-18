using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.ViewModels;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 결과 그리드 위 기능 자리(설계 §2.1 「AI 띠」): 결과 종류에 따라 기능이 입구·띠 모델을 꽂는다(예: 플래시백 VERSIONS 결과의 「차이 요약」).
/// 화면은 ViewLocator 로 그 모델의 화면을 그린다. 새 결과가 붙으면 비운다.
/// </summary>
public sealed partial class SqlResultPaneViewModel
{
    /// <summary>결과 위에 붙는 기능 모델. 없으면 null(자리를 숨긴다).</summary>
    [ObservableProperty]
    private ViewModelBase? _featureStrip;
}
