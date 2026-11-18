using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.Report;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Features.Ai.Chat;

/// <summary>대화 창에 쌓이는 한 줄의 공통 뿌리(사용자 글·도구 카드·AI 글).</summary>
public abstract record ChatEntry;

/// <summary>사용자가 보낸 글(처음 요청 문구나 이어 말하기). 이어 말하기에 이미지를 붙였으면 축소 이미지가 글 위에 함께 보인다(메모리에만 있고 저장하지 않는다).</summary>
public sealed record ChatUserEntry(string Text) : ChatEntry
{
    public IReadOnlyList<ChatImage> Images { get; init; } = [];

    public bool HasImages => Images.Count > 0;

    public bool HasText => !string.IsNullOrEmpty(Text);
}

/// <summary>
/// 대화에 붙인 이미지 한 장. 정규화를 마친 전송용 첨부와 화면용 축소 PNG 를 들고 있다. 축소 이미지(<see cref="Thumbnail"/>)는 처음 그릴 때 만든다
/// (모델 시험이 화면 플랫폼 없이 돌 수 있게).
/// </summary>
#pragma warning disable CA1001 // 축소 이미지(264px 이하)는 대화 줄이 사는 동안 쓰고 대화를 닫을 때 GC 가 거둔다. 줄마다 Dispose 시점을 따로 관리하지 않는다.
public sealed class ChatImage(AiNormalizedImage image)
#pragma warning restore CA1001
{
    private Bitmap? _thumbnail;
    private bool _thumbnailTried;

    /// <summary>모델로 보낼 이미지.</summary>
    public AiImageAttachment Attachment { get; } = image.Attachment;

    internal byte[] PreviewBytes { get; } = image.Preview;

    public int Width { get; } = image.Width;

    public int Height { get; } = image.Height;

    /// <summary>보낼 크기(「1.8 MB」).</summary>
    public string SizeText => AiImageNormalizer.SizeText(Attachment.Data.Length);

    /// <summary>툴팁·접근성 글: 「1280×720 · 340 KB」.</summary>
    public string Description => string.Create(CultureInfo.CurrentCulture, $"{Width}×{Height} · {SizeText}");

    /// <summary>화면에 보일 축소 이미지. 만들지 못하면 null.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailTried)
            {
                _thumbnailTried = true;
                try
                {
                    _thumbnail = new Bitmap(new MemoryStream(PreviewBytes, writable: false));
                }
#pragma warning disable CA1031 // 화면 플랫폼이 없거나 디코딩에 실패하면 축소 이미지 없이 글 칸만 보인다.
                catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                {
                    _thumbnail = null;
                }
            }

            return _thumbnail;
        }
    }

    /// <summary>여러 장의 전송 크기 합.</summary>
    public static long TotalBytes(IEnumerable<ChatImage> images) => images.Sum(i => (long)i.Attachment.Data.Length);
}

/// <summary>보내기를 눌러 확정한 이어 말하기: 글과 붙인 이미지. 호출 쪽이 취소하면 <see cref="AiChatViewModel.RestoreFollowUp"/> 으로 되돌린다.</summary>
public sealed record ChatFollowUp(string Text, IReadOnlyList<ChatImage> Images);

/// <summary>
/// 기능 카드: 튜닝 밖의 흐름(<see cref="IAiChatConversation"/>, 예 AI 파티션 추천 후보)이 대화에 넣는 카드. 모양은 <see cref="Card"/> 형식의
/// DataTemplate 이 정한다(대화 창 <c>AiChatView.axaml</c>). 카드 모델이 자기 단추 명령을 들고 있다.
/// </summary>
public sealed record ChatCardEntry(object Card) : ChatEntry;

/// <summary>AI 가 돌려준 글.</summary>
public sealed record ChatAssistantEntry(string Text) : ChatEntry;

/// <summary>
/// 도구 카드: 도구 이름, 응답 크기, 게이트 판단(허용·거절·실행 중 등). 한 줄 제목·핵심 인자·거부/실패 사유 문장은 <see cref="ToolCardText"/> 표에서 만든다.
/// <see cref="Arguments"/> 는 드라이버의 인자 요약(LLM 이 보낸 글 그대로라 리터럴은 이미 <c>:vN</c> 으로 가려져 있다), <see cref="Note"/> 는 게이트 사유 코드.
/// </summary>
public sealed record ChatToolEntry(string Name, string ResponseSize, string Verdict, bool Done = true, string? CallId = null) : ChatEntry
{
    public string Arguments { get; init; } = string.Empty;

    /// <summary>이 카드가 속한 묶음(한 차례에 이어 부른 도구들). 묶음이 접히면 카드는 감춰진다.</summary>
    public ChatToolGroup? Group { get; init; }

    /// <summary>걸린 초(묶음 합계용, 잴 수 없었으면 0).</summary>
    internal double Seconds { get; init; }

    public string? Note { get; init; }

    /// <summary>부름부터 결과까지 걸린 시간(잴 수 없었거나 아주 짧으면 빈 글).</summary>
    public string Elapsed { get; init; } = string.Empty;

    internal DateTime StartedUtc { get; init; }

    /// <summary>결과가 오기 전에 차례가 끝났다(멈춤·오류). 더는 돌지 않는다.</summary>
    public bool Interrupted { get; init; }

    /// <summary>지금 실행 중인 카드다(앞에 회전 표식을 보인다).</summary>
    public bool IsSpinning => !Done && !Interrupted;

    public bool ShowMark => !IsSpinning;

    /// <summary>카드 앞 상태 표식(시안 9): 끝났으면 ✓, 진행 중이면 …</summary>
    public string Mark => Done ? "✓" : "…";

    /// <summary>사람 말 한 줄(예 「인덱스 정보 조회 · APS.TB_A」). 모르는 도구는 이름 그대로.</summary>
    public string Headline
    {
        get
        {
            var line = ToolCardText.Describe(Name, Arguments);
            return line.Detail is { } detail ? line.Title + " · " + detail : line.Title;
        }
    }

    /// <summary>거부·실패 사유 문장(없으면 null).</summary>
    public string? Reason => ToolCardText.ReasonText(Note);

    public bool HasReason => Reason is not null;

    /// <summary>펼침 칸: 도구 이름, 인자(가린 글 그대로 한 줄에 하나), 사유 코드 원문.</summary>
    public string Details
    {
        get
        {
            var parts = new List<string> { "tool: " + Name };
            if (!string.IsNullOrWhiteSpace(Arguments))
            {
                parts.Add(ToolCardText.Expand(Arguments));
            }

            if (!string.IsNullOrWhiteSpace(Note))
            {
                parts.Add("reason: " + Note);
            }

            return string.Join(Environment.NewLine, parts);
        }
    }
}

/// <summary>
/// 도구 카드 묶음의 머리 줄(st-3): 「도구 N개 실행 · 합계 초 · 모두 허용」. 펼치면 아래에 카드 목록이 보인다.
/// 실행 중에는 펼친 채로 두고 차례가 끝나면 접는다. 카드는 <see cref="AiChatViewModel.Entries"/> 에 그대로 있고 <see cref="ChatToolEntry.Group"/> 으로 묶인다.
/// </summary>
public sealed record ChatToolGroupEntry(ChatToolGroup Group) : ChatEntry;

/// <summary>도구 카드 묶음의 상태(펼침·요약 글). 카드가 늘거나 끝날 때 대화 창 모델이 <see cref="Update"/> 로 다시 센다.</summary>
public sealed partial class ChatToolGroup : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleText))]
    private bool _isExpanded = true;

    /// <summary>머리 줄 오른쪽 글(펼친 채면 「접기」).</summary>
    public string ToggleText => IsExpanded ? "접기" : "펼치기";

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>머리 줄 앞 표식: 진행 중 …, 문제 없이 끝남 ✓, 거부·실패가 있으면 !.</summary>
    [ObservableProperty]
    private string _mark = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDoneMark), nameof(ShowProblemMark))]
    private bool _hasProblem;

    /// <summary>묶음 안에 지금 실행 중인 카드가 있다(머리 줄 앞에 회전 표식을 보인다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDoneMark), nameof(ShowProblemMark))]
    private bool _isToolRunning;

    public bool ShowDoneMark => !IsToolRunning && !HasProblem;

    public bool ShowProblemMark => !IsToolRunning && HasProblem;

    internal void Update(IReadOnlyList<ChatToolEntry> cards)
    {
        var running = cards.Any(c => c.IsSpinning);
        var seconds = cards.Sum(c => c.Seconds);
        var parts = new List<string> { string.Create(CultureInfo.CurrentCulture, $"도구 {cards.Count}개 {(running ? "실행 중" : "실행")}") };
        if (seconds >= 0.1)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0} 초"));
        }

        var done = cards.Where(c => c.Done).ToList();
        var counts = done.GroupBy(c => c.Verdict).Select(g => (Verdict: g.Key, Count: g.Count())).ToList();
        if (counts is [var only] && done.Count == cards.Count)
        {
            parts.Add("모두 " + only.Verdict);
        }
        else
        {
            parts.AddRange(counts.Select(c => string.Create(CultureInfo.CurrentCulture, $"{c.Verdict} {c.Count}")));
        }

        Summary = string.Join(" · ", parts);
        IsToolRunning = running;
        HasProblem = done.Any(c => c.Verdict is "거부" or "실패") || cards.Any(c => c.Interrupted);
        Mark = running ? "…" : HasProblem ? "!" : "✓";
    }
}

/// <summary>
/// 재작성 제안 카드(c6): 비교 창을 거절·닫기 한 뒤에도 대화 창에 남고 「다시 보기」 로 같은 비교 창을 다시 연다. 받아들인 제안은 「적용함」 과
/// 「비교 보기」(원본과 적용한 글을 보기만 하는 비교 창).
/// <see cref="Label"/>·<see cref="Summary"/> 는 AI 가 보낸 글 그대로다(가린 대화면 자리표시 그대로 — 되돌린 SQL 은 <see cref="Reopen"/> 안에만 있다).
/// </summary>
public sealed record ChatProposalEntry(string Label, string Summary, bool Applied) : ChatEntry
{
    /// <summary>벤치마크 경과 변화율(예 「−37%」). 벤치마크가 없으면 null 이고 카드에 안 보인다.</summary>
    public string? Change { get; init; }

    public bool HasChange => !string.IsNullOrEmpty(Change);

    /// <summary>비교 창을 다시 연다. 받아들이면 true, 거절하면 false, 열지 못했으면(탭이 닫힘·문장이 바뀜) null.</summary>
    internal Func<Task<bool?>>? Reopen { get; init; }

    /// <summary>적용한 제안을 보기만 하는 비교 창(원본과 적용한 글)을 연다. 편집기 글은 바꾸지 않는다.</summary>
    internal Func<Task>? View { get; init; }

    public string Headline => "재작성 제안 · " + Label;

    public bool CanReview => !Applied;

    public bool CanView => Applied && View is not null;
}
