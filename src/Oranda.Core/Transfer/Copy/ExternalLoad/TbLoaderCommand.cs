using System.Globalization;
using System.Text;

namespace Oranda.Core.Transfer.Copy.ExternalLoad;

/// <summary>
/// tbLoader 실행 묶음(XL2). tbLoader 는 parfile 이 없고 비밀번호를 <c>userid=</c> 명령줄로만 받는다(설명서) — 명령줄에 비밀번호를 두지 않으려고
/// <c>userid=사용자@별칭</c> 만 주고 비밀번호는 표준 입력으로 준다(도구가 묻는다고 보고, win144 실측으로 확인. 묻지 않으면 접속 실패 → 실행기가 기본 방식으로 다시 넣는다).
/// 접속 별칭은 작업 폴더에 만든 <c>tbdsn.tbr</c>(<see cref="DsnAlias"/>)이고 환경 변수 <c>TBDSN_FILE</c> 로 가리킨다(실측 필요).
/// </summary>
public static class TbLoaderCommand
{
    public const string DsnAlias = "ORANDA_LOAD";
    public const string DsnFileName = "tbdsn.tbr";

    /// <summary>Tibero 기본 리스너 포트.</summary>
    private const int DefaultPort = 8629;

    public static (ExternalProcessStart Start, string Description) Build(string toolPath, LoaderWorkspace workspace, ExternalLoadLogin login, int maxErrors, bool direct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolPath);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(login);
        if (login.User.Any(c => c is ' ' or '/' or '@' or '"' or '\''))
        {
            throw new ArgumentException("사용자 이름에 tbLoader 명령줄에 적을 수 없는 글자가 있습니다.", nameof(login));
        }

        var dsn = Path.Combine(workspace.Directory, DsnFileName);
        File.WriteAllText(dsn, $"{DsnAlias}=(\n    {Instance(login.Connect)}\n)\n", SqlLoaderControlFile.FileEncoding);
        var inv = CultureInfo.InvariantCulture;
        string[] arguments =
        [
            $"userid={login.User}@{DsnAlias}",
            $"control={workspace.ControlFile}",
            $"data={workspace.DataFile}",
            $"log={workspace.LogFile}",
            $"bad={workspace.BadFile}",
            $"discard={workspace.DiscardFile}",
            string.Create(inv, $"errors={maxErrors}"),
            $"direct={(direct ? "Y" : "N")}",
        ];
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["TBDSN_FILE"] = dsn,
            ["TB_NLS_LANG"] = "UTF8",
        };
        var start = new ExternalProcessStart(toolPath, arguments, workspace.Directory, environment, login.Password + "\n");
        return (start, $"{Path.GetFileName(toolPath)} {string.Join(' ', arguments)} (비밀번호는 표준 입력)");
    }

    /// <summary>접속 주소(<c>host:port/db_name</c> 또는 <c>(INSTANCE=…)</c> 기술자) → tbdsn 의 <c>(INSTANCE=(HOST=)(PORT=)(DB_NAME=))</c>.</summary>
    internal static string Instance(string connect)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connect);
        var text = connect.Trim();
        if (text.StartsWith('('))
        {
            // 이미 조립된 기술자: 바깥 괄호 한 겹((( … )))은 tbdsn 의 별칭 괄호와 겹치므로 벗긴다.
            while (text.StartsWith("((", StringComparison.Ordinal) && text.EndsWith("))", StringComparison.Ordinal))
            {
                text = text[1..^1];
            }

            return text;
        }

        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == text.Length - 1)
        {
            throw new ArgumentException("접속 주소는 host:port/db_name 형식이어야 합니다.", nameof(connect));
        }

        var hostPort = text[..slash];
        var db = text[(slash + 1)..].Trim();
        var host = hostPort;
        var port = DefaultPort;
        var close = hostPort.StartsWith('[') ? hostPort.IndexOf(']', StringComparison.Ordinal) : -1;
        var colon = hostPort.LastIndexOf(':');
        if (colon > close && colon > 0)
        {
            host = hostPort[..colon];
            // 포트가 숫자가 아니면 ArgumentException(실행기가 준비 실패로 받아 기본 방식으로 넣는다 — FormatException 이면 실행 전체가 멈춘다).
            if (!int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is <= 0 or > 65535)
            {
                throw new ArgumentException("접속 주소의 포트가 숫자가 아닙니다(host:port/db_name).", nameof(connect));
            }
        }

        host = host.Trim().TrimStart('[').TrimEnd(']');
        var b = new StringBuilder();
        b.Append(CultureInfo.InvariantCulture, $"(INSTANCE=(HOST={host})(PORT={port})(DB_NAME={db}))");
        return b.ToString();
    }
}
