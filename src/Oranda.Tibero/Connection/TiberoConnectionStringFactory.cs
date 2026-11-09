using System.Buffers;
using System.Globalization;
using System.Text;
using Oranda.Core.Composition;
using Oranda.Core.Connection;

namespace Oranda.Tibero.Connection;

/// <summary>
/// Tibero 연결 문자열(docs/design/tibero.md 3장): <c>Data Source=((INSTANCE=(HOST=)(PORT=)(DB_NAME=)));User Id=…;Password=…</c>.
/// 프로필의 <see cref="ConnectionProfile.DataSource"/> 는 <c>host:port/db_name</c>(포트 생략 시 8629, IPv6 는 <c>[::1]:8629/db</c>)
/// 또는 이미 조립된 기술자(<c>(</c> 로 시작)다. <c>tbdsn.tbr</c> 별칭·TLS·지갑·프록시·SYSDBA 등 인증 모드는 아직 지원하지 않는다.
/// </summary>
[OrandaService(typeof(IConnectionStringFactory), Dbms = Dbms.Tibero)]
internal sealed class TiberoConnectionStringFactory : IConnectionStringFactory
{
    /// <summary>Tibero 기본 리스너 포트.</summary>
    public const int DefaultPort = 8629;

    public Dbms Dbms => Dbms.Tibero;

    public string Build(ConnectionProfile profile, string password)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(password);

        if (profile.Auth != AuthMode.Default)
        {
            throw new NotSupportedException("Tibero 는 아직 기본 인증만 지원합니다.");
        }

        if (!string.IsNullOrWhiteSpace(profile.ProxyUser))
        {
            throw new NotSupportedException("Tibero 는 프록시 접속을 지원하지 않습니다.");
        }

        var builder = new StringBuilder();
        builder.Append("Data Source=").Append(Descriptor(profile.DataSource));
        builder.Append(";User Id=").Append(Value(profile.User));
        builder.Append(";Password=").Append(Value(password));
        return builder.ToString();
    }

    /// <summary>접속 주소 글 → <c>((INSTANCE=(HOST=)(PORT=)(DB_NAME=)))</c>. 이미 기술자면 그대로.</summary>
    internal static string Descriptor(string dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new ArgumentException("접속 주소(host:port/db_name)가 비어 있습니다.", nameof(dataSource));
        }

        var text = dataSource.Trim();
        if (text.StartsWith('('))
        {
            // 연결 문자열의 다른 키를 끼워 넣지 못하게 한다(「;」·따옴표). 괄호는 짝이 맞아야 한다.
            if (text.AsSpan().IndexOfAny(StringSeparators) >= 0 || !ParenthesesBalanced(text))
            {
                throw new ArgumentException("접속 주소 기술자가 올바르지 않습니다.", nameof(dataSource));
            }

            return text;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1)
        {
            throw new ArgumentException("접속 주소는 host:port/db_name 형식이어야 합니다.", nameof(dataSource));
        }

        var hostPort = text[..slash];
        var dbName = text[(slash + 1)..].Trim();
        string host;
        var port = DefaultPort;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0)
            {
                throw new ArgumentException("접속 주소의 IPv6 표기에 ] 가 없습니다.", nameof(dataSource));
            }

            host = hostPort[1..close];
            var rest = hostPort[(close + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    throw new ArgumentException("접속 주소는 [IPv6]:port/db_name 형식이어야 합니다.", nameof(dataSource));
                }

                port = ParsePort(rest[1..], nameof(dataSource));
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon >= 0 && hostPort.IndexOf(':', StringComparison.Ordinal) != colon)
            {
                throw new ArgumentException("IPv6 주소는 [주소]:port/db_name 처럼 대괄호로 감싸야 합니다.", nameof(dataSource));
            }

            if (colon >= 0)
            {
                host = hostPort[..colon];
                port = ParsePort(hostPort[(colon + 1)..], nameof(dataSource));
            }
            else
            {
                host = hostPort;
            }
        }

        host = host.Trim();
        if (host.Length == 0 || dbName.Length == 0)
        {
            throw new ArgumentException("접속 주소는 host:port/db_name 형식이어야 합니다.", nameof(dataSource));
        }

        if (HasDescriptorSeparator(host) || HasDescriptorSeparator(dbName))
        {
            throw new ArgumentException("접속 주소에 쓸 수 없는 문자가 있습니다.", nameof(dataSource));
        }

        return string.Create(CultureInfo.InvariantCulture, $"((INSTANCE=(HOST={host})(PORT={port})(DB_NAME={dbName})))");
    }

    private static int ParsePort(string text, string paramName)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("접속 주소의 포트가 올바르지 않습니다.", paramName);
        }

        return port;
    }

    private static readonly SearchValues<char> DescriptorSeparators = SearchValues.Create("()=;\"'");

    private static readonly SearchValues<char> StringSeparators = SearchValues.Create(";\"'");

    private static bool ParenthesesBalanced(string text)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    private static readonly SearchValues<char> ValueSpecials = SearchValues.Create(";\"'=");

    private static bool HasDescriptorSeparator(string text) => text.AsSpan().IndexOfAny(DescriptorSeparators) >= 0;

    /// <summary>연결 문자열 값. 구분자·따옴표·앞뒤 공백이 있으면 큰따옴표로 감싼다(안의 큰따옴표는 두 번).</summary>
    private static string Value(string value)
    {
        var needsQuote = value.Length > 0
            && (value.AsSpan().IndexOfAny(ValueSpecials) >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        return needsQuote ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }
}
