using Oranda.Core.Connection;

namespace Oranda.Core.Transfer.Copy;

/// <summary>대상 접속 보호. 운영 접속에는 쓰지 않고 우회 설정도 두지 않는다(설계 4장).</summary>
public static class CopyTargetGuard
{
    public static bool IsAllowedTarget(ConnectionProfile profile) => profile.Environment != ConnectionEnvironment.Production;

    /// <summary><c>DROP TABLE</c>·<c>CREATE TABLE</c> 은 개발 접속에만 허용한다.</summary>
    public static bool CanRecreate(ConnectionProfile profile) => profile.Environment == ConnectionEnvironment.Development;

    public static void EnsureAllowedTarget(ConnectionProfile profile)
    {
        if (!IsAllowedTarget(profile))
            throw new InvalidOperationException($"운영 접속 '{profile.Name}' 은 복사 대상으로 쓸 수 없다.");
    }
}
