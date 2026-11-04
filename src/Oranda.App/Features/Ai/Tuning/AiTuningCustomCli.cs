using System;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Tuning;

/// <summary>
/// 기타 CLI(사용자가 이름과 실행 명령을 등록한 항목)를 고른 대화의 공통 판단과 안내 글. 기타 CLI 는 DB 도구와 실행 토큰 없이 글만 주고받으므로
/// 튜닝에는 쓰지 않고, 값을 가려 보낼 수단이 없어 가림 대상 접속에는 보내지 않으며, 이미지도 보내지 않는다.
/// 판단은 설정의 활성 대상이 기타 CLI(<see cref="AiActiveKind.CustomCli"/>·<see cref="AiActiveKind.CustomCliUnavailable"/>)인지로만 한다.
/// </summary>
internal static class AiTuningCustomCli
{
    /// <summary>AI 튜닝을 시작하려는데 기타 CLI 가 선택돼 있을 때 대화 창에 남기는 안내.</summary>
    public const string TuningText = "기타 CLI 는 DB 도구를 쓸 수 없어 AI 튜닝에는 쓰지 않습니다. 튜닝은 설정의 「AI 모델」 에서 Claude Code 나 API 프로필을 고른 뒤 시작하세요. 기타 CLI 로는 지금 일반 대화만 쓸 수 있습니다(SQL 설명·실행 계획 해설·오류 고치기는 아직 지원하지 않습니다).";

    /// <summary>가림 대상 접속(운영 표시 또는 값 가림 설정)에서 기타 CLI 로 보내지 않았을 때의 안내.</summary>
    public const string MaskingBlockedText = "가림 대상 접속(운영 표시 또는 값 가림 설정)에서는 기타 CLI 로 보내지 않습니다. 기타 CLI 는 값을 가려서 보낼 수 없으므로 아무것도 보내지 않았습니다. 설정의 「AI 모델」 에서 다른 모델을 고르세요.";

    /// <summary>고른 기타 CLI 가 목록에서 지워졌거나 실행 명령이 비어 있을 때의 안내.</summary>
    public const string UnavailableText = "선택한 기타 CLI 를 쓸 수 없습니다. 목록에서 지워졌거나 실행 명령이 비어 있습니다. 설정의 「AI 모델」 에서 다시 고르세요.";

    /// <summary>기타 CLI 는 이미지를 받지 않을 때(입구와 보내기 직전의 안내).</summary>
    public const string ImagesText = "기타 CLI 에는 이미지를 보내지 않습니다. 이미지를 지우거나 설정의 「AI 모델」 에서 이미지를 지원하는 모델을 고르세요.";

    /// <summary>일반 대화에서 기타 CLI 를 쓸 때 대화 창에 한 번 보이는 안내(문장·DB 정보를 보내지 않는다는 사실).</summary>
    public const string GeneralText = "기타 CLI 는 DB 도구를 쓸 수 없어 입력한 글만 보냅니다. 편집기의 문장과 DB 정보는 보내지 않습니다.";

    /// <summary>이 명령이 기타 CLI 를 지원하지 않을 때의 안내.</summary>
    public const string CommandUnsupportedText = "이 명령은 기타 CLI 로 쓸 수 없습니다. 설정의 「AI 모델」 에서 다른 모델을 고르세요.";

    /// <summary>설정의 활성 대상이 기타 CLI(쓸 수 없는 줄 포함)인가.</summary>
    public static bool IsSelected(AiSettings? settings) =>
        settings is not null && AiActiveProfile.Find(settings).Kind is AiActiveKind.CustomCli or AiActiveKind.CustomCliUnavailable;

    /// <summary>대화의 보낼 곳이 기타 CLI(쓸 수 없는 줄 포함)인가.</summary>
    public static bool IsCustom(AiConversationTarget? target) =>
        target is not null && AiCustomCliTarget.TryGetId(target.SystemCliId, out _);

    /// <summary>기타 CLI 인데 실행할 수 있는 줄이 아니다(명령 해시가 없다).</summary>
    public static bool IsUnavailable(AiConversationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.CustomCliCommandHash is null;
    }
}
