using System.Globalization;
using Oranda.Core.Execution.Providers;

namespace Oranda.Tibero.Execution;

/// <summary>
/// Tibero 의 <see cref="IDdlSource"/> 자리(E5, D4). T2 는 DDL 직전 이전 DDL 을 <b>보관하지 않는다</b> — 서버에서 아무것도 읽지 않고,
/// <see cref="ChangeCapture.CaptureAsync"/> 가 DDL 한 건마다 실패 행 하나(<see cref="Reason"/>)를 일지에 남기게 한다.
/// 「보관했어야 하는데 조용히 빠짐」이 아니라 「보관 안 함 + 이유」가 일지에 보인다. 실제 읽기(DBMS_METADATA 대응)는 T3 이 이 클래스를 바꾼다.
/// </summary>
internal sealed class TiberoDdlSource : IDdlSource
{
    /// <summary>일지 실패 행 사유. 사용자에게 그대로 보인다.</summary>
    internal const string Reason = "보관 안 함: Tibero 의 DDL 직전 이전 DDL 보관은 아직 지원하지 않는다(다음 단계에서 지원)";

    /// <summary>「보관 안 함」 표지. 서버 번호가 아니며 <see cref="ObjectNotFound"/> 와 달라야 실패 행이 남는다.</summary>
    internal const int NotCaptured = int.MinValue + 1;

    /// <summary>「객체 없음」 표지. 서버 번호가 아니다. 선택 읽기(PACKAGE·TYPE 본문)에만 돌려줘 DDL 한 건에 행이 하나만 남게 한다.</summary>
    internal const int ObjectNotFound = int.MinValue;

    /// <summary>문장에 종류가 없을 때(RENAME) 쓰는 일지 객체 종류.</summary>
    internal const string UnknownType = "OBJECT";

    public static TiberoDdlSource Instance { get; } = new();

    private TiberoDdlSource()
    {
    }

    public int ObjectNotFoundNumber => ObjectNotFound;

    /// <summary>서버를 묻지 않는다. null 을 주면 「객체를 찾지 못했다」로 잘못 남으므로 <see cref="UnknownType"/> 으로 넘긴다.</summary>
    public Task<string?> ResolveTypeAsync(string? owner, string name, CancellationToken ct) => Task.FromResult<string?>(UnknownType);

    /// <summary>늘 「보관 안 함」. 본문(<c>*_BODY</c>, 선택 읽기)은 「객체 없음」으로 건너뛰어 명세 행 하나만 남긴다.</summary>
    public Task<DdlRead> ReadAsync(string getDdlType, string? owner, string name, CancellationToken ct)
        => Task.FromResult(getDdlType.EndsWith("_BODY", StringComparison.Ordinal)
            ? new DdlRead(null, owner, ObjectNotFound)
            : new DdlRead(null, owner, NotCaptured, Reason));

    public string DescribeError(int number, string? message)
        => number == NotCaptured
            ? Reason
            : ("TBR-" + Math.Abs((long)number).ToString(CultureInfo.InvariantCulture) + " " + message).TrimEnd();
}
