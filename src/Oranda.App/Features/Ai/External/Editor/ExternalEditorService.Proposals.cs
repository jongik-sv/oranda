using Oranda.App.Features.SqlEditor;
using Oranda.Core.Extensibility;
using Oranda.Core.Text;

namespace Oranda.App.Features.Ai.External.Editor;

// ExternalEditorService 의 일부: 제안 대기열(propose_edit · get_proposal)과 저장 요청(save_tab).
public sealed partial class ExternalEditorService
{
    /// <summary>끝난 제안을 기억하는 수(get_proposal 이 나중에 묻는다). 넘으면 오래된 끝난 것부터 잊는다.</summary>
    public const int RememberedProposals = 200;

    /// <summary>제안 기다림 기본·최대(초).</summary>
    public const int DefaultWaitSeconds = 120;

    public const int MaxWaitSeconds = 600;

    private readonly Dictionary<string, Proposal> _proposals = new(StringComparer.Ordinal);
    private readonly List<Proposal> _order = [];
    private readonly HashSet<SqlEditorViewModel> _watched = [];
    private readonly List<SaveRequest> _saves = [];
    private SqlEditorHost? _watchedHost;
    private Proposal? _applying;

    /// <summary>
    /// 기존 탭에 편집을 제안한다(<c>propose_edit</c>의 앞 절반). 기다리지 않는다: 띠에 세우고 <c>pending</c>, 판이 다르면 띠 없이 바로 <c>stale</c>.
    /// 잠긴 탭·읽기 전용 탭·글 밖 범위·열 블록 선택은 거부한다. 탭당 띠에는 하나만 보이고 나머지는 줄 선다(「대기 N」).
    /// 기다림은 <see cref="WaitAsync"/>, 다시 묻기는 <see cref="GetProposal"/>.
    /// </summary>
    public ExternalEditorResult<ExternalProposalView> Propose(IExternalTabAccess access, ExternalProposalRequest request)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.NewText);
        if (Find(request.TabId) is not { } tab)
        {
            return ExternalEditorResult.Fail<ExternalProposalView>(ExternalEditorErrors.TabNotFound, ExternalEditorText.TabNotFound);
        }

        if (Refuse<ExternalProposalView>(access, tab.ConnectionProfileId) is { } refused)
        {
            return refused;
        }

        if (tab.IsReadOnly)
        {
            return ExternalEditorResult.Fail<ExternalProposalView>(ExternalEditorErrors.ReadOnly, ExternalEditorText.ReadOnly);
        }

        var client = ClientName(request.Client);
        if (request.BaseVersion != tab.Version)
        {
            // 띠를 띄우지 않는다. 에이전트가 다시 읽고 다시 제안한다.
            var stale = Remember(new Proposal(request, tab.TabId, default, client) { Status = ExternalProposalStatus.Stale, Version = tab.Version });
            return ExternalEditorResult.Ok<ExternalProposalView>(stale.View);
        }

        if (RangeOf(tab, request.Range) is not { } range)
        {
            var message = request.Range.Kind switch
            {
                ExternalRangeKind.Selection => ExternalEditorText.BlockSelection,
                ExternalRangeKind.Statement => ExternalEditorText.NoStatement,
                _ => ExternalEditorText.RangeOutside,
            };
            return ExternalEditorResult.Fail<ExternalProposalView>(ExternalEditorErrors.InvalidRange, message);
        }

        var proposal = Remember(new Proposal(request, tab.TabId, range, client));
        Watch(tab);
        var headline = ExternalEditorText.ProposalHeadline(client) + (string.IsNullOrWhiteSpace(request.Title) ? string.Empty : " · " + request.Title.Trim());
        proposal.Item = new AgentBarItem(AgentBarItemKind.Proposal, headline)
        {
            Detail = request.Reason?.Trim() ?? string.Empty,
            Compare = () => CompareAsync(proposal),
            Accept = () => AcceptAsync(proposal),
            Discard = () => Finish(proposal, ExternalProposalStatus.Rejected),
        };
        BarOf(tab).Enqueue(proposal.Item);
        return ExternalEditorResult.Ok<ExternalProposalView>(proposal.View);
    }

    /// <summary>
    /// 제안이 끝나기를 <paramref name="wait"/> 만큼 기다린다(<c>propose_edit</c> 의 뒤 절반). 0 이면 지금 상태를 바로 준다(<c>pending</c>).
    /// 시간이 지나도 사람이 정하지 않았으면 <c>timeout</c> 을 주고 제안은 띠에 남긴다(<see cref="GetProposal"/> 로 다시 본다).
    /// </summary>
    public async Task<ExternalEditorResult<ExternalProposalView>> WaitAsync(string proposalId, TimeSpan wait, CancellationToken ct)
    {
        if (!_proposals.TryGetValue(proposalId ?? string.Empty, out var proposal))
        {
            return ExternalEditorResult.Fail<ExternalProposalView>(ExternalEditorErrors.ProposalNotFound, ExternalEditorText.ProposalNotFound);
        }

        if (proposal.Status == ExternalProposalStatus.Pending && wait > TimeSpan.Zero)
        {
            var max = TimeSpan.FromSeconds(MaxWaitSeconds);
            var delay = Task.Delay(wait > max ? max : wait, ct);
            await Task.WhenAny(proposal.Done.Task, delay).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            if (proposal.Status == ExternalProposalStatus.Pending)
            {
                return ExternalEditorResult.Ok<ExternalProposalView>(proposal.View with { Status = ExternalProposalStatus.Timeout });
            }
        }

        return ExternalEditorResult.Ok<ExternalProposalView>(proposal.View);
    }

    /// <summary>제안 상태를 다시 본다(<c>get_proposal</c>).</summary>
    public ExternalEditorResult<ExternalProposalView> GetProposal(string proposalId) =>
        _proposals.TryGetValue(proposalId ?? string.Empty, out var proposal)
            ? ExternalEditorResult.Ok<ExternalProposalView>(proposal.View)
            : ExternalEditorResult.Fail<ExternalProposalView>(ExternalEditorErrors.ProposalNotFound, ExternalEditorText.ProposalNotFound);

    /// <summary>
    /// 탭 저장을 사람에게 요청한다(<c>save_tab</c>, 확인 등급). 띠 「… 가 이 탭 저장을 요청했습니다 [저장] [무시]」 를 세우고 <paramref name="wait"/> 만큼 기다린다.
    /// 파일 없는 탭은 「저장」 이 「다른 이름으로」 창을 연다(사람이 고른다). 시간이 지나면 띠를 거두고 <c>timeout</c>.
    /// </summary>
    public async Task<ExternalEditorResult<ExternalSaveResult>> RequestSaveAsync(IExternalTabAccess access, Guid tabId, string? client, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (Find(tabId) is not { } tab)
        {
            return ExternalEditorResult.Fail<ExternalSaveResult>(ExternalEditorErrors.TabNotFound, ExternalEditorText.TabNotFound);
        }

        if (Refuse<ExternalSaveResult>(access, tab.ConnectionProfileId) is { } refused)
        {
            return refused;
        }

        if (tab.IsReadOnly)
        {
            return ExternalEditorResult.Fail<ExternalSaveResult>(ExternalEditorErrors.ReadOnly, ExternalEditorText.ReadOnly);
        }

        Watch(tab);
        var request = new SaveRequest(tab);
        request.Item = new AgentBarItem(AgentBarItemKind.Save, ExternalEditorText.SaveHeadline(ClientName(client)))
        {
            Detail = tab.Title,
            Accept = async () =>
            {
                if (!await Host.SaveAsync(tab, CancellationToken.None).ConfigureAwait(true))
                {
                    return ExternalEditorText.NotSaved;
                }

                FinishSave(request, new ExternalSaveResult(ExternalSaveStatus.Saved, tab.FilePath is { } path ? Path.GetFileName(path) : null));
                return null;
            },
            Discard = () => FinishSave(request, new ExternalSaveResult(ExternalSaveStatus.Rejected)),
        };
        _saves.Add(request);
        BarOf(tab).Enqueue(request.Item);
        var max = TimeSpan.FromSeconds(MaxWaitSeconds);
        var delay = Task.Delay(wait <= TimeSpan.Zero ? TimeSpan.Zero : wait > max ? max : wait, ct);
        try
        {
            await Task.WhenAny(request.Done.Task, delay).ConfigureAwait(true);
        }
        finally
        {
            if (!request.Done.Task.IsCompleted)
            {
                FinishSave(request, new ExternalSaveResult(ExternalSaveStatus.Timeout));
            }
        }

        ct.ThrowIfCancellationRequested();
        return ExternalEditorResult.Ok<ExternalSaveResult>(await request.Done.Task.ConfigureAwait(true));
    }

    // ---- 받아들이기·비교·버리기 --------------------------------------------------------------

    private async Task<string?> AcceptAsync(Proposal proposal)
    {
        if (proposal.Status != ExternalProposalStatus.Pending)
        {
            return null;
        }

        if (Find(proposal.TabId) is not { } tab)
        {
            Finish(proposal, ExternalProposalStatus.Rejected);
            return null;
        }

        var edit = new TextEdit(proposal.Range, proposal.Request.NewText) { Description = CompareTitle(proposal) };
        EditProposalOutcome outcome;
        _applying = proposal; // 넣을 때 글이 바뀌는 알림이 이 제안을 어긋남으로 만들지 않게
        try
        {
            outcome = await new EditorContext { Host = Host }.ProposeToTabAsync(tab, edit, proposal.Request.BaseVersion, CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            _applying = null;
        }

        switch (outcome.Status)
        {
            case EditProposalStatus.Accepted:
                Finish(proposal, ExternalProposalStatus.Accepted, outcome.Version, outcome.FinalText);
                return null;
            case EditProposalStatus.Modified:
                Finish(proposal, ExternalProposalStatus.Modified, outcome.Version, outcome.FinalText);
                return null;
            case EditProposalStatus.Stale:
                Finish(proposal, ExternalProposalStatus.Stale, outcome.Version);
                return null;
            case EditProposalStatus.ReadOnly:
                Finish(proposal, ExternalProposalStatus.Rejected, outcome.Version);
                return null;
            default:
                return ExternalEditorText.NotApplied; // 비교 창을 닫았다: 띠에 남긴다
        }
    }

    private async Task CompareAsync(Proposal proposal)
    {
        if (proposal.Status != ExternalProposalStatus.Pending || Find(proposal.TabId) is not { } tab || !proposal.Range.IsWithin(tab.Document.TextLength))
        {
            return;
        }

        var before = tab.Document.GetText(proposal.Range.Start, proposal.Range.Length);
        await Host.Dialogs.ShowComparisonAsync(CompareTitle(proposal), before, proposal.Request.NewText, CancellationToken.None).ConfigureAwait(true);
    }

    private static string CompareTitle(Proposal proposal) =>
        string.IsNullOrWhiteSpace(proposal.Request.Title) ? ExternalEditorText.CompareTitle(proposal.Client) : proposal.Request.Title.Trim();

    private void Finish(Proposal proposal, ExternalProposalStatus status, long? version = null, string? finalText = null)
    {
        if (proposal.Status != ExternalProposalStatus.Pending)
        {
            return;
        }

        proposal.Status = status;
        proposal.Version = version ?? Find(proposal.TabId)?.Version;
        proposal.FinalText = finalText;
        if (proposal.Item is { } item && Find(proposal.TabId)?.AgentBar is { } bar)
        {
            bar.Remove(item);
        }

        proposal.Done.TrySetResult();
        Forget();
    }

    private void FinishSave(SaveRequest request, ExternalSaveResult result)
    {
        if (!request.Done.TrySetResult(result))
        {
            return;
        }

        _saves.Remove(request);
        if (request.Item is { } item)
        {
            request.Tab.AgentBar?.Remove(item);
        }
    }

    // ---- 탭 글 바뀜·탭 닫힘 -------------------------------------------------------------

    /// <summary>탭 글이 바뀌거나 탭이 닫히는 것을 듣는다(줄 선 제안을 어긋남·거절로 끝내려고).</summary>
    private void Watch(SqlEditorViewModel tab)
    {
        var host = Host;
        if (!ReferenceEquals(_watchedHost, host))
        {
            if (_watchedHost is { } old)
            {
                old.TabClosed -= OnTabClosed;
            }

            host.TabClosed += OnTabClosed;
            _watchedHost = host;
        }

        if (_watched.Add(tab))
        {
            tab.TextChanged += OnTabTextChanged;
        }
    }

    private void OnTabTextChanged(object? sender, EventArgs e)
    {
        if (sender is not SqlEditorViewModel tab)
        {
            return;
        }

        // 범위를 따라가지 않는다(다음 회차): 기준 판과 달라진 제안은 모두 어긋남이다.
        foreach (var proposal in PendingOf(tab.TabId).Where(p => !ReferenceEquals(p, _applying) && p.Request.BaseVersion != tab.Version))
        {
            Finish(proposal, ExternalProposalStatus.Stale, tab.Version);
        }
    }

    private void OnTabClosed(object? sender, SqlEditorViewModel tab)
    {
        foreach (var proposal in PendingOf(tab.TabId))
        {
            Finish(proposal, ExternalProposalStatus.Rejected, tab.Version);
        }

        foreach (var save in _saves.Where(s => ReferenceEquals(s.Tab, tab)).ToList())
        {
            FinishSave(save, new ExternalSaveResult(ExternalSaveStatus.Rejected));
        }

        if (_watched.Remove(tab))
        {
            tab.TextChanged -= OnTabTextChanged;
        }
    }

    private List<Proposal> PendingOf(Guid tabId) => [.. _order.Where(p => p.TabId == tabId && p.Status == ExternalProposalStatus.Pending)];

    // ---- 기억 -------------------------------------------------------------------------------

    private Proposal Remember(Proposal proposal)
    {
        _proposals[proposal.Id] = proposal;
        _order.Add(proposal);
        Forget();
        return proposal;
    }

    private void Forget()
    {
        while (_order.Count > RememberedProposals && _order.FirstOrDefault(p => p.Status != ExternalProposalStatus.Pending) is { } old)
        {
            _order.Remove(old);
            _proposals.Remove(old.Id);
        }
    }

    /// <summary>제안의 범위를 지금 탭 상태로 정한다. 글 밖·열 블록 선택·문장 없음이면 null.</summary>
    private static TextRange? RangeOf(SqlEditorViewModel tab, ExternalRange range)
    {
        var length = tab.Document.TextLength;
        TextRange? resolved = range.Kind switch
        {
            ExternalRangeKind.Document => new TextRange(0, length),
            ExternalRangeKind.Selection when tab.HasMultiPieceSelection || tab.SelectionIsBlock => null,
            ExternalRangeKind.Selection => new TextRange(tab.SelectionStart, tab.SelectionLength),
            ExternalRangeKind.Caret => new TextRange(tab.CaretOffset, 0),
            ExternalRangeKind.Statement => tab.GetStatementAtCaret()?.Range,
            _ => range.Start >= 0 && range.Length >= 0 ? new TextRange(range.Start, range.Length) : null,
        };
        return resolved is { } r && r.IsWithin(length) ? r : null;
    }

    private sealed class Proposal(ExternalProposalRequest request, Guid tabId, TextRange range, string client)
    {
        public string Id { get; } = "p-" + Guid.NewGuid().ToString("N")[..12];

        public ExternalProposalRequest Request { get; } = request;

        public Guid TabId { get; } = tabId;

        public TextRange Range { get; } = range;

        public string Client { get; } = client;

        public ExternalProposalStatus Status { get; set; } = ExternalProposalStatus.Pending;

        public long? Version { get; set; }

        public string? FinalText { get; set; }

        public AgentBarItem? Item { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExternalProposalView View => new(Id, Status) { Version = Version, FinalText = FinalText };
    }

    private sealed class SaveRequest(SqlEditorViewModel tab)
    {
        public SqlEditorViewModel Tab { get; } = tab;

        public AgentBarItem? Item { get; set; }

        public TaskCompletionSource<ExternalSaveResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
