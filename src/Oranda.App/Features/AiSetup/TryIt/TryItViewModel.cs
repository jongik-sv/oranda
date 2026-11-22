using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Shell;
using Oranda.Core.Connection;

namespace Oranda.App.Features.AiSetup.TryIt;

/// <summary>답 뒤에 보이는 자주 쓰는 기능 한 줄: 이름과 누르는 곳.</summary>
public sealed record TryItTip(string Title, string Where);

/// <summary>
/// 「해 보기」(마지막 단계). 켠 접속 하나(여럿이면 고르기, 운영은 맨 뒤)로 연습 탭을 열고 <c>SELECT * FROM dual</c> 의 SQL 설명을 받는다.
/// 답이 오면 끝나고 자주 쓰는 3기능을 안내한다. 고급 확인란을 켜고 끝내면 외부 에이전트 연결 창을 이어서 연다.
/// </summary>
public sealed partial class TryItViewModel : ObservableObject, IDisposable
{
    /// <summary>연습 탭에 넣는 문장.</summary>
    public const string PracticeSql = "SELECT * FROM dual";

    private readonly ITryItActions _actions;
    private readonly IShellText? _shellText;
    private readonly TryItText _t;
    private Guid? _tabId;
    private Guid? _tabProfileId;
    private Func<string>? _status;

    /// <param name="actions">편집기·런처·외부 에이전트 창.</param>
    /// <param name="enabled">AI 를 켠 접속(접속 단계가 넘긴다).</param>
    /// <param name="text">한/영 글.</param>
    public TryItViewModel(ITryItActions actions, IReadOnlyList<ConnectionProfile> enabled, IShellText? text)
    {
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(enabled);
        _actions = actions;
        _shellText = text;
        _t = new TryItText(text);
        Connections = [.. enabled.OrderBy(p => p.Environment == ConnectionEnvironment.Production)];
        _selectedConnection = Connections.Count > 0 ? Connections[0] : null;
        if (text is not null)
        {
            text.LanguageChanged += OnLanguageChanged;
        }
    }

    public IReadOnlyList<ConnectionProfile> Connections { get; }

    /// <summary>켠 접속이 둘 이상이라 고르는 칸을 보인다.</summary>
    public bool ShowPicker => Connections.Count > 1;

    /// <summary>켠 접속이 없어 이 단계를 건너뛴다.</summary>
    public bool IsSkipped => Connections.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenTab), nameof(CanExplain))]
    private ConnectionProfile? _selectedConnection;

    [ObservableProperty]
    private bool _tabOpened;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenTab), nameof(CanExplain))]
    private bool _isWaiting;

    /// <summary>답을 받아 이 단계가 끝났다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTips), nameof(Title))]
    private bool _isComplete;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _answerText = string.Empty;

    /// <summary>「검은 창의 Claude Code 에서도 Oranda 시키기(고급)」. 기본 꺼짐.</summary>
    [ObservableProperty]
    private bool _alsoExternalAgent;

    public bool CanOpenTab => SelectedConnection is not null && !IsWaiting;

    public bool CanExplain => CanOpenTab;

    public bool ShowTips => IsComplete;

    public string Title => IsComplete ? _t.DoneTitle : _t.Title;

    public string Intro => IsSkipped ? _t.Skipped : _t.Intro;

    public string PickLabel => _t.PickConnection;

    public string OpenTabLabel => _t.OpenTab;

    public string ExplainLabel => _t.Explain;

    public string AnswerNote => _t.AnswerNote;

    public string AlsoExternalAgentLabel => _t.AlsoExternalAgent;

    public IReadOnlyList<TryItTip> Tips =>
    [
        new(_t.TipTuneTitle, _t.TipTuneWhere),
        new(_t.TipFixTitle, _t.TipFixWhere),
        new(_t.TipChatTitle, _t.TipChatWhere),
    ];

    /// <summary>고른 접속으로 연습 탭을 연다. 이미 그 접속으로 연 탭이 있어도 새로 연다(사용자가 닫았을 수 있다).</summary>
    [RelayCommand]
    public void OpenTab()
    {
        if (SelectedConnection is not { } profile || IsWaiting)
        {
            return;
        }

        _tabId = _actions.OpenPracticeTab(profile, _t.TabTitle, PracticeSql);
        _tabProfileId = _tabId is null ? null : profile.Id;
        TabOpened = _tabId is not null;
        ShowStatus(TabOpened ? () => _t.TabOpened : () => _t.TabFailed);
    }

    /// <summary>연습 탭의 문장으로 SQL 설명을 받는다. 탭이 없거나 다른 접속으로 열렸으면 먼저 연다.</summary>
    [RelayCommand]
    public async Task ExplainAsync()
    {
        if (!CanExplain)
        {
            return;
        }

        var profile = SelectedConnection!;
        if (_actions.CannotStartReason(profile, _t.IsEnglish) is { } blocked)
        {
            ShowStatus(() => _actions.CannotStartReason(profile, _t.IsEnglish) ?? blocked);
            return;
        }

        if (_tabId is null || _tabProfileId != profile.Id)
        {
            OpenTab();
            if (_tabId is null)
            {
                return;
            }
        }

        IsWaiting = true;
        ShowStatus(() => _t.Waiting);
        try
        {
            var result = await _actions.ExplainAsync(_tabId!.Value, CancellationToken.None).ConfigureAwait(true);
            if (result.TabMissing)
            {
                // 사용자가 연습 탭을 닫았다: 새로 열고 한 번만 다시 묻는다.
                _tabId = _actions.OpenPracticeTab(profile, _t.TabTitle, PracticeSql);
                _tabProfileId = _tabId is null ? null : profile.Id;
                TabOpened = _tabId is not null;
                result = _tabId is { } reopened
                    ? await _actions.ExplainAsync(reopened, CancellationToken.None).ConfigureAwait(true)
                    : new TryItExplainResult(false, null, _t.TabFailed);
            }

            if (result.Answered)
            {
                AnswerText = result.Answer ?? string.Empty;
                ShowStatus(null);
                IsComplete = true;
            }
            else if (result.Reason is { Length: > 0 } reason)
            {
                ShowStatus(() => reason);
            }
            else
            {
                ShowStatus(() => _t.NoAnswer);
            }
        }
#pragma warning disable CA1031 // 런처가 어떤 이유로 실패해도 단계는 이유를 보이고 다시 누를 수 있게 남는다.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ex.Message;
            ShowStatus(() => _t.Failed + message);
        }
        finally
        {
            IsWaiting = false;
        }
    }

    /// <summary>[끝내기] 를 눌렀을 때 창 틀이 부른다. 고급 확인란이 켜져 있으면 외부 에이전트 연결 창을 연다.</summary>
    public async Task FinishAsync(CancellationToken ct)
    {
        if (!AlsoExternalAgent)
        {
            return;
        }

        try
        {
            await _actions.OpenExternalAgentAsync(ct).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // 끝내는 길이다: 외부 에이전트 창이 실패해도 도우미는 닫혀야 한다(메뉴에서 다시 열 수 있다).
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            System.Diagnostics.Trace.TraceWarning("외부 에이전트 연결 창을 열지 못함: {0}", ex.GetType().Name);
        }
    }

    private void ShowStatus(Func<string>? status)
    {
        _status = status;
        StatusText = status?.Invoke() ?? string.Empty;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var name in new[] { nameof(Title), nameof(Intro), nameof(PickLabel), nameof(OpenTabLabel), nameof(ExplainLabel), nameof(AnswerNote), nameof(AlsoExternalAgentLabel), nameof(Tips) })
        {
            OnPropertyChanged(name);
        }

        if (_status is { } status)
        {
            StatusText = status();
        }
    }

    public void Dispose()
    {
        if (_shellText is not null)
        {
            _shellText.LanguageChanged -= OnLanguageChanged;
        }
    }
}
