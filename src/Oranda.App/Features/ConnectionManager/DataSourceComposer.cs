using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary><c>dataSource</c> 문자열과 접속 방식 사이의 변환.</summary>
public static partial class DataSourceComposer
{
    [GeneratedRegex(@"^(?:(?<scheme>tcps?)://)?(?<host>[A-Za-z0-9_.\-]+)(?::(?<port>\d{1,5}))?/(?<service>[A-Za-z0-9_.\-$#]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPortPattern();

    // 공백을 뺀 단순 SID 기술자: 주소 하나(TCP·TCPS)와 CONNECT_DATA 의 SID 하나뿐. 다른 키가 있으면 사용자 지정으로 둔다.
    [GeneratedRegex(@"^\(DESCRIPTION=\(ADDRESS=\(PROTOCOL=(?<proto>TCPS?)\)\(HOST=(?<host>[A-Za-z0-9_.\-]+)\)\(PORT=(?<port>\d{1,5})\)\)\(CONNECT_DATA=\(SID=(?<sid>[A-Za-z0-9_.\-$#]+)\)\)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidDescriptorPattern();

    /// <summary>
    /// 호스트·포트·서비스를 한 문자열로. 포트가 비면 생략(기본 1521). TLS 면 <c>tcps://</c>.
    /// SID 는 Easy Connect 로 쓸 수 없어 접속 기술자로 만든다(포트가 비면 1521, TLS 면 <c>PROTOCOL=TCPS</c>).
    /// </summary>
    public static string Compose(bool tls, string host, string? port, string service, bool useSid = false)
    {
        if (useSid)
        {
            var p = string.IsNullOrWhiteSpace(port) ? "1521" : port.Trim();
            return $"(DESCRIPTION=(ADDRESS=(PROTOCOL={(tls ? "TCPS" : "TCP")})(HOST={host.Trim()})(PORT={p}))(CONNECT_DATA=(SID={service.Trim()})))";
        }

        var scheme = tls ? "tcps://" : string.Empty;
        var portPart = string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port.Trim();
        return $"{scheme}{host.Trim()}{portPart}/{service.Trim()}";
    }

    /// <summary>단순한 <c>[tcps://]host[:port]/service</c> 이면 나눠 준다.</summary>
    public static bool TryParseHostPort(string dataSource, out bool tls, out string host, out string port, out string service)
    {
        tls = false;
        host = port = service = string.Empty;
        var m = HostPortPattern().Match(ConnectStringText.Normalize(dataSource));
        if (!m.Success)
        {
            return false;
        }

        tls = m.Groups["scheme"].Value.Equals("tcps", StringComparison.OrdinalIgnoreCase);
        host = m.Groups["host"].Value;
        port = m.Groups["port"].Value;
        service = m.Groups["service"].Value;
        return true;
    }

    /// <summary>
    /// 호스트·포트 방식으로 보일 수 있는 문자열이면 나눠 준다: <c>[tcps://]host[:port]/service</c> 또는 단순 SID 기술자
    /// (공백은 무시, <paramref name="sid"/> = true).
    /// </summary>
    public static bool TryParseBasic(string dataSource, out bool tls, out string host, out string port, out string service, out bool sid)
    {
        sid = false;
        var text = ConnectStringText.Normalize(dataSource);
        if (TryParseHostPort(text, out tls, out host, out port, out service))
        {
            return true;
        }

        var m = SidDescriptorPattern().Match(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        if (!m.Success)
        {
            return false;
        }

        tls = m.Groups["proto"].Value.Equals("TCPS", StringComparison.OrdinalIgnoreCase);
        host = m.Groups["host"].Value;
        port = m.Groups["port"].Value;
        service = m.Groups["sid"].Value;
        sid = true;
        return true;
    }

    /// <summary>저장된 문자열을 보고 편집기에서 보일 방식을 고른다(여러 줄·주석·별칭은 정리한 한 줄로 판별).</summary>
    public static ConnectionMode Detect(string dataSource)
    {
        dataSource = ConnectStringText.Normalize(dataSource);
        if (dataSource.Length == 0)
        {
            return ConnectionMode.HostPortService;
        }

        if (TryParseBasic(dataSource, out _, out _, out _, out _, out _))
        {
            return ConnectionMode.HostPortService;
        }

        return dataSource.AsSpan().IndexOfAny("/:(=?") >= 0 ? ConnectionMode.EasyConnect : ConnectionMode.TnsAlias;
    }

    /// <summary>
    /// 저장된 프로필의 연결 문자열(SQL*Plus·SQLcl 식 <c>user@접속 문자열 [AS 역할]</c>). 비밀번호는 넣지 않는다.
    /// </summary>
    public static string Describe(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Describe(profile.User, profile.ProxyUser, profile.DataSource, profile.Auth);
    }

    /// <summary>
    /// 연결 문자열 미리보기: <c>ft@localhost:1522/FREEPDB1</c>, TCPS 면 <c>ft@tcps://host:2484/svc</c>, 프록시면 <c>app[ft]@…</c>.
    /// 비밀번호 칸은 받지 않으며, 접속 문자열이나 사용자에 비밀이 섞여 있으면(저장 때 거부되는 값) 빈 글자를 돌려준다.
    /// </summary>
    public static string Describe(string? user, string? proxyUser, string? dataSource, AuthMode auth)
    {
        var target = ConnectStringText.Normalize(dataSource);
        var who = user?.Trim() ?? string.Empty;
        var proxy = proxyUser?.Trim() ?? string.Empty;
        if (target.Length == 0 || ConnectionProfileValidator.ContainsSecret(target) || who.Contains('/', StringComparison.Ordinal) || proxy.Contains('/', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (proxy.Length > 0)
        {
            who = $"{proxy}[{who}]";
        }

        var text = who.Length == 0 ? target : $"{who}@{target}";
        return auth == AuthMode.Default ? text : $"{text} AS {auth.ToString().ToUpperInvariant()}";
    }
}
