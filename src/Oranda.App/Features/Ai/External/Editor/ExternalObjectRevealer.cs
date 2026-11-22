using Oranda.App.Features.Navigation;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Features.Ai.External.Editor;

/// <summary><c>reveal_object</c> 의 결과: 스키마 브라우저 트리에서 찾았는가, 객체를 열었는가.</summary>
public sealed record ExternalRevealResult(bool Found, bool Opened);

/// <summary>
/// <c>reveal_object</c> 의 안쪽(화면 등급): 객체를 스키마 브라우저 트리에서 고르고, 원하면 연다(표는 상세, PL/SQL 은 소스).
/// 이름은 부르는 쪽이 게이트의 이름 풀기로 실제 <c>OWNER.NAME</c> 을 정해 넘긴다(허가 범위 확인도 거기서). 화면만 바뀌고 글·DB 는 그대로다. UI 스레드에서 부른다.
/// </summary>
public sealed class ExternalObjectRevealer
{
    private readonly ObjectNavigationCommandHandler _navigation;

    public ExternalObjectRevealer(IServiceProvider services)
        : this(new ObjectNavigationCommandHandler(services ?? throw new ArgumentNullException(nameof(services))))
    {
    }

    /// <summary>처리기를 정해 쓴다(시험).</summary>
    internal ExternalObjectRevealer(ObjectNavigationCommandHandler navigation)
    {
        _navigation = navigation ?? throw new ArgumentNullException(nameof(navigation));
    }

    public async Task<ExternalRevealResult> RevealAsync(DbObjectRef target, ISession session, bool open, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(session);
        var found = await _navigation.RevealAsync(target, session, ct).ConfigureAwait(true);
        if (!open)
        {
            return new ExternalRevealResult(found, Opened: false);
        }

        await _navigation.OpenAsync(target, session, ct).ConfigureAwait(true);
        return new ExternalRevealResult(found, Opened: true);
    }
}
