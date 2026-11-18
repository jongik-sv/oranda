using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.ViewModels;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using Oranda.Core.Monitoring;
using KitEntryState = Oranda.App.Features.Ai.Kit.AiEntryState;

namespace Oranda.App.Features.LockMonitor.Ai;

/// <summary>
/// 락 모니터 「AI 풀이」·세션 모니터 「대기 풀이」(설계 §3.2, 바로 처리 — 모니터 아래 AI 띠). 입구 단추·띠·「더 깊게」(주 모델로 채팅창 새 대화)를 한데 둔다.
/// 받은 풀이의 결론 한 줄은 KILL 확인 창에 다시 보인다(<see cref="KillLineFor"/>, 새 요청 없음).
/// </summary>
public sealed partial class LockExplainPanelViewModel : ViewModelBase
{
    /// <summary>고른 줄이 없을 때.</summary>
    public const string NoSelectionText = "풀이할 줄을 먼저 고르세요.";

    /// <summary>모니터 세션이 없을 때.</summary>
    public const string NotOpenText = "모니터가 열려 있지 않습니다.";

    private readonly AiInPlaceHost _host;
    private readonly LockExplainKind _kind;
    private readonly Func<LockExplainInput?> _input;
    private readonly Func<ConnectionProfile?> _profile;
    private LockExplainInput? _sent;

    /// <summary>띠 아래 안내(자리표시로 보냄·SQL 뺌, 「더 깊게」 를 보내지 못한 까닭).</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    /// <summary>마지막 풀이(틀대로 읽은 것).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDeeperVisible))]
    private LockExplainResult? _result;

    /// <param name="host">AI 연결.</param>
    /// <param name="kind">락 사슬 또는 세션 대기.</param>
    /// <param name="input">지금 고른 줄로 만든 입력(누를 때마다 다시 읽는다). 고른 줄이 없으면 null.</param>
    /// <param name="profile">모니터 세션의 접속.</param>
    public LockExplainPanelViewModel(AiInPlaceHost host, LockExplainKind kind, Func<LockExplainInput?> input, Func<ConnectionProfile?> profile)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _kind = kind;
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        var title = kind == LockExplainKind.LockChain ? LockExplainPrompt.ChainTitle : LockExplainPrompt.SessionTitle;
        Strip = host.CreateStrip(title, "lock_explain", LockExplainPrompt.Tier, Source, Prepare);
        Strip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiInlineStripViewModel.State))
            {
                OnStripState();
            }
        };
        Entry = new AiEntryButton(kind == LockExplainKind.LockChain ? "AI 풀이" : "대기 풀이", Evaluate, () => Strip.RunAsync(CancellationToken.None));
    }

    /// <summary>풀이 띠.</summary>
    public AiInlineStripViewModel Strip { get; }

    /// <summary>입구 단추.</summary>
    public AiEntryButton Entry { get; }

    /// <summary>「더 깊게」 를 보인다(받은 풀이가 있고 채팅창을 쓸 수 있을 때).</summary>
    public bool CanDeeperVisible => Result is not null && _host.ChatHost is not null;

    /// <summary>고른 줄·접속이 바뀌었을 때 입구를 다시 계산한다(모니터가 부른다).</summary>
    public void Refresh() => Entry.Refresh();

    /// <summary>KILL 확인 창 한 줄: 받은 풀이가 그 세션을 담았고 결론이 있으면 「AI 풀이: …」, 아니면 null(숨김).</summary>
    public string? KillLineFor(SessionKey target) =>
        _sent is { } sent && sent.Sessions.Any(s => s.Key == target) ? LockExplainPrompt.KillDialogLine(Result) : null;

    /// <summary>「더 깊게」: 주 모델로 채팅창에 새 대화(띠의 대화는 넘기지 않는다).</summary>
    [RelayCommand]
    private async Task DeeperAsync()
    {
        var why = await _host.OpenDeeperAsync(Strip.Title, "lock_explain", Source, (run, _) => Task.FromResult(Prepare(run))).ConfigureAwait(true);
        if (why is not null)
        {
            Note = why;
        }
    }

    private AiStripPreparation Prepare(AiOneShotRun run)
    {
        if (_input() is not { } input)
        {
            return AiStripPreparation.Refuse(NoSelectionText);
        }

        var prepared = LockExplainPrompt.Prepare(input, run, _host.Masker);
        if (prepared.Request is not null)
        {
            _sent = input;
        }

        return prepared;
    }

    private void OnStripState()
    {
        if (Strip is { State: AiStripState.Done, LastRequest: { } request } && _sent is { } sent)
        {
            Result = LockExplainPrompt.Read(Strip.AnswerText, sent, request);
            Note = string.Join(" ", Result.Notes);
        }
        else if (Strip.IsRunning)
        {
            Result = null;
            Note = string.Empty;
        }

        Entry.Refresh();
    }

    private KitEntryState Evaluate()
    {
        var profile = _profile();
        if (_host.Configured && profile is null)
        {
            return new KitEntryState(false, NotOpenText);
        }

        return _host.Entry(profile, AiAccessLevel.Plan, _input() is null ? NoSelectionText : null,
            _kind == LockExplainKind.LockChain ? "고른 줄의 차단 사슬을 AI 가 풀이합니다(실행하지 않습니다)" : "고른 세션이 무엇을 기다리는지 AI 가 풀이합니다");
    }

    private AiInPlaceSource Source()
    {
        var profile = _profile();
        var input = _input();
        var tokens = input is null ? 0 : AiTierRouting.EstimateTokens(string.Join("\n", input.Sessions.Select(s => s.SqlText + s.PrevSqlText)));
        return new AiInPlaceSource(profile, tokens + (input?.Sessions.Count ?? 0) * 80, profile is null ? NotOpenText : input is null ? NoSelectionText : null);
    }
}
