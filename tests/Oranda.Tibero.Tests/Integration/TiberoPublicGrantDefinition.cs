namespace Oranda.Tibero.Tests.Integration;

// win144 #117: 같은 실행에서 PUBLIC 에 동시에 GRANT 하면 TBR-12033(잠금 NOWAIT 실패)이 난다. PUBLIC 권한을 건드리는 IT 는 이 모음에서 차례로 돈다.
// 컬렉션끼리는 기본이 병렬이라 컬렉션만으로는 다른 IT 와 부딪힌다(전체 실행에서 4건 실패). DisableParallelization 으로 이 모음이 다른 컬렉션과도 겹치지 않게 한다.
[CollectionDefinition("TiberoPublicGrant", DisableParallelization = true)]
public sealed class TiberoPublicGrantDefinition;
