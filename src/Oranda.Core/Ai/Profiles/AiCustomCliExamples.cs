namespace Oranda.Core.Ai.Profiles;

/// <summary>명령 틀을 실제로 왕복해 봤는지(어느 PC 결과인지는 항목 주석).</summary>
public enum AiCustomCliVerification
{
    /// <summary>빈 임시 폴더에 짧은 프롬프트를 보내 응답을 받았다(「확인됨」).</summary>
    Confirmed,

    /// <summary>공식 문서·공식 도움말에 적힌 형태만 따랐다(「미확인(문서 기준)」). 왕복해 보지 않았거나 응답까지 가지 못했다.</summary>
    Unconfirmed,
}

/// <summary>
/// 많이 쓰는 CLI 의 1회 실행 명령 틀 한 줄. 설정 화면의 명령 틀 목록이 고르면 이름과 명령을 자동으로 채운다(채운 뒤 고칠 수 있다).
/// </summary>
/// <param name="Id">틀의 식별자(영문 소문자).</param>
/// <param name="Name">등록 이름의 기본값.</param>
/// <param name="Command">명령 문자열. <see cref="AiCustomCliCommand.Parse"/> 를 통과해야 한다(시험이 확인한다).</param>
/// <param name="Verification">왕복 확인 여부.</param>
/// <param name="NoteKo">고를 때 함께 알릴 한 줄(한국어). 없으면 null.</param>
/// <param name="NoteEn">같은 글의 영어.</param>
/// <param name="NoteUnlessOption">이 옵션이 명령에 있으면 사용자가 직접 정한 것이므로 안내를 보이지 않는다(예: 기본 모드가 자동 승인인 CLI 의 <c>--mode</c>). 없으면 null.</param>
public sealed record AiCustomCliExample(string Id, string Name, string Command, AiCustomCliVerification Verification, string? NoteKo = null, string? NoteEn = null, string? NoteUnlessOption = null);

/// <summary>
/// 기타 CLI 명령 틀 표. 글만 주고받는 1회 실행 형태이며, 대화형 실행이 아니다.
/// 근거가 있는 것만 넣는다: 각 항목은 공식 문서 또는 공식 도움말에서 형태를 찾았고, 출처를 주석에 적는다. 출처를 찾지 못한 CLI 는 넣지 않는다.
/// 「확인됨」 은 빈 임시 폴더에 「OK 라고만 답하라」 를 보내 응답을 받은 것만이다. 어느 PC 결과인지는 항목 주석에 적는다(2026-11-04, macOS arm64 와 Windows win144).
/// 틀의 프롬프트 자리는 <c>{prompt}</c> 이므로 글이 명령줄 인자로 실린다(분해기가 화면에 그 경고를 낸다).
/// </summary>
public static class AiCustomCliExamples
{
    /// <summary>표 전체(이름순이 아니라 확인됨이 먼저, 그 안에서는 쓰는 사람이 많은 순).</summary>
    public static IReadOnlyList<AiCustomCliExample> All { get; } =
    [
        // 확인됨 — 왕복 성공
        // 출처: https://learn.chatgpt.com/docs/non-interactive-mode (codex exec "<prompt>", 읽기 전용 샌드박스가 기본, Git 저장소 밖에서는 --skip-git-repo-check 필요)
        // 왕복: 빈 임시 폴더에서 `codex exec --skip-git-repo-check "OK 라고만 답하라"` → 응답 OK, 종료 코드 0(macOS arm64). 같은 날 Windows win144 에서도 CLI 를 직접 불러 같은 결과(응답 OK, 종료 코드 0, 앱 경로 아님). 표준 입력은 닫아 둔다(프롬프트 인자가 있으면 표준 입력을 추가 문맥으로 읽는다).
        new("codex", "Codex CLI (exec)", "codex exec --skip-git-repo-check \"{prompt}\"", AiCustomCliVerification.Confirmed),

        // 출처: https://opencode.ai/docs/cli/ (opencode run [message..]) 와 `opencode run --help`. 자동 승인 옵션 --auto 는 넣지 않는다.
        // 왕복: macOS 에서 2026-11-04 Mac 조정 메모에 「왕복: opencode 성공」 으로 적힌 기록을 인용한 것이다(원 출력은 남아 있지 않다). Mac 에서 같은 날 다시 시도했을 때는 무료 사용량 한도 오류(응답 단계 전 계정 한도)였다. Windows win144 에서 한 번 시도했을 때는 CLI 안에서 오류 추적이 나고(분류: 인증·한도 문구 없음, 서버 연결 단계 오류로 보임) 종료 코드 1이라 응답을 받지 못했다(이 PC 에서는 확인하지 못함).
        new("opencode", "OpenCode", "opencode run \"{prompt}\"", AiCustomCliVerification.Confirmed),

        // 미확인(문서 기준)
        // 출처: https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/headless.md (-p/--prompt 가 비대화형) 와 `gemini --help` (--skip-trust: 현재 작업 폴더를 신뢰, -p 는 표준 입력 뒤에 붙음).
        // 이 PC 는 계정 등급 오류(IneligibleTierError)로 인증 단계에서 끝나 응답까지 가지 못했다.
        new("gemini", "Gemini CLI", "gemini --skip-trust -p \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: `zcode --help` (-p, --prompt <text>: Run a single prompt without opening the TUI). 공식 문서 주소는 찾지 못했고 도움말만 근거다.
        // 프롬프트는 인자로만 받고 표준 입력은 읽지 않는다. 이 PC 는 모델 서버 연결 오류로 응답까지 가지 못했다.
        // --mode 는 build·edit·yolo 이고 --prompt 의 기본이 yolo 이므로, 명령에 옵션이 없어도 확인 없이 도구를 실행한다. 분해기는 옵션이 없는 기본값을 알릴 수 없어서 안내를 따로 붙이며, 화면은 이 CLI 의 줄에 안내를 계속 보인다(--mode 를 쓰면 뺀다).
        new("zcode", "ZCode", "zcode -p \"{prompt}\"", AiCustomCliVerification.Unconfirmed,
            "이 CLI 는 -p 로 실행하면 기본이 자동 승인(yolo)입니다. 명령에 옵션이 없어도 확인 없이 파일을 고치거나 명령을 실행할 수 있습니다.",
            "This CLI runs in auto-approve (yolo) mode by default with -p. Even without options in the command it may change files or run commands without asking.",
            NoteUnlessOption: "--mode"),

        // 출처: https://aider.chat/docs/scripting.html (--message: 한 번 보내고 처리한 뒤 종료)
        // 파일을 고치는 도구라 빈 폴더에서도 파일을 만들 수 있다. 설치되어 있지 않아 왕복하지 않았다.
        new("aider", "Aider", "aider --message \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: https://goose-docs.ai/docs/guides/goose-cli-commands (goose run -t/--text, --no-session: 세션 파일을 만들지 않음)
        new("goose", "Goose", "goose run --no-session -t \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: https://kilo.ai/docs/cli (kilo run "<message>"; --auto 가 없으면 권한 요청을 스스로 거절한다). --auto 는 넣지 않는다.
        new("kilo", "Kilo Code", "kilo run \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: https://docs.github.com/en/copilot/how-tos/copilot-cli/automate-copilot-cli/run-cli-programmatically (copilot -p "<prompt>"). --allow-all-tools 등 허용 옵션은 넣지 않는다.
        new("copilot", "GitHub Copilot CLI", "copilot -p \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: https://kiro.dev/docs/cli/headless/ (kiro-cli chat --no-interactive "<prompt>"; 환경 변수 KIRO_API_KEY 필요). --trust-all-tools 는 넣지 않는다.
        // KIRO_API_KEY 는 명령에 쓰지 말고 앱을 띄운 환경에 설정해 둔다(명령은 설정 파일에 평문으로 남는다).
        new("kiro", "Kiro CLI", "kiro-cli chat --no-interactive \"{prompt}\"", AiCustomCliVerification.Unconfirmed,
            "KIRO_API_KEY 환경 변수가 필요합니다. 명령에 쓰지 말고 앱을 실행한 환경에 설정하세요.",
            "Needs the KIRO_API_KEY environment variable. Do not put it in the command; set it in the environment that launches the app."),

        // 출처: https://docs.qoder.com/cli/run-in-scripts.md · https://docs.qoder.com/cli/cli-reference (qodercli -p "<prompt>", 비대화형에서 확인이 필요한 작업은 기본 거부). --yolo 는 넣지 않는다.
        new("qodercli", "Qoder CLI", "qodercli -p \"{prompt}\"", AiCustomCliVerification.Unconfirmed),

        // 출처: https://github.com/badlogic/pi-mono/blob/main/packages/coding-agent/docs/cli.md (pi --print "<prompt>": 응답을 표준 출력에 쓰고 종료)
        new("pi", "pi", "pi --print \"{prompt}\"", AiCustomCliVerification.Unconfirmed),
    ];

    // 넣지 않은 후보: amp(공식 문서 쪽 실행 모드 페이지를 로그인 없이 읽지 못함), grok(공식 출처를 찾지 못함). 출처를 확인하면 더한다.
}
