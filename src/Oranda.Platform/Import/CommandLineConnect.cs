using Oranda.Core.Connection;

namespace Oranda.Platform.Import;

/// <summary>명령줄 <c>oranda user@alias [as sysdba]</c> 해석 결과. 비밀번호는 담지 않는다.</summary>
public sealed record CommandLineTarget(string User, string DataSource, AuthMode Auth);

/// <summary>명령줄 <c>oranda user@alias [as sysdba]</c> 해석. 소유: TSK-03-05.</summary>
public static class CommandLineConnect
{
    /// <summary>
    /// 인자에서 접속 대상을 뽑는다. <c>user@alias</c>, <c>user@host:1521/svc</c>, <c>user@alias as sysdba</c>(한 덩어리나 여러 인자)를 받는다.
    /// <c>user/password@alias</c> 의 비밀번호 부분은 버린다. 해석할 수 없으면 null.
    /// </summary>
    public static CommandLineTarget? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var tokens = args.SelectMany(a => a.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var at = tokens.FindIndex(t => !t.StartsWith('-') && !t.StartsWith('/') && t.Contains('@', StringComparison.Ordinal));
        if (at < 0)
        {
            return null;
        }

        var token = tokens[at];
        var split = token.LastIndexOf('@');
        var left = token[..split];
        var source = token[(split + 1)..];
        var slash = left.IndexOf('/', StringComparison.Ordinal);
        var user = (slash >= 0 ? left[..slash] : left).Trim().Trim('"');
        if (user.Length == 0 || source.Length == 0)
        {
            return null;
        }

        var auth = AuthMode.Default;
        if (at + 2 < tokens.Count && tokens[at + 1].Equals("as", StringComparison.OrdinalIgnoreCase))
        {
            auth = ImportHelpers.ParseRole(tokens[at + 2]);
        }

        return new CommandLineTarget(user, source, auth);
    }

    /// <summary>저장된 프로필 가운데 같은 사용자·접속 문자열(없으면 이름이 <c>user@alias</c>)인 것을 찾는다.</summary>
    public static ConnectionProfile? FindProfile(IEnumerable<ConnectionProfile> profiles, CommandLineTarget target)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(target);
        var list = profiles.ToList();
        return list.FirstOrDefault(p => Same(p.User, target.User) && Same(p.DataSource, target.DataSource) && p.Auth == target.Auth)
            ?? list.FirstOrDefault(p => Same(p.User, target.User) && Same(p.DataSource, target.DataSource))
            ?? list.FirstOrDefault(p => Same(p.Name, $"{target.User}@{target.DataSource}"));
    }

    /// <summary>저장된 프로필이 없을 때 쓸 새 프로필(비밀번호 없음).</summary>
    public static ConnectionProfile ToProfile(CommandLineTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ConnectionProfile($"{target.User}@{target.DataSource}", target.User, target.DataSource, target.Auth);
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
