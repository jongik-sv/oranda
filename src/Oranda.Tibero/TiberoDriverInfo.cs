using System.Runtime.CompilerServices;

namespace Oranda.Tibero;

/// <summary>
/// 이 빌드에 Tibero 드라이버(TDP.NET)가 들어 있고 읽을 수 있는지. 드라이버가 없는 빌드(공개 배포본, CI)에서도 이 형식은 있다.
/// 드라이버 의존 코드는 <c>#if TIBERO_DRIVER</c> 안에만 둔다. 드라이버 있는 빌드의 <c>Oranda.Tibero.dll</c> 만 드라이버 없이 옮겨져도
/// 예외 없이 「없음」 으로 답한다.
/// </summary>
public static class TiberoDriverInfo
{
    /// <summary>드라이버 어셈블리 버전(예: 1.1.35.0). 드라이버가 없거나 읽을 수 없으면 null.</summary>
    public static string? Version { get; } = ReadVersion();

    /// <summary>드라이버를 읽을 수 있다.</summary>
    public static bool IsLinked => Version is not null;

    private static string? ReadVersion()
    {
#if TIBERO_DRIVER
        try
        {
            return DriverVersion();
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null; // 드라이버 DLL 이 옆에 없다.
        }
#else
        return null;
#endif
    }

#if TIBERO_DRIVER
    // 드라이버 형식을 쓰는 코드는 이 메서드 안에만 둔다 — JIT 가 이 메서드를 컴파일할 때 어셈블리를 읽으므로 호출부의 try 로 잡힌다.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? DriverVersion() =>
        typeof(global::Tibero.DataAccess.Client.TiberoConnection).Assembly.GetName().Version?.ToString();
#endif
}
