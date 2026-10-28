namespace Oranda.Core.Ai.Bridge;

/// <summary>
/// AI 다리(연구서 §4)를 여는 손잡이. 앱이 시작 작업에서 aiTuning 기능이 켜져 있을 때만 <see cref="Start"/> 를 부른다.
/// 닫기는 구현의 Dispose 가 맡는다. 구현은 Oranda.Platform 에 있고 플랫폼 형식은 이 인터페이스에 드러나지 않는다.
/// </summary>
public interface IAgentBridgeHost
{
    /// <summary>다리를 연다. 이미 열려 있으면 아무 일도 하지 않고 같은 파이프 이름을 돌려준다.</summary>
    string Start();
}
