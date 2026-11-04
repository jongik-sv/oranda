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

/// <summary>
/// AI 대화 도구 창 최소판(시안 8): 도구 카드가 진행하는 모습, 멈춤, 이어 말하기 입력. 대화 기록은 저장하지 않는다(창을 닫으면 사라진다).
/// 호출 쪽(튜닝 실행 흐름)이 <see cref="BeginTurn"/>·<see cref="AddToolCall"/>·<see cref="AddAssistantText"/>·<see cref="EndTurn"/> 을 UI 스레드에서 부른다.
/// 셸이 도구 창으로 만들 때는 매개변수 없는 생성자를 쓰고, 그 인스턴스가 <see cref="Current"/> 가 된다.
/// </summary>
[OrandaToolPanel("oranda.panel.aiChat", ToolPanelKind.Tool, DockLocation.Right, Order = 50)]
public sealed partial class AiChatViewModel : ToolPanelViewModelBase
{
    /// <summary>새 소식이 이만큼 없으면 오래 걸린다고 알린다.</summary>
    internal static readonly TimeSpan SlowAfter = TimeSpan.FromMinutes(1);

    /// <summary>도구가 실행 중일 때의 기준. 성능 비교처럼 오래 걸리는 도구가 있어 더 길다.</summary>
    internal static readonly TimeSpan SlowToolAfter = TimeSpan.FromMinutes(5);

    private DateTime _turnStart;
    private DateTime _lastEventUtc;
    private int _turnToolCount;
    private bool _writingAnswer;
    private bool _turnStopped;
    private bool _turnFailed;

    [ObservableProperty]
    private string _title = "대화";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(StopCommand), nameof(ReviewProposalCommand), nameof(ViewProposalCommand), nameof(SaveReportCommand), nameof(NewConversationCommand))]
    [NotifyPropertyChangedFor(nameof(CanType), nameof(CanSaveReport), nameof(CanAttach), nameof(ShowProgressNormal))]
    private bool _isRunning;

    /// <summary>제안 비교 창을 다시 연 채다(그동안 다른 제안을 겹쳐 열지 않는다).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewProposalCommand), nameof(ViewProposalCommand))]
    private bool _isReviewing;

    /// <summary>
    /// 지금 대화가 튜닝 대화다(<see cref="StartConversation"/> 이 불렸다). 튜닝 대화의 이어 말하기는 튜닝 규칙(구조 결과·제안·인덱스 후보)으로 가고,
    /// 아니면 처음부터 열려 있는 일반 대화로 간다(<see cref="StartNewConversation"/> 이 일반 대화로 되돌린다). 입력칸은 어느 쪽이든 열려 있다.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStartHint))]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    private bool _isTuning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = string.Empty;

    /// <summary>진행 줄 글(실행 중에만 있다): 지금 무엇을 하는지.</summary>
    [ObservableProperty]
    private string _progressText = string.Empty;

    /// <summary>진행 줄의 이번 차례 경과 시간.</summary>
    [ObservableProperty]
    private string _progressElapsed = string.Empty;

    /// <summary>새 소식이 오래 없다(진행 줄을 경고 색으로 보이고 멈춤을 안내한다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowProgressNormal))]
    private bool _isProgressSlow;

    /// <summary>끝난 차례의 결과 줄 글. 다음 차례가 시작될 때까지 남는다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _resultMark = string.Empty;

    [ObservableProperty]
    private bool _resultIsCompleted;

    [ObservableProperty]
    private bool _resultIsFailed;

    /// <summary>이미지 첨부 결과 안내(5장 초과·읽지 못함 등). 다음 첨부·빼기·보내기 때 지운다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachNotice))]
    private string? _attachNotice;

    /// <summary>지금 카드를 이어 붙일 도구 묶음(차례가 새로 시작되면 끊긴다).</summary>
    private ChatToolGroup? _openGroup;

    /// <summary>셸이 만든 대화 창 모델(튜닝 명령이 같은 창에 쓴다). 아직 만들어지지 않았으면 null.</summary>
    public static AiChatViewModel? Current { get; private set; }

    public AiChatViewModel()
    {
        Current = this;
        Attachments.CollectionChanged += (_, _) => OnAttachmentsChanged();
        Entries.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowStartHint));
            NewConversationCommand.NotifyCanExecuteChanged();
        };
    }

    public ObservableCollection<ChatEntry> Entries { get; } = [];

    /// <summary>다음 이어 말하기에 실을 이미지(입력칸 위에 보인다). 최대 <see cref="AiImageNormalizer.MaxCount"/> 장.</summary>
    public ObservableCollection<ChatImage> Attachments { get; } = [];

    /// <summary>
    /// 지금 설정(<c>ai</c> 섹션). 활성 프로필의 이미지 지원 판단과 제목 줄의 모델 이름에 쓴다. 셸이 대화 창을 만든 뒤 튜닝 시작 쪽이 연결하며,
    /// 연결이 없으면 모든 것을 허용으로 본다. <c>IAiDriver.Capabilities</c> 는 동기 속성이라 프로필별로 답하지 못해 쓰지 않는다.
    /// </summary>
    internal Func<AiSettings?> SettingsProvider { get; set; } = () => null;

    /// <summary>이미지 입구를 닫는 사유 글(활성 프로필이 이미지를 지원하지 않음). 열려 있으면 null. 지원 여부를 모르는(null) 프로필은 열어 둔다.</summary>
    public string? ImagesBlockedReason =>
        SettingsProvider() is { } settings && (AiActiveProfile.Find(settings) is { Kind: AiActiveKind.Profile, Profile.SupportsImages: false }
            or { Kind: AiActiveKind.SystemCli or AiActiveKind.SystemCliUnavailable, SystemCli.SupportsImages: false }
            or { Kind: AiActiveKind.CustomCli or AiActiveKind.CustomCliUnavailable }) // 기타 CLI 는 글만 주고받는다
            ? ImagesUnsupportedText
            : null;

    public const string ImagesUnsupportedText = "이 모델은 이미지 입력을 지원하지 않습니다. 설정에서 다른 프로필을 고르십시오.";

    /// <summary>입구(붙여 넣기·끌어 놓기·파일 선택)가 열려 있다.</summary>
    public bool ImagesAllowed => ImagesBlockedReason is null;

    /// <summary>
    /// AI 를 쓸 수 없는 원인을 돌려주는 쪽(없으면 null = 쓸 수 있음 또는 모름). 입력칸은 이 원인이 있어도 열려 있고, 원인을 입력칸 위에 보인다.
    /// 셸이 대화 창을 만든 뒤 튜닝 쪽 조립이 연결한다.
    /// </summary>
    internal Func<string?> UnavailableProvider { get; set; } = () => null;

    /// <summary>AI 를 쓸 수 없는 원인 글(드라이버 없음·모델 선택 안 함·프로필 없음 등). 쓸 수 있으면 null.</summary>
    public string? UnavailableReason => UnavailableProvider();

    public bool HasUnavailableReason => UnavailableReason is not null;

    /// <summary>입력칸 위 안내: 일반 대화 시작 전(줄이 하나도 없음)이고 AI 를 쓸 수 없는 원인도 없을 때 보인다.</summary>
    public bool ShowStartHint => !IsTuning && Entries.Count == 0 && !HasUnavailableReason;

    /// <summary>제목 줄 오른쪽의 모델 이름: 프로필 이름 또는 Claude Code. 설정을 모르면 빈 글.</summary>
    public string ModelLabel => SettingsProvider() is { } settings
        ? AiActiveProfile.Find(settings) switch
        {
            { Kind: AiActiveKind.Profile or AiActiveKind.ProfileInvalid, Profile: { } profile } => profile.Name,
            { Kind: AiActiveKind.ClaudeCode } => "Claude Code",
            { Kind: AiActiveKind.SystemCli or AiActiveKind.SystemCliUnavailable, SystemCli: { } cli } => cli.DisplayName,
            { Kind: AiActiveKind.CustomCli, CustomCli: { } custom } => custom.Name.Trim().Length > 0 ? custom.Name.Trim() : custom.Id,
            _ => string.Empty,
        }
        : string.Empty;

    /// <summary>더 붙일 수 있는 장수(입구가 읽을 파일 수를 미리 자르는 데 쓴다).</summary>
    public int RemainingSlots => Math.Max(0, AiImageNormalizer.MaxCount - Attachments.Count);

    public bool HasAttachments => Attachments.Count > 0;

    public bool HasAttachNotice => !string.IsNullOrEmpty(AttachNotice);

    /// <summary>이미지를 더 붙일 수 있다: 입력이 열려 있고 입구가 열려 있으며 장수가 남았다.</summary>
    public bool CanAttach => CanType && ImagesAllowed && Attachments.Count < AiImageNormalizer.MaxCount;

    /// <summary>입력칸 아래 상태 줄: 이미지를 붙였으면 「이미지 N장 · 최대 5장」. 실행 상태는 대화 끝의 진행 줄·결과 줄이 보인다.</summary>
    public string FooterText => Attachments.Count > 0
        ? string.Create(CultureInfo.CurrentCulture, $"이미지 {Attachments.Count}장 · 최대 {AiImageNormalizer.MaxCount}장")
        : string.Empty;

    public bool ShowProgressNormal => IsRunning && !IsProgressSlow;

    public bool HasResult => ResultText.Length > 0;

    /// <summary>지금 시각(시험에서 바꾼다).</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>설정이 바뀌었을 수 있다(프로필 교체 등): 입구·모델 이름·보내기 상태를 다시 계산한다.</summary>
    public void RefreshActiveModel()
    {
        OnPropertyChanged(nameof(ImagesBlockedReason));
        OnPropertyChanged(nameof(ImagesAllowed));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(CanAttach));
        OnPropertyChanged(nameof(UnavailableReason));
        OnPropertyChanged(nameof(HasUnavailableReason));
        OnPropertyChanged(nameof(ShowStartHint));
        SendCommand.NotifyCanExecuteChanged();
    }

    private void OnAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        OnPropertyChanged(nameof(CanAttach));
        OnPropertyChanged(nameof(FooterText));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 이미지 바이트를 정규화해 붙인다(디코딩은 백그라운드). 입구가 닫혔거나 5장을 넘기거나 읽지 못한 것은 붙이지 않고 <see cref="AttachNotice"/> 에 이유를 남긴다.
    /// 붙인 장수를 돌려준다.
    /// </summary>
    public async Task<int> AddImagesAsync(IEnumerable<byte[]> blobs)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        AttachNotice = null;
        if (!CanType)
        {
            return 0;
        }

        if (ImagesBlockedReason is { } blocked)
        {
            AttachNotice = blocked;
            return 0;
        }

        var added = 0;
        var notices = new List<string>();
        foreach (var blob in blobs)
        {
            if (Attachments.Count >= AiImageNormalizer.MaxCount)
            {
                notices.Add(string.Create(CultureInfo.CurrentCulture, $"이미지는 한 번에 {AiImageNormalizer.MaxCount}장까지 붙일 수 있습니다."));
                break;
            }

            var result = await Task.Run(() => AiImageNormalizer.Normalize(blob)).ConfigureAwait(true);

            // 디코딩하는 사이 다른 입구(붙여 넣기·끌어 놓기)가 장수를 채우거나 보내기가 눌렸을 수 있다: 붙이기 직전에 다시 확인한다.
            if (!CanType)
            {
                notices.Add("이어 말하는 중이라 이미지를 붙이지 못했습니다. 끝난 뒤 다시 붙여 주세요.");
                break;
            }

            if (Attachments.Count >= AiImageNormalizer.MaxCount)
            {
                notices.Add(string.Create(CultureInfo.CurrentCulture, $"이미지는 한 번에 {AiImageNormalizer.MaxCount}장까지 붙일 수 있습니다."));
                break;
            }

            if (result.Image is { } image)
            {
                Attachments.Add(new ChatImage(image));
                added++;
            }
            else
            {
                notices.Add(result.Error ?? "이미지를 붙이지 못했습니다.");
            }
        }

        OnAttachmentsChanged();
        AttachNotice = notices.Count == 0 ? null : string.Join(' ', notices.Distinct());
        return added;
    }

    [RelayCommand]
    private void RemoveAttachment(ChatImage? image)
    {
        if (image is not null && Attachments.Remove(image))
        {
            AttachNotice = null;
        }
    }

    /// <summary>
    /// 취소한 이어 말하기를 입력칸으로 되돌린다: 글은 입력칸이 비어 있으면 그대로, 그 사이 새 글이 있으면 원래 글을 앞에 두고 줄을 바꿔 잇는다(어느 쪽도 잃지 않는다).
    /// 이미지는 앞쪽에 다시 붙이고, 장수 상한 때문에 못 붙인 것이 있으면 <see cref="AttachNotice"/> 로 알린다.
    /// </summary>
    public void RestoreFollowUp(ChatFollowUp followUp)
    {
        ArgumentNullException.ThrowIfNull(followUp);
        if (followUp.Text.Length > 0)
        {
            Draft = string.IsNullOrWhiteSpace(Draft) ? followUp.Text : followUp.Text + Environment.NewLine + Draft;
        }

        var pending = followUp.Images.Where(i => !Attachments.Contains(i)).ToList();
        var room = RemainingSlots;
        foreach (var (image, index) in pending.Take(room).Select((i, n) => (i, n)))
        {
            Attachments.Insert(index, image);
        }

        if (pending.Count > room)
        {
            AttachNotice = string.Create(CultureInfo.CurrentCulture, $"이미지는 한 번에 {AiImageNormalizer.MaxCount}장까지 붙일 수 있어 일부를 되돌리지 못했습니다.");
        }

        OnAttachmentsChanged();
    }

    /// <summary>레포트로 저장할 재료(튜닝 결과를 반영한 뒤에 생긴다). 새 대화를 열면 비운다.</summary>
    private AiTuningReportSource? _reportSource;

    private bool _isSavingReport;

    /// <summary>지금 저장할 레포트 재료(시험이 튜닝 흐름이 넘긴 내용을 확인하는 용도).</summary>
    internal AiTuningReportSource? ReportSource => _reportSource;

    /// <summary>저장 창을 띄우는 쪽. 기본은 편집기의 대화상자 모음이고 시험에서 가짜로 바꾼다.</summary>
    internal Func<IEditorDialogs> DialogsProvider { get; set; } = () => SqlEditorHost.Current.Dialogs;

    /// <summary>파일에 쓰는 동작(경로, 글). 시험에서 실패하는 쪽으로 바꾼다.</summary>
    internal Func<string, string, CancellationToken, Task> FileWriter { get; set; } =
        (path, text, ct) => File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct);

    /// <summary>튜닝 결과가 있는 대화이고 실행 중이 아니다: 「레포트 저장」 을 쓸 수 있다.</summary>
    public bool CanSaveReport => _reportSource is not null && !IsRunning && !_isSavingReport;

    /// <summary>튜닝 흐름이 결과를 반영한 뒤 레포트 재료를 넘긴다(가린 대화의 자리표시는 그대로).</summary>
    public void SetReportSource(AiTuningReportSource? source)
    {
        _reportSource = source;
        OnPropertyChanged(nameof(CanSaveReport));
        SaveReportCommand.NotifyCanExecuteChanged();
    }

    /// <summary>입력 칸에 쓸 수 있다: 처음부터 열려 있고, 실행 중이 아니며 레포트를 저장하는 중도 아니다(저장 중에 보낸 글이 레포트와 어긋나지 않게).</summary>
    public bool CanType => !IsRunning && !_isSavingReport;

    /// <summary>이어 말하기를 보냈다(인수 = 보낸 글과 붙인 이미지). 호출 쪽이 같은 세션으로 다시 실행한다. 입력칸과 첨부는 이 사건 전에 비운다.</summary>
    public event EventHandler<ChatFollowUp>? FollowUpRequested;

    /// <summary>멈춤을 눌렀다.</summary>
    public event EventHandler? StopRequested;

    /// <summary>새 튜닝 대화를 연다(앞의 줄은 지운다).</summary>
    public void StartConversation(string title)
    {
        ResetConversation(title);
        IsTuning = true;
    }

    /// <summary>
    /// 새 일반 대화로 되돌린다(앞의 줄·세션 이어받기 id·레포트 재료를 지운다). 일반 대화든 튜닝 대화든 쓸 수 있고, 실행 중이거나 레포트를 저장하는 중에는 쓰지 않는다.
    /// 입력칸에 쓰던 글과 붙인 이미지는 그대로 둔다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartNew))]
    private void NewConversation() => StartNewConversation();

    private bool CanStartNew() => !IsRunning && !IsBusy && !_isSavingReport && (IsTuning || Entries.Count > 0 || ResumeId is not null);

    /// <summary>
    /// 대화 세대: 새 대화(<see cref="StartConversation"/>·<see cref="StartNewConversation"/>)나 이어받기 id 를 잊는 일(<see cref="ForgetResume"/>)마다 늘어난다.
    /// 보내기를 준비하거나 결과를 반영하는 흐름이 시작 때의 값을 기억했다가 끝에서 대조해, 그 사이 대화가 바뀌었으면 옛 대화의 요청·레포트 재료·줄이 새 대화에 섞이지 않게 한다.
    /// </summary>
    public int ConversationGeneration { get; private set; }

    /// <summary>
    /// 흐름이 보내기를 준비하거나(확인 창·감사) 튜닝 결과를 반영하는 중이다(실행 중이 아니어도). 이 동안 「새 대화」 는 쓸 수 없다.
    /// 흐름이 시작과 끝에서 켜고 끈다.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged();
                NewConversationCommand.NotifyCanExecuteChanged();
            }
        }
    }

    private bool _isBusy;

    /// <summary>새 일반 대화로 되돌린다(<see cref="NewConversationCommand"/> 와 같다).</summary>
    public void StartNewConversation()
    {
        var draft = Draft;
        var attachments = Attachments.ToList();
        ResetConversation("대화");
        Draft = draft;
        foreach (var image in attachments)
        {
            Attachments.Add(image);
        }

        IsTuning = false;
    }

    private void ResetConversation(string title)
    {
        ConversationGeneration++;
        Entries.Clear();
        SetReportSource(null); // 앞 대화의 결과로 새 대화의 레포트를 저장하지 않게
        ResumeId = null; // 앞 대화의 claude 세션을 새 대화가 이어 받지 않게
        Title = title;
        Draft = string.Empty;
        Attachments.Clear();
        AttachNotice = null;
        _openGroup = null;
        IsRunning = false;
        ClearResult();
        RefreshProgress();
        OnPropertyChanged(nameof(ShowStartHint));
        NewConversationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>한 번의 실행을 시작한다. <paramref name="userText"/> 를 사용자 줄로 보이고, <paramref name="images"/> 가 있으면 그 줄에 축소 이미지를 함께 보인다.</summary>
    public void BeginTurn(string userText, IReadOnlyList<ChatImage>? images = null)
    {
        Entries.Add(new ChatUserEntry(userText) { Images = images ?? [] });
        _openGroup = null;
        _turnStart = _lastEventUtc = UtcNow();
        _turnToolCount = 0;
        _writingAnswer = false;
        _turnStopped = false;
        _turnFailed = false;
        ClearResult();
        IsRunning = true;
        RefreshProgress();
    }

    public void AddToolCall(string name, string responseSize, string verdict, bool done = true, string? callId = null, string arguments = "")
        => AddToolCard(new ChatToolEntry(name, responseSize, verdict, done, callId) { Arguments = arguments });

    /// <summary>카드를 넣는다. 바로 앞이 지금 묶음의 머리 줄·카드가 아니면(차례 시작, 사이에 AI 글) 새 묶음 머리 줄부터 넣는다.</summary>
    private void AddToolCard(ChatToolEntry card)
    {
        var continues = _openGroup is { } open && Entries.Count > 0
            && (Entries[^1] is ChatToolEntry last && last.Group == open || Entries[^1] is ChatToolGroupEntry head && head.Group == open);
        if (!continues)
        {
            _openGroup = new ChatToolGroup { IsExpanded = IsRunning };
            Entries.Add(new ChatToolGroupEntry(_openGroup));
        }

        Entries.Add(card with { Group = _openGroup });
        _turnToolCount++;
        UpdateGroup(_openGroup!);
        RefreshProgress();
    }

    private void UpdateGroup(ChatToolGroup group) => group.Update(Entries.OfType<ChatToolEntry>().Where(c => c.Group == group).ToList());

    public void AddAssistantText(string text) => Entries.Add(new ChatAssistantEntry(text));

    /// <summary>제안 카드를 남긴다. <paramref name="reopen"/> 은 같은 제안의 비교 창을 다시 여는 호출 쪽 동작, <paramref name="change"/> 는 벤치마크 변화율(없으면 null).</summary>
    public void AddProposal(string label, string summary, bool applied, Func<Task<bool?>> reopen, string? change = null, Func<Task>? view = null)
        => Entries.Add(new ChatProposalEntry(label, summary, applied) { Reopen = reopen, Change = change, View = view });

    /// <summary>앞 대화를 잇는 id(<see cref="AiStarted"/>·<see cref="AiFinal"/> 이 알려 준다). 이어 말하기를 보낼 때 호출 쪽이 읽어 요청에 싣는다.</summary>
    public string? ResumeId { get; private set; }

    /// <summary>앞 대화를 잇는 id 를 잊는다(다음 보내기가 새 대화가 된다). 줄은 그대로 둔다: 보낼 곳이 바뀐 일반 대화가 새 모델로 앞 대화를 보내지 않게 한다.</summary>
    public void ForgetResume()
    {
        ConversationGeneration++;
        ResumeId = null;
        NewConversationCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 드라이버 사건 하나를 화면에 옮긴다(UI 스레드). 도구 부름은 진행 중 카드로 두었다가 같은 호출 id 의 결과가 오면 크기·게이트 판단으로 바꾼다.
    /// 글 조각은 마지막 AI 글 줄에 이어 붙이고, 끝·오류가 오면 실행을 끝낸다.
    /// </summary>
    public void Apply(AiEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        _lastEventUtc = UtcNow();
        switch (e)
        {
            case AiStarted started:
                ResumeId = started.ResumeId ?? ResumeId;
                break;
            case AiTextDelta delta:
                _writingAnswer = true;
                if (Entries.Count > 0 && Entries[^1] is ChatAssistantEntry last)
                {
                    Entries[^1] = last with { Text = last.Text + delta.Text };
                }
                else
                {
                    AddAssistantText(delta.Text);
                }

                break;
            case AiToolCall call:
                _writingAnswer = false;
                AddToolCard(new ChatToolEntry(call.ToolName, string.Empty, "실행 중", Done: false, call.CallId) { Arguments = call.ArgumentsSummary, StartedUtc = DateTime.UtcNow });
                break;
            case AiToolResult result:
                _writingAnswer = false;
                CompleteToolCall(result);
                break;
            case AiNeedsChoice choice:
                AddAssistantText($"{choice.Question}\n" + string.Join('\n', choice.Options.Select((o, i) => string.Create(CultureInfo.InvariantCulture, $"{i + 1}) {o}"))));
                EndTurn();
                break;
            case AiFinal final:
                ResumeId = final.ResumeId ?? ResumeId;
                if (!string.IsNullOrEmpty(final.Text) && !(Entries.Count > 0 && Entries[^1] is ChatAssistantEntry))
                {
                    AddAssistantText(final.Text);
                }

                EndTurn();
                break;
            case AiError error:
                _turnFailed = true;
                AddAssistantText("오류: " + AiErrorText.Describe(error));
                EndTurn();
                break;
        }

        RefreshProgress();
    }

    /// <summary>드라이버가 내는 사건 흐름을 끝까지 화면에 옮긴다. 흐름이 끝나거나 취소·예외로 멈추면 실행 중 표시를 내린다.</summary>
    public async Task RunAsync(IAsyncEnumerable<AiEvent> events, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        try
        {
            await foreach (var e in events.WithCancellation(ct).ConfigureAwait(true))
            {
                Apply(e);
            }
        }
        catch (OperationCanceledException)
        {
            // 멈춤: 아래 finally 가 실행 중 표시를 내린다.
            _turnStopped = true;
        }
        finally
        {
            EndTurn();
        }
    }

    private void CompleteToolCall(AiToolResult result)
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i] is ChatToolEntry { Done: false } card && card.CallId == result.CallId)
            {
                var seconds = SecondsSince(card.StartedUtc);
                Entries[i] = card with
                {
                    ResponseSize = SizeText(result.Size), Verdict = VerdictText(result.Judgement), Done = true, Note = result.Note,
                    Elapsed = ElapsedText(seconds), Seconds = seconds,
                };
                if (card.Group is { } group)
                {
                    UpdateGroup(group);
                }

                return;
            }
        }

        AddToolCard(new ChatToolEntry(result.ToolName, SizeText(result.Size), VerdictText(result.Judgement), true, result.CallId) { Note = result.Note });
    }

    private static string SizeText(int size) => string.Create(CultureInfo.CurrentCulture, $"{size:N0} 자");

    private static double SecondsSince(DateTime startedUtc) => startedUtc == default ? 0 : Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);

    private static string ElapsedText(double seconds) =>
        seconds < 0.1 ? string.Empty : string.Create(CultureInfo.CurrentCulture, $"{seconds:0.0} 초");

    private static string VerdictText(AiToolJudgement judgement) => judgement switch
    {
        AiToolJudgement.Allowed => "허용",
        AiToolJudgement.Masked => "가림",
        AiToolJudgement.Denied => "거부",
        AiToolJudgement.Failed => "실패",
        _ => judgement.ToString(),
    };

    /// <summary>실행이 끝났거나 멈췄다.</summary>
    public void EndTurn()
    {
        var wasRunning = IsRunning;
        IsRunning = false;
        MarkUnfinishedToolsInterrupted();
        foreach (var group in Entries.OfType<ChatToolGroupEntry>())
        {
            group.Group.IsExpanded = false; // 끝난 차례의 도구 카드는 한 줄 요약으로 접는다
        }

        if (wasRunning)
        {
            ShowResult();
        }

        RefreshProgress();
    }

    // 결과가 오지 않은 카드는 차례가 끝나면 더 돌지 않는다.
    private void MarkUnfinishedToolsInterrupted()
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i] is ChatToolEntry { IsSpinning: true } card)
            {
                Entries[i] = card with { Interrupted = true, Verdict = "중단" };
                if (card.Group is { } group)
                {
                    UpdateGroup(group);
                }
            }
        }
    }

    private void ShowResult()
    {
        var tail = string.Create(CultureInfo.CurrentCulture, $"{DurationText(UtcNow() - _turnStart)} · 도구 {_turnToolCount}개");
        (ResultMark, ResultText) = _turnStopped ? ("■", "멈춤 · " + tail)
            : _turnFailed ? ("!", "오류로 끝남 · " + tail)
            : ("✓", "완료 · " + tail);
        ResultIsCompleted = !_turnStopped && !_turnFailed;
        ResultIsFailed = !_turnStopped && _turnFailed;
    }

    private void ClearResult()
    {
        ResultText = string.Empty;
        ResultMark = string.Empty;
        ResultIsCompleted = false;
        ResultIsFailed = false;
    }

    /// <summary>진행 줄을 지금 상태로 다시 계산한다. 사건이 없어도 초가 흐르도록 화면이 1초마다 부른다.</summary>
    public void RefreshProgress()
    {
        if (!IsRunning)
        {
            ProgressText = string.Empty;
            ProgressElapsed = string.Empty;
            IsProgressSlow = false;
            return;
        }

        var now = UtcNow();
        var tool = Entries.OfType<ChatToolEntry>().LastOrDefault(c => c.IsSpinning);
        var slowAfter = tool is null ? SlowAfter : SlowToolAfter;
        ProgressElapsed = DurationText(now - _turnStart);
        IsProgressSlow = now - _lastEventUtc >= slowAfter;
        ProgressText = IsProgressSlow ? string.Create(CultureInfo.CurrentCulture, $"AI 작업 중 · {slowAfter.TotalMinutes:0}분 넘게 새 소식 없음")
            : tool is not null ? "AI 작업 중 · " + ToolCardText.Describe(tool.Name, tool.Arguments).Title
            : _writingAnswer ? "답변 작성 중"
            : "AI 응답을 기다리는 중";
    }

    /// <summary>「14초」, 「1분 12초」, 「2분」.</summary>
    private static string DurationText(TimeSpan elapsed)
    {
        var total = (int)Math.Max(0, elapsed.TotalSeconds);
        var (minutes, seconds) = (total / 60, total % 60);
        return minutes == 0 ? string.Create(CultureInfo.CurrentCulture, $"{seconds}초")
            : seconds == 0 ? string.Create(CultureInfo.CurrentCulture, $"{minutes}분")
            : string.Create(CultureInfo.CurrentCulture, $"{minutes}분 {seconds}초");
    }

    // 글이나 이미지 중 하나는 있어야 한다. 이미지를 붙인 뒤 지원하지 않는 프로필로 바뀌었으면 막는다(글만 보내려면 이미지를 뺀다).
    private bool CanSend() => CanType && (!string.IsNullOrWhiteSpace(Draft) || Attachments.Count > 0) && (Attachments.Count == 0 || ImagesAllowed);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        if (FollowUpRequested is null)
        {
            AddAssistantText("AI 연결이 아직 준비되지 않아 보내지 못했습니다. 잠시 뒤 다시 보내세요."); // 입력을 비우지 않는다: 받는 쪽이 없으면 글이 사라진다
            return;
        }

        var followUp = new ChatFollowUp(Draft.Trim(), [.. Attachments]);
        Draft = string.Empty; // 이벤트 전에 비운다. 취소되면 호출 쪽이 RestoreFollowUp 으로 되돌린다
        Attachments.Clear();
        AttachNotice = null;
        FollowUpRequested?.Invoke(this, followUp);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _turnStopped = true;
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanReviewProposal(ChatProposalEntry? entry) => entry is { Applied: false, Reopen: not null } && !IsRunning && !IsReviewing;

    /// <summary>「다시 보기」: 제안 비교 창을 다시 연다. 받아들이면 카드를 「적용함」 으로 바꾼다. 실행 중에는 열지 않는다.</summary>
    [RelayCommand(CanExecute = nameof(CanReviewProposal))]
    private async Task ReviewProposalAsync(ChatProposalEntry? entry)
    {
        if (entry?.Reopen is not { } reopen)
        {
            return;
        }

        IsReviewing = true;
        try
        {
            if (await reopen().ConfigureAwait(true) == true && Entries.IndexOf(entry) is >= 0 and var i)
            {
                Entries[i] = entry with { Applied = true };
            }
        }
        finally
        {
            IsReviewing = false;
        }
    }

    private bool CanViewProposal(ChatProposalEntry? entry) => entry is { CanView: true } && !IsReviewing;

    /// <summary>「비교 보기」: 적용한 제안의 원본과 적용한 글을 비교 창으로 다시 본다(닫기만 있음).</summary>
    [RelayCommand(CanExecute = nameof(CanViewProposal))]
    private async Task ViewProposalAsync(ChatProposalEntry? entry)
    {
        if (entry?.View is not { } view)
        {
            return;
        }

        IsReviewing = true;
        try
        {
            await view().ConfigureAwait(true);
        }
        finally
        {
            IsReviewing = false;
        }
    }

    /// <summary>
    /// 「레포트 저장」: 형식(.md·.html)은 저장 창에서 고른 파일 확장자로 정한다. 저장하면 경로 한 줄을, 쓰지 못하면 사유 한 줄을 대화 창에 남긴다(예외는 밖으로 내지 않는다).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveReport))]
    private async Task SaveReportAsync()
    {
        if (_reportSource is not { } source)
        {
            return;
        }

        _isSavingReport = true;
        NotifySavingChanged();
        try
        {
            var suggested = AiTuningReportFiles.SuggestName(source.CreatedAt, AiTuningReportFormat.Html);
            var picked = await DialogsProvider().PickReportSaveFileAsync(suggested, CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(picked))
            {
                return; // 취소
            }

            if (IsRunning)
            {
                AddAssistantText(AiTuningReportAssembler.FailedPrefix + "이어 말하는 중입니다. 끝난 뒤 다시 저장하세요."); // 저장 창이 떠 있는 사이 다른 경로로 시작된 차례
                return;
            }

            if (!AiTuningReportFiles.TryResolve(picked, out var format))
            {
                AddAssistantText(AiTuningReportAssembler.FailedPrefix + "파일 확장자는 .md 또는 .html 이어야 합니다.");
                return;
            }

            var path = picked;
            try
            {
                var input = AiTuningReportAssembler.Build(_reportSource ?? source, Title, Entries); // 저장 창이 떠 있는 사이 갱신된 재료를 쓴다
                await FileWriter(path, AiTuningReportBuilder.Build(input, format), CancellationToken.None).ConfigureAwait(true);
                AddAssistantText(AiTuningReportAssembler.SavedPrefix + path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                AddAssistantText(AiTuningReportAssembler.FailedPrefix + ex.Message);
            }
        }
        finally
        {
            _isSavingReport = false;
            NotifySavingChanged();
        }
    }

    private void NotifySavingChanged()
    {
        OnPropertyChanged(nameof(CanSaveReport));
        OnPropertyChanged(nameof(CanType));
        OnPropertyChanged(nameof(CanAttach));
        SaveReportCommand.NotifyCanExecuteChanged();
        SendCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
    }
}
