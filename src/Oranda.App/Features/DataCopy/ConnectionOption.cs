using Oranda.Core.Connection;

namespace Oranda.App.Features.DataCopy;

/// <summary>접속 고르기 목록의 한 줄. 운영 접속은 대상으로는 비활성이고 이유를 툴팁으로 알린다(설계 10장).</summary>
public sealed record ConnectionOption(ConnectionProfile Profile, bool IsEnabled, string? Tooltip)
{
    public string Display => $"{Profile.Name} ({Profile.User})";
}
