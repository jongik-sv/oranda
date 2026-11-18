using System;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.ViewModels;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Connection;
using KitEntryState = Oranda.App.Features.Ai.Kit.AiEntryState;

namespace Oranda.App.Features.Flashback.Ai;

/// <summary>
/// 플래시백 VERSIONS 결과 위의 「차이 요약」 입구와 AI 띠(설계 §4.2, 바로 처리 — 차이 화면 위 한 단락). 결과 영역의 기능 자리
/// (<c>SqlResultPaneViewModel.FeatureStrip</c>)에 붙는다. 가벼운 일 한 번짜리 요청이다.
/// </summary>
public sealed partial class FlashbackDiffSummaryViewModel : ViewModelBase
{
    public const string EntryLabel = "차이 요약";

    public const string NoConnectionText = "이 결과를 만든 접속을 알 수 없어 요약할 수 없습니다.";

    private readonly AiInPlaceHost _host;
    private readonly Func<FlashbackDiffInput> _input;
    private readonly Func<ConnectionProfile?> _profile;

    /// <summary>띠 아래 안내(「값 없이 요약했습니다」).</summary>
    [ObservableProperty]
    private string _note = string.Empty;

    /// <param name="host">AI 연결.</param>
    /// <param name="input">지금까지 받은 버전(누를 때마다 다시 읽는다).</param>
    /// <param name="profile">결과를 만든 세션의 접속.</param>
    public FlashbackDiffSummaryViewModel(AiInPlaceHost host, Func<FlashbackDiffInput> input, Func<ConnectionProfile?> profile)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        Strip = host.CreateStrip(FlashbackDiffSummaryPrompt.Title, "flashback_diff_summary", FlashbackDiffSummaryPrompt.Tier, Source, run => FlashbackDiffSummaryPrompt.Prepare(_input(), run));
        Strip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiInlineStripViewModel.State))
            {
                Note = Strip is { State: AiStripState.Done, LastRequest: { } request }
                    ? string.Join(" ", FlashbackDiffSummaryPrompt.Read(Strip.AnswerText, request).Notes)
                    : string.Empty;
            }
        };
        Entry = new AiEntryButton(EntryLabel, Evaluate, () => Strip.RunAsync(CancellationToken.None));
    }

    /// <summary>요약 띠.</summary>
    public AiInlineStripViewModel Strip { get; }

    /// <summary>「차이 요약」 단추.</summary>
    public AiEntryButton Entry { get; }

    private KitEntryState Evaluate()
    {
        var profile = _profile();
        if (_host.Configured && profile is null)
        {
            return new KitEntryState(false, NoConnectionText);
        }

        return _host.Entry(profile, AiAccessLevel.Metadata, enabledTip: "앞 버전과 달라진 것을 AI 가 한 단락으로 요약합니다");
    }

    private AiInPlaceSource Source()
    {
        var profile = _profile();
        var input = _input();
        var tokens = AiTierRouting.EstimateTokens(string.Join("\n", input.Versions.SelectMany(v => v.Changes.Select(c => c.Column + c.OldValue + c.NewValue))));
        return new AiInPlaceSource(profile, tokens, profile is null ? NoConnectionText : input.Versions.Count == 0 ? FlashbackDiffSummaryPrompt.NothingText : null);
    }
}
