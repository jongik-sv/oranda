using Oranda.Core.Composition;

[assembly: OrandaServiceAssembly]

namespace Oranda.Core;

/// <summary>
/// 어셈블리 표지. 서비스 자동 수집이 이 어셈블리를 찾을 때 쓴다 — Core 가 내는 서비스는 DBMS 디스패처
/// (<c>Oranda.Core.Composition.Dispatch</c>, D-046)뿐이다. 앱 조립은 이 어셈블리를 바로(지연 없이) 훑는다.
/// </summary>
public static class CoreAssembly
{
}
