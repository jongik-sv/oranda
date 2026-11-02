using System.Text;
using Oranda.Core.Ai.Contracts;

namespace Oranda.Platform.Ai.Cli;

/// <summary>
/// claude 세션 id 의 소속(어느 프로필이 만들었는가) 기록. claude 는 <c>--resume &lt;id&gt;</c> 에서 작업 폴더와 무관하게 저장된 세션 전체를 뒤져
/// 세션을 찾으므로, 다른 경로(다른 프로필·구독 Claude Code)가 만든 세션을 이어 말하면 앞 대화(SQL·이미지)가 지금 경로의 서버로 전송된다.
/// 드라이버가 스스로 막는다: 프로필 실행은 같은 프로필이 만든 세션만 이어 말할 수 있고, 구독 실행은 프로필이 만든 세션을 이어 말할 수 없다.
/// 구독 실행은 기록하지 않는다: 기록이 없는 세션은 구독 세션(이 기능 이전의 것 포함)으로 보고 지금처럼 이어진다.
/// 판정할 수 없으면(기록을 읽지 못함, 안전하지 않은 id) 프로필 실행은 닫는다.
/// </summary>
internal sealed class ClaudeSessionOwners(string directory)
{
    public const string MismatchCode = "resume_route_mismatch";

    // 기록은 작다. 지우면 구독 경로가 프로필 세션을 이어 말하게 될 수 있으므로 claude 의 세션 보관 기간보다 훨씬 길게 둔다.
    private static readonly TimeSpan RecordLifetime = TimeSpan.FromDays(365);

    /// <summary>이어 말해도 되는지 확인한다. <paramref name="owner"/> 는 프로필 id(<c>N</c> 형식), 구독 실행이면 null.</summary>
    public AiError? CheckResume(string resumeId, string? owner)
    {
        var mismatch = new AiError("이 대화는 지금 선택한 AI 모델이 시작한 것이 아니어서 이어 말할 수 없습니다. 새 대화로 시작하세요.", MismatchCode);

        // 기록 파일 이름으로 쓸 수 없는 id 는 소속을 확인할 수 없다. 구독 실행은 claude 가 알아서 거절하게 두고, 프로필 실행은 닫는다.
        if (!IsSafe(resumeId))
        {
            return owner is null ? null : mismatch;
        }

        string? recorded;
        try
        {
            recorded = Read(resumeId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AiError("이어 말할 대화의 소속을 확인하지 못했습니다. 새 대화로 시작하세요.", "session_record_unreadable");
        }

        var allowed = owner is null ? recorded is null : string.Equals(recorded, owner, StringComparison.Ordinal);
        return allowed ? null : mismatch;
    }

    /// <summary>
    /// 프로필이 만든(또는 이어 간) 세션을 기록한다. 기록하지 못하면 false: 프로필 쪽은 기록이 없으면 거절되어 안전하지만 구독 쪽은 기록이 없는 세션을
    /// 이어 말하게 하므로, 호출한 쪽이 실행을 닫아야 한다.
    /// </summary>
    public bool Record(string sessionId, string owner)
    {
        if (!IsSafe(sessionId))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName(sessionId));
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, owner, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Prune();
        return true;
    }

    // 대소문자 구분 볼륨에서 id 의 대소문자를 바꿔 기록을 비켜 가지 못하게 소문자로 맞춘다.
    private static string FileName(string id) => id.ToLowerInvariant();

    private string? Read(string resumeId)
    {
        var path = Path.Combine(directory, FileName(resumeId));
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    // claude 세션 id 는 UUID 다. 경로 구분자·점이 들어간 값은 기록 파일 이름으로 쓰지 않는다.
    private static bool IsSafe(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private void Prune()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                // 파일이 사라지는 중이면 시각이 1601년으로 읽힌다. 비정상 시각은 오래된 것으로 보지 않는다.
                var written = File.GetLastWriteTimeUtc(file);
                if (written.Year > 2000 && DateTime.UtcNow - written > RecordLifetime)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 정리는 최선을 다하는 일이다.
        }
    }
}
