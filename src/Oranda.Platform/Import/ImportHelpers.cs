using System.Text;
using System.Text.RegularExpressions;
using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>가져오기 구현이 함께 쓰는 작은 도구. 소유: TSK-03-05.</summary>
internal static class ImportHelpers
{
    /// <summary>사용자 이름을 알 수 없는 원본(tnsnames.ora)에서 쓰는 자리 표시 이름. 사용자가 접속 관리자에서 고친다.</summary>
    public const string PlaceholderUser = "USERNAME";

    /// <summary>UTF-8(BOM 포함)로 읽어 보고 안 되면 CP949 로 읽는다(Orange·옛 도구 파일).</summary>
    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949).GetString(bytes);
        }
    }

    public static AuthMode ParseRole(string? role) => role?.Trim().ToUpperInvariant().Replace(" ", string.Empty, StringComparison.Ordinal) switch
    {
        "SYSDBA" => AuthMode.SysDba,
        "SYSOPER" => AuthMode.SysOper,
        "SYSBACKUP" => AuthMode.SysBackup,
        "SYSDG" => AuthMode.SysDg,
        "SYSKM" => AuthMode.SysKm,
        _ => AuthMode.Default,
    };

    /// <summary>Easy Connect Plus 문자열 <c>host:port/service</c>. 포트·서비스가 없으면 있는 것만 쓴다.</summary>
    public static string EasyConnect(string host, string? port, string? service)
    {
        var text = host.Trim();
        if (!string.IsNullOrWhiteSpace(port))
        {
            text += ":" + port.Trim();
        }

        if (!string.IsNullOrWhiteSpace(service))
        {
            text += "/" + service.Trim().TrimStart('/');
        }

        return text;
    }

    /// <summary>JDBC URL(<c>jdbc:oracle:thin:@//host:1521/svc</c>·<c>@host:1521:sid</c>·<c>@alias</c>)에서 접속 문자열을 뽑는다.</summary>
    public static string? FromJdbcUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var at = url.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var rest = url[(at + 1)..].Trim();
        if (rest.StartsWith("//", StringComparison.Ordinal))
        {
            return rest[2..];
        }

        var sid = Regex.Match(rest, @"^([^:/()\s]+):(\d+):([^:/()\s]+)$");
        return sid.Success ? $"{sid.Groups[1].Value}:{sid.Groups[2].Value}/{sid.Groups[3].Value}" : rest;
    }
}
