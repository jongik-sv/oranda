using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.InPlace;
using Oranda.App.Features.Ai.Kit;
using Oranda.App.Features.Rollback.Ai;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Extensibility;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Sessions;
using Oranda.Core.Text;
using KitEntryState = Oranda.App.Features.Ai.Kit.AiEntryState;

namespace Oranda.App.Features.Rollback;

/// <summary>
/// 원복 스크립트 창의 「AI 점검」(설계 §4.2, 바로 처리 — 점검 띠). 주 모델 한 번짜리 요청. 고친 스크립트는 「고친 스크립트를 편집기에」 로
/// 새 SQL 탭(원래 스크립트)을 열고 편집기 제안(<see cref="IEditorContext.ProposeEditAsync"/>, 차이 미리 보기)으로만 넣는다. 실행하지 않는다.
/// </summary>
public sealed partial class RollbackScriptViewModel
{
    /// <summary>입구 단추 글.</summary>
    public const string AiCheckLabel = "AI 점검";

    /// <summary>접속이 열려 있지 않을 때의 입구 까닭.</summary>
    public const string AiNoSessionText = "묶음의 접속이 열려 있지 않습니다. 먼저 접속하세요.";

    /// <summary>편집기에 넣지 못했을 때.</summary>
    public const string AiProposeFailedText = "고친 스크립트를 편집기에 넣지 않았습니다(거절했거나 편집기를 열지 못함).";

    private AiInPlaceHost? _ai;
    private AiInlineStripViewModel? _aiStrip;
    private AiEntryButton? _aiEntry;

    /// <summary>점검 띠(앱 연결 전에는 null).</summary>
    public AiInlineStripViewModel? AiStrip => _aiStrip;

    /// <summary>「AI 점검」 단추(앱 연결 전에는 null).</summary>
    public AiEntryButton? AiEntry => _aiEntry;

    /// <summary>마지막 점검 결과(띠의 답을 읽은 것).</summary>
    public RollbackCheckResult? AiResult { get; private set; }

    /// <summary>「고친 스크립트를 편집기에」 를 보인다.</summary>
    [ObservableProperty]
    private bool _hasAiFix;

    /// <summary>대상 표의 FK·트리거·시퀀스 읽기. 기본 = 메타데이터의 표 정보에서 FK.</summary>
    internal Func<ISession, IReadOnlyList<DbObjectRef>, CancellationToken, Task<(IReadOnlyList<RollbackForeignKey> Keys, IReadOnlyList<RollbackTableFacts> Tables)>>? FactsReader { get; set; }

    /// <summary>편집기 제안(활성 편집기). 셸은 서비스의 <see cref="IEditorContext"/>.</summary>
    internal Func<IEditorContext?> EditorContext { get; set; } = () => null;

    /// <summary>AI 연결을 잇는다(<see cref="UseServices"/> 와 시험이 부른다).</summary>
    internal void UseAi(AiInPlaceHost host, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        _ai = host;
        if (services is not null)
        {
            EditorContext = () => services.GetService(typeof(IEditorContext)) as IEditorContext;
            FactsReader ??= (session, tables, ct) => ReadFactsAsync(services.GetService(typeof(IMetadataProvider)) as IMetadataProvider, session, tables, ct);
        }

        _aiStrip = host.CreateStrip(RollbackCheckPrompt.Title, "rollback_check", RollbackCheckPrompt.Tier, AiSource, PrepareAiAsync);
        _aiStrip.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AiInlineStripViewModel.State))
            {
                OnAiStateChanged();
            }
        };
        _aiEntry = new AiEntryButton(AiCheckLabel, EvaluateAiEntry, () => _aiStrip.RunAsync(CancellationToken.None));
        OnPropertyChanged(nameof(AiStrip));
        OnPropertyChanged(nameof(AiEntry));
    }

    /// <summary>점검 입력: 묶음의 문장(무효 빼고)과 탭의 스크립트.</summary>
    internal RollbackCheckInput AiInput(IReadOnlyList<RollbackForeignKey>? keys = null, IReadOnlyList<RollbackTableFacts>? tables = null) =>
        new(Group.Statements.Where(s => !s.Invalidated).OrderBy(s => s.Seq)
                .Select(s => new RollbackCheckChange(s.Seq, s.Verb.ToString().ToUpperInvariant(), s.Table is { } t ? t.Owner + "." + t.Name : null, s.RowsAffected) { StatementText = s.Summary })
                .ToList(),
            Text)
        {
            ForeignKeys = keys ?? [],
            Tables = tables ?? [],
        };

    private KitEntryState EvaluateAiEntry()
    {
        if (_ai is null)
        {
            return new KitEntryState(false, null);
        }

        // 접속이 없으면 허용 수준을 읽을 수 없으므로 「끔」 이 아니라 접속 안내를 먼저 보인다(설정이 없으면 그 안내가 먼저).
        var session = SessionFinder(Group.ProfileId);
        if (_ai.Configured && session is null)
        {
            return new KitEntryState(false, AiNoSessionText);
        }

        return _ai.Entry(session?.Profile, AiAccessLevel.Metadata, string.IsNullOrWhiteSpace(Text) ? RollbackCheckPrompt.NothingText : null);
    }

    private AiInPlaceSource AiSource()
    {
        var session = SessionFinder(Group.ProfileId);
        return new AiInPlaceSource(session?.Profile, AiTierRouting.EstimateTokens(Text), session is null ? AiNoSessionText : null);
    }

    private async Task<AiStripPreparation> PrepareAiAsync(AiOneShotRun run, CancellationToken ct)
    {
        IReadOnlyList<RollbackForeignKey> keys = [];
        IReadOnlyList<RollbackTableFacts> tables = [];
        if (SessionFinder(Group.ProfileId) is { } session && FactsReader is { } reader)
        {
            var targets = Group.Statements.Select(s => s.Table).OfType<DbObjectRef>().Distinct().ToList();
            (keys, tables) = await reader(session, targets, ct).ConfigureAwait(true);
        }

        _aiInput = AiInput(keys, tables);
        return RollbackCheckPrompt.Prepare(_aiInput, run, _ai?.Masker);
    }

    private RollbackCheckInput? _aiInput;

    private void OnAiStateChanged()
    {
        if (_aiStrip is { State: AiStripState.Done, LastRequest: { } request } strip && _aiInput is { } input)
        {
            AiResult = RollbackCheckPrompt.Read(strip.AnswerText, input, request, _ai?.Masker);
            HasAiFix = AiResult.EditorText is not null;
            if (AiResult.EditorNote is { } note)
            {
                Tell(note, warning: true);
            }
            else
            {
                Tell(string.Join(" ", AiResult.Notes), warning: false); // 「값 없이 점검했습니다」·「제안입니다」
            }
        }
        else if (_aiStrip is { IsRunning: true })
        {
            AiResult = null;
            HasAiFix = false;
        }

        _aiEntry?.Refresh();
    }

    /// <summary>
    /// 「고친 스크립트를 편집기에」: 원래 스크립트로 새 SQL 탭을 열고, 그 탭 전체를 고친 스크립트로 바꾸는 제안을 낸다(차이 미리 보기에서 받아들일 때만 바뀐다).
    /// </summary>
    public async Task<bool> ProposeAiFixAsync(CancellationToken ct = default)
    {
        if (AiResult?.EditorText is not { } fixedScript)
        {
            return false;
        }

        if (!EditorOpener(Text, Group.ProfileId == Guid.Empty ? null : Group.ProfileId) || EditorContext() is not { } editor)
        {
            Tell(AiProposeFailedText, warning: true);
            return false;
        }

        var length = editor.GetActiveDocument()?.Text.Length ?? Text.Length;
        var accepted = await editor.ProposeEditAsync(new TextEdit(new TextRange(0, length), fixedScript) { Description = "AI 점검 — 고친 원복 스크립트" }, ct).ConfigureAwait(true);
        Tell(accepted ? "고친 스크립트를 새 SQL 탭에 넣었습니다(자동으로 실행하지 않습니다)." : AiProposeFailedText, warning: !accepted);
        return accepted;
    }

    /// <summary>표마다 FK(자식 → 부모)를 메타데이터에서 읽는다. 읽지 못한 표는 건너뛴다(점검은 조언이다).</summary>
    internal static async Task<(IReadOnlyList<RollbackForeignKey> Keys, IReadOnlyList<RollbackTableFacts> Tables)> ReadFactsAsync(IMetadataProvider? metadata, ISession session, IReadOnlyList<DbObjectRef> tables, CancellationToken ct)
    {
        var keys = new List<RollbackForeignKey>();
        if (metadata is null)
        {
            return (keys, []);
        }

        foreach (var table in tables)
        {
            try
            {
                var info = await metadata.GetTableAsync(session, table, ct).ConfigureAwait(true);
                keys.AddRange(info.Constraints
                    .Where(c => c.Kind == ConstraintKind.ForeignKey && c.ReferencedTable is not null)
                    .Select(c => new RollbackForeignKey(table.Owner + "." + table.Name, c.ReferencedTable!.Owner + "." + c.ReferencedTable.Name, c.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                // 표 정보를 못 읽으면 FK 없이 점검한다.
            }
        }

        return (keys, []);
    }
}
