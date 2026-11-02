using System;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Ai.Access;
using Oranda.Core.Connection;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>
/// 새 이미지를 보내기 직전 확인 창·감사 기록에 쓰는 사실. 이미지 안의 값은 가릴 수 없으므로, 그 대화가 리터럴을 가리는 중일 때(<see cref="Masking"/>)
/// 사용자가 받는 곳·장수·크기를 보고 확인해야 보낸다. 이미지 내용은 어디에도 싣지 않는다.
/// </summary>
/// <param name="ConnectionName">대화가 묶인 접속 이름.</param>
/// <param name="Environment">접속 환경(운영이면 확인 창에 「운영」 배지를 보인다).</param>
/// <param name="RecipientName">받는 곳 이름: 프로필 이름 또는 Claude Code.</param>
/// <param name="RecipientUrl">프로필의 서버 주소. Claude Code 면 null.</param>
/// <param name="Count">이번에 보내는 새 이미지 장수.</param>
/// <param name="TotalBytes">이번에 보내는 이미지의 전송 크기 합.</param>
/// <param name="Masking">이 대화가 리터럴을 가리는 중이다(확인 창을 띄우는 조건).</param>
/// <param name="AccessLevel">그 시점 접속의 AI 허용 수준(감사 기록용).</param>
public sealed record AiImageSendNotice(
    string ConnectionName,
    ConnectionEnvironment Environment,
    string RecipientName,
    string? RecipientUrl,
    int Count,
    long TotalBytes,
    bool Masking,
    AiAccessLevel AccessLevel = AiAccessLevel.Off)
{
    public bool IsProduction => Environment == ConnectionEnvironment.Production;

    public string EnvironmentLabel => Environment switch
    {
        ConnectionEnvironment.Production => "운영",
        ConnectionEnvironment.Test => "테스트",
        _ => "개발",
    };

    /// <summary>받는 곳 한 줄: 「예제 LLM · http://llm.example:8080」, 주소가 없으면 이름만.</summary>
    public string Recipient => string.IsNullOrEmpty(RecipientUrl) ? RecipientName : RecipientName + " · " + RecipientUrl;

    /// <summary>이미지 한 줄: 「2장 · 1.8 MB」.</summary>
    public string ImageSummary => $"{Count}장 · {AiImageNormalizer.SizeText(TotalBytes)}";
}

/// <summary>운영 접속 경고 창(시안 ChatWarning)의 모델: 확인하면 true, 취소하면 false 로 닫힌다.</summary>
public sealed partial class AiImageWarningViewModel(AiImageSendNotice notice)
{
    public AiImageSendNotice Notice { get; } = notice ?? throw new ArgumentNullException(nameof(notice));

    /// <summary>창이 닫을 때 쓰는 결과 요청(true = 그대로 보내기).</summary>
    public event EventHandler<bool>? CloseRequested;

    [RelayCommand]
    private void Confirm() => CloseRequested?.Invoke(this, true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
