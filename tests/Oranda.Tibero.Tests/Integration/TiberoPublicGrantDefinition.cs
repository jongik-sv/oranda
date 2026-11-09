namespace Oranda.Tibero.Tests.Integration;

// win144 #117: 같은 실행에서 PUBLIC 에 동시에 GRANT 하면 TBR-12033(잠금 NOWAIT 실패)이 난다. PUBLIC 권한을 건드리는 IT 는 이 모음에서 차례로 돈다.
[CollectionDefinition("TiberoPublicGrant")]
public sealed class TiberoPublicGrantDefinition;
