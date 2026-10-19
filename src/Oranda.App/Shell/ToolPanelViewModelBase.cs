using Oranda.App.ViewModels;

namespace Oranda.App.Shell;

/// <summary>
/// 도구 창·문서 ViewModel 의 공통 바탕. <see cref="Oranda.Core.Shell.OrandaToolPanelAttribute"/> 를 단 클래스가 상속한다.
/// View 는 이름 규칙(<c>…ViewModel</c> → <c>…View</c>, 같은 네임스페이스)으로 <see cref="ViewLocator"/> 가 찾는다.
/// </summary>
public abstract class ToolPanelViewModelBase : ViewModelBase
{
}
