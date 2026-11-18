using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Schema.TableEdit.Partitions;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;

namespace Oranda.App.Features.TableEdit;

/// <summary>
/// AI 파티션 추천의 세션 몫(설계 2026-11-18 3장 ①·④): 파티션 페이지 단추(「✨ AI 추천…」·「분포 보기」)가 부르는 동작,
/// 「편집기에 채우기」(일반 → 파티션 바꾸기 모델을 통째로 바꿈), 채운 뒤 띠 문구·「되돌리기」(채우기 직전 모델로)·「추천 다시 보기」.
/// 동작은 객체 상세가 넣는다(<c>ObjectDetailViewModel.PartitionAdvisor.cs</c>). null 이면 단추를 숨긴다.
/// </summary>
public sealed partial class TableEditSession
{
    private Func<Task>? _openAiAdvisor;
    private Func<Task>? _openPartitionEvidence;
    private Func<Task>? _reopenAiAdvice;
    private AdviceFill? _adviceFill;
    private PartitionTableDraft? _adviceBefore;
    private PartitionTableDraft? _adviceFilled;
    private PartitionTableDraft? _adviceOriginal;
    private bool _adviceWatching;

    /// <summary>「✨ AI 추천…」 동작(확인 창을 연다).</summary>
    public Func<Task>? OpenAiAdvisor
    {
        get => _openAiAdvisor;
        set
        {
            _openAiAdvisor = value;
            WatchAdvice();
            OnPropertyChanged(nameof(ShowsAiAdvisor));
        }
    }

    /// <summary>「분포 보기」 동작(AI 없이 근거 패널만, PA-9).</summary>
    public Func<Task>? OpenPartitionEvidence
    {
        get => _openPartitionEvidence;
        set
        {
            _openPartitionEvidence = value;
            OnPropertyChanged(nameof(ShowsAiAdvisor));
        }
    }

    /// <summary>띠의 「추천 다시 보기」 동작(AI 대화 창을 보인다, PA-22). null 이면 단추를 숨긴다.</summary>
    public Func<Task>? ReopenAiAdvice
    {
        get => _reopenAiAdvice;
        set
        {
            _reopenAiAdvice = value;
            OnPropertyChanged(nameof(CanReopenAiAdvice));
        }
    }

    /// <summary>
    /// 「✨ AI 추천…」·「분포 보기」 줄을 보인다(PA-8): 편집 중인 일반(비파티션) 일반 힙 테이블이고 파티션을 고칠 수 있다.
    /// 열 형식과 상관없이 늘 보인다(날짜 열이 없는 테이블도 여기서 시작). 바꾸기를 시작한 뒤에도 남는다.
    /// </summary>
    public bool ShowsAiAdvisor => _openAiAdvisor is not null && ShowsPartitionMethodChoice && CanEditPartitions;

    /// <summary>「✨ AI 추천…」을 누름.</summary>
    public Task OpenAiAdvisorAsync() => ShowsAiAdvisor ? _openAiAdvisor!() : Task.CompletedTask;

    /// <summary>「분포 보기」를 누름.</summary>
    public Task OpenPartitionEvidenceAsync() => ShowsAiAdvisor && _openPartitionEvidence is { } open ? open() : Task.CompletedTask;

    /// <summary>「추천 다시 보기」를 누름.</summary>
    public Task ReopenAiAdviceAsync() => _reopenAiAdvice is { } open ? open() : Task.CompletedTask;

    public bool CanReopenAiAdvice => _reopenAiAdvice is not null;

    // 파티션 페이지 단추(명령으로 묶어 화면 코드 뒤를 건드리지 않는다).
    [RelayCommand]
    private Task AskAiAdvice() => OpenAiAdvisorAsync();

    [RelayCommand]
    private Task ShowEvidence() => OpenPartitionEvidenceAsync();

    [RelayCommand]
    private Task ReopenAdvice() => ReopenAiAdviceAsync();

    [RelayCommand]
    private void UndoAdvice() => RevertAdvice();

    // ---- 채우기 ----------------------------------------------------------------

    /// <summary>
    /// 「편집기에 채우기」: 후보로 만든 파티션 모델(<see cref="AdviceDraft.Draft"/>)로 편집 모델을 바꾼다(바꾸기 경로, 설계 4.2).
    /// ✖ 가 하나라도 있거나(<paramref name="checks"/>·<see cref="AdviceDraft.Problems"/>) 일반 테이블이 아니거나 고칠 수 없으면 아무것도 하지 않고 false.
    /// 채우기 직전 모델을 기억해 「되돌리기」가 그리로 간다. ⚠ 는 띠에 남긴다.
    /// </summary>
    public bool ApplyAdvice(PartitionCandidate candidate, AdviceDraft advice, IReadOnlyList<AdviceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentNullException.ThrowIfNull(checks);
        if (!CanEditPartitions || !ShowsPartitionMethodChoice || _partitionDraft is null || advice.Draft.Scheme is null
            || !PartitionAdviceChecker.CanFill([.. checks, .. advice.Problems]))
        {
            return false;
        }

        FlushPendingInput?.Invoke();
        var before = _partitionDraft;
        _partitionKeyInput = null;
        _partitionHeaderError = null;
        // 하위 파티션 템플릿·LOCAL 인덱스는 모델 칸으로(PA-11·12). 채우기가 아직 모델에 넣지 않았으면 AdviceDraft 칸에서 옮긴다.
        var scheme = advice.Draft.Scheme;
        if (scheme.SubpartitionTemplate is null && advice.Subpartition is { } sub && ToTemplate(sub) is { } template)
        {
            scheme = scheme with { SubpartitionTemplate = template };
        }

        IReadOnlyList<string> local = advice.Draft.LocalIndexes.Count > 0 ? advice.Draft.LocalIndexes : advice.LocalIndexes;
        UpdatePartitions(d => d with { Scheme = scheme, Partitions = advice.Draft.Partitions, LocalIndexes = [.. local] });
        if (ReferenceEquals(before, _partitionDraft))
        {
            return false;
        }

        _adviceBefore = before;
        _adviceFilled = _partitionDraft;
        _adviceOriginal = _partitionOriginal;
        _adviceFill = new AdviceFill(
            candidate.Rank,
            candidate.Title,
            advice.Draft.Partitions.Count,
            scheme.SubpartitionTemplate is { } filled ? $"{filled.Method.ToString().ToUpperInvariant()}({string.Join(", ", filled.KeyColumns)})" : null,
            [.. local],
            [.. checks.Concat(advice.Problems).Where(c => c.Level == AdviceCheckLevel.Warn)]);
        WatchAdvice();
        RaiseAdviceBanner();
        return true;
    }

    /// <summary>띠의 「되돌리기」: 채우기 직전 모델로. 띠를 내린다.</summary>
    public bool RevertAdvice()
    {
        if (!ShowsAdviceBanner || _adviceBefore is not { } before)
        {
            return false;
        }

        FlushPendingInput?.Invoke();
        _partitionKeyInput = null;
        _partitionHeaderError = null;
        ClearAdvice();
        UpdatePartitions(_ => before);
        RaiseAdviceBanner();
        return true;
    }

    /// <summary>재계산마다 단추 줄·띠를 다시 알린다(실행 뒤 다시 읽어 파티션 테이블이 되면 단추 줄이 사라지게). 한 번만 건다.</summary>
    private void WatchAdvice()
    {
        if (!_adviceWatching)
        {
            _adviceWatching = true;
            Recomputed += (_, _) => RaiseAdviceBanner();
        }
    }

    private void ClearAdvice()
    {
        _adviceFill = null;
        _adviceBefore = null;
        _adviceFilled = null;
        _adviceOriginal = null;
    }

    /// <summary>마지막으로 채운 후보(띠가 없으면 null).</summary>
    public AdviceFill? AdviceFilled => ShowsAdviceBanner ? _adviceFill : null;

    /// <summary>
    /// 띠를 보인다: 채운 적이 있고 그 뒤 원본이 그대로다(실행 뒤 다시 읽으면 원본이 바뀌어 띠를 내린다).
    /// </summary>
    public bool ShowsAdviceBanner => _adviceFill is not null && ReferenceEquals(_adviceOriginal, _partitionOriginal);

    /// <summary>채운 뒤 사용자가 파티션 편집을 고쳤다(띠 문구가 「직접 고침」).</summary>
    public bool IsAdviceEdited => ShowsAdviceBanner && !ReferenceEquals(_adviceFilled, _partitionDraft);

    /// <summary>띠 문구(설계 3장 ④).</summary>
    public string AdviceBannerText
    {
        get
        {
            if (!ShowsAdviceBanner || _adviceFill is not { } fill)
            {
                return string.Empty;
            }

            if (IsAdviceEdited)
            {
                return TableEditText.T(
                    $"✨ AI 추천 {fill.Rank}안에서 직접 고침 — 실행 전에 확인하세요.",
                    $"✨ Edited by hand from AI suggestion {fill.Rank} — check before you run.");
            }

            var parts = new List<string>();
            if (fill.Rows > 0)
            {
                parts.Add(TableEditText.T($"파티션 {fill.Rows}줄", $"{fill.Rows} partitions"));
            }

            if (fill.Subpartition is { } sub)
            {
                parts.Add(TableEditText.T($"하위 {sub}", $"subpartition {sub}"));
            }

            if (fill.LocalIndexes.Count > 0)
            {
                parts.Add(TableEditText.T($"인덱스 {fill.LocalIndexes.Count}개 LOCAL", $"{fill.LocalIndexes.Count} indexes LOCAL"));
            }

            var detail = parts.Count == 0 ? string.Empty : " — " + string.Join(", ", parts);
            return TableEditText.T($"✨ AI 추천 {fill.Rank}안을 채웠습니다{detail}. 실행 전에 확인하세요.",
                $"✨ Filled AI suggestion {fill.Rank}{detail}. Check before you run.");
        }
    }

    /// <summary>띠 아래 ⚠ 줄(채운 후보의 앱 확인 ⚠ — 채운 뒤에도 남긴다).</summary>
    public IReadOnlyList<string> AdviceBannerWarnings =>
        ShowsAdviceBanner && _adviceFill is { } fill ? [.. fill.Warnings.Select(w => "⚠ " + Advisor.PartitionAdvisorText.Check(w))] : [];

    /// <summary>
    /// AI 하위 파티션 제안 → 바꾸기 템플릿(PA-11). LIST 값은 키 열 형식대로 쓴다(글 = 따옴표, 수 = 그대로), 기본 묶음 = <c>DEFAULT</c>.
    /// HASH 개수가 없으면 채우기 기본 개수. 모르는 방식이면 null.
    /// </summary>
    private SubpartitionTemplate? ToTemplate(SubpartitionAdvice sub)
    {
        var method = AdviceMethod.ToMethod(sub.Method);
        if (method is not (PartitionMethod.List or PartitionMethod.Hash) || sub.KeyColumns.Count == 0)
        {
            return null;
        }

        if (method == PartitionMethod.Hash)
        {
            return new SubpartitionTemplate(PartitionMethod.Hash, [.. sub.KeyColumns]) { HashCount = sub.HashCount ?? PartitionAdviceApplier.DefaultHashCount };
        }

        var text = Draft.Columns.FirstOrDefault(c => string.Equals(c.Name, sub.KeyColumns[0], StringComparison.OrdinalIgnoreCase)) is not { } column
                   || PartitionBounds.KindOf(column.DataType) != PartitionKeyKind.Number;
        string Literal(string v) => text ? "'" + v.Replace("'", "''", StringComparison.Ordinal) + "'" : v;
        var rows = (sub.Groups ?? [])
            .Select(g => new SubpartitionTemplateRow(g.Name, g.IsDefault ? "DEFAULT" : string.Join(", ", g.Values.Select(Literal))))
            .ToList();
        return rows.Count == 0 ? null : new SubpartitionTemplate(PartitionMethod.List, [.. sub.KeyColumns]) { Rows = rows };
    }

    public bool HasAdviceBannerWarnings => AdviceBannerWarnings.Count > 0;

    private void RaiseAdviceBanner()
    {
        OnPropertyChanged(nameof(ShowsAdviceBanner));
        OnPropertyChanged(nameof(IsAdviceEdited));
        OnPropertyChanged(nameof(AdviceFilled));
        OnPropertyChanged(nameof(AdviceBannerText));
        OnPropertyChanged(nameof(AdviceBannerWarnings));
        OnPropertyChanged(nameof(HasAdviceBannerWarnings));
        OnPropertyChanged(nameof(ShowsAiAdvisor));
    }
}

/// <summary>채운 후보 하나의 기억(띠 문구).</summary>
/// <param name="Rows">채운 파티션 줄 수</param>
/// <param name="Subpartition">채운 하위 파티션 템플릿 요약(<c>LIST(STATUS)</c>), 없으면 null</param>
/// <param name="Warnings">앱 확인 ⚠(채운 뒤 띠에 남김)</param>
public sealed record AdviceFill(
    int Rank,
    string Title,
    int Rows,
    string? Subpartition,
    IReadOnlyList<string> LocalIndexes,
    IReadOnlyList<AdviceCheck> Warnings);
