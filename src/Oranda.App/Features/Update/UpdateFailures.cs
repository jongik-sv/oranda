using System.Net;
using System.Net.Sockets;

namespace Oranda.App.Features.Update;

/// <summary>업데이트 확인·내려받기 실패의 종류. 화면은 종류별 쉬운 문구를 보이고, 원문은 툴팁·로그에 남긴다.</summary>
public enum UpdateFailure
{
    /// <summary>알 수 없는 실패(원문 그대로 보인다).</summary>
    Other,

    /// <summary>403·429: 요청 한도 초과(GitHub API 의 IP 당 시간 60회 등) 또는 서버가 거절.</summary>
    RateLimited,

    /// <summary>404·없는 폴더·파일: 피드 주소에 릴리스 파일이 없다.</summary>
    NotFound,

    /// <summary>이름 풀이·연결·프록시·TLS 실패, 시간 초과: 서버에 닿지 못했다.</summary>
    Unreachable,
}

public static class UpdateFailures
{
    /// <summary>
    /// 예외(안쪽 예외 포함)를 보고 종류를 가른다. 앱 어셈블리는 네트워크 라이브러리를 참조하지 않는다는 안전 규칙
    /// (SafetyCrossNoDbTests)을 지키려고 Velopack 이 던지는 HTTP 요청 예외는 형식 이름과 공개 속성(<c>StatusCode</c>·<c>HttpRequestError</c>)으로만 읽는다.
    /// </summary>
    public static UpdateFailure Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AggregateException { InnerExceptions.Count: > 0 } aggregate)
            {
                return Classify(aggregate.InnerExceptions[0]);
            }

            if (IsHttpRequestException(e))
            {
                switch (Property(e, "StatusCode"))
                {
                    case HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests:
                        return UpdateFailure.RateLimited;
                    case HttpStatusCode.NotFound:
                        return UpdateFailure.NotFound;
                    case null when Property(e, "HttpRequestError")?.ToString() is "NameResolutionError" or "ConnectionError" or "ProxyTunnelError" or "SecureConnectionError":
                        return UpdateFailure.Unreachable;
                }

                continue; // 안쪽 예외(소켓 오류 등)를 더 본다
            }

            switch (e)
            {
                case DirectoryNotFoundException or FileNotFoundException:
                    return UpdateFailure.NotFound;
                case SocketException or TimeoutException:
                case TaskCanceledException: // HTTP 요청 시간 초과(사용자 취소는 여기까지 오지 않는다)
                    return UpdateFailure.Unreachable;
            }
        }

        return UpdateFailure.Other;
    }

    private static bool IsHttpRequestException(Exception e)
    {
        for (var type = e.GetType(); type is not null; type = type.BaseType)
        {
            if (type.Name == "HttpRequestException")
            {
                return true;
            }
        }

        return false;
    }

    private static object? Property(Exception e, string name) => e.GetType().GetProperty(name)?.GetValue(e);

    /// <summary>종류별 문구 리소스 키(<see cref="UpdateFailure.Other"/> 는 원문을 쓰므로 키가 없다).</summary>
    public static string TextKey(UpdateFailure failure) => "Update.error." + failure switch
    {
        UpdateFailure.RateLimited => "rateLimited",
        UpdateFailure.NotFound => "notFound",
        UpdateFailure.Unreachable => "unreachable",
        _ => "other",
    };

    /// <summary>툴팁·로그에 남길 원문: 예외 형식과 메시지(안쪽 예외 메시지가 다르면 덧붙인다).</summary>
    public static string Describe(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var text = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException is { } inner && !string.Equals(inner.Message, ex.Message, StringComparison.Ordinal))
        {
            text += $" ({inner.GetType().Name}: {inner.Message})";
        }

        return text;
    }
}
