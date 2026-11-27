using Oranda.App.Features.Binds;

namespace Oranda.App.Tests.TestSupport;

/// <summary>시험용: 바인드 목록의 그 이름 줄(탭을 열 때·접속을 정할 때 미리 채운 줄이 있으면 그것, 없으면 새로 더한 줄).</summary>
internal static class BindRows
{
    public static BindVariableRow Row(this BindVariableStore store, string name) => store.Find(name) ?? store.Add(name);
}
