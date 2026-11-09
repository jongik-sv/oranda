using Oranda.Core.Connection;

namespace Oranda.Tibero.Driver;

/// <summary>드라이버를 쓸 수 없을 때(지정 안 함·파일 없음·검사 거절·멤버 묶기 실패). <see cref="Exception.Message"/> 는 사용자에게 보일 문장이다.</summary>
internal class TiberoDriverUnavailableException : InvalidOperationException
{
    public TiberoDriverUnavailableException(DriverCheck check)
        : base((check ?? throw new ArgumentNullException(nameof(check))).Reason ?? TiberoDriver.NotSpecifiedMessage)
    {
        Check = check;
    }

    /// <summary>못 쓰는 이유의 검사 결과(<see cref="DriverCheck.IsUsable"/> 은 늘 false).</summary>
    public DriverCheck Check { get; }
}

/// <summary>드라이버는 읽혔으나 어댑터가 필요한 멤버가 없을 때.</summary>
internal sealed class TiberoDriverBindException : TiberoDriverUnavailableException
{
    public TiberoDriverBindException(IReadOnlyList<string> missingMembers, string? version = null, string? path = null)
        : base(CreateCheck(missingMembers, version, path))
    {
        MissingMembers = missingMembers;
    }

    /// <summary>없는 멤버 이름(<c>TiberoCommand.BindByName</c> 꼴) 전부.</summary>
    public IReadOnlyList<string> MissingMembers { get; }

    private static DriverCheck CreateCheck(IReadOnlyList<string> missingMembers, string? version, string? path)
    {
        ArgumentNullException.ThrowIfNull(missingMembers);

        return new DriverCheck(
            DriverState.Unusable,
            version,
            path,
            $"이 드라이버는 쓸 수 없습니다(없는 멤버: {string.Join(", ", missingMembers)})");
    }
}
