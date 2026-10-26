using Oranda.App.Shell.Settings;
using Oranda.Core.Grid;

namespace Oranda.App.Features.ResultGrid;

/// <summary>
/// 설정 <c>grid.pageSize</c>·<c>grid.initialLobFetchSize</c> 를 실행 서비스에 알린다(<see cref="IGridFetchSettings"/>). 읽는 순간의 현재 설정이라
/// 설정을 바꾸면 다음 실행부터 따라간다(앱을 다시 켤 필요가 없다).
/// </summary>
public sealed class GridFetchSettings(SettingsService settings) : IGridFetchSettings
{
    public int PageSize => settings.Current.Grid.PageSize;

    public int InitialLobFetchSize => settings.Current.Grid.InitialLobFetchSize;
}
