using System.Security.Cryptography;
using System.Text;
using Oranda.Core.Settings;

namespace Oranda.Core.Ai.Profiles;

/// <summary>
/// 기타 CLI 를 고르고 확인하는 값. 선택은 <see cref="AiSettings.SystemCliId"/> 에 <see cref="SelectionPrefix"/> + id 로 저장하고,
/// 호출한 쪽이 확인한 명령은 <see cref="CommandHash"/> 로 요청에 실어 라우터가 실행 직전에 다시 비교한다(명령만 고쳐도 다른 대상이다).
/// </summary>
public static class AiCustomCliTarget
{
    /// <summary>선택 값의 접두어. 시스템 CLI 의 id 와 겹치지 않는다.</summary>
    public const string SelectionPrefix = "custom:";

    /// <summary>등록한 기타 CLI 의 <paramref name="id"/> 를 <see cref="AiSettings.SystemCliId"/> 에 저장할 값으로 바꾼다.</summary>
    public static string SelectionOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return SelectionPrefix + id;
    }

    /// <summary>선택 값이 기타 CLI 를 가리키면 그 id 를 낸다(빈 id 도 낸다). 아니면 false.</summary>
    public static bool TryGetId(string? selection, out string id)
    {
        if (selection is not null && selection.StartsWith(SelectionPrefix, StringComparison.Ordinal))
        {
            id = selection[SelectionPrefix.Length..];
            return true;
        }

        id = "";
        return false;
    }

    /// <summary>명령 문자열의 해시(앞뒤 공백을 뺀 UTF-8 의 SHA-256 앞 16자리, 소문자 16진수). 명령 원문을 요청·기록에 싣지 않으려고 쓴다.</summary>
    public static string CommandHash(string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.Trim())))[..16];
    }

    /// <summary>실행할 수 있는 줄인가: id 와 명령이 비어 있지 않다.</summary>
    public static bool IsRunnable(AiCustomCli? cli) =>
        cli is not null && !string.IsNullOrWhiteSpace(cli.Id) && !string.IsNullOrWhiteSpace(cli.Command);
}
