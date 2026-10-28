using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.SessionMonitor;
using Oranda.Core.Plans;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SqlMonitor;

/// <summary>튜닝 추천 목록 한 줄.</summary>
public sealed class TuningRecommendationItem(TuningRecommendation rec)
{
    public TuningRecommendation Recommendation { get; } = rec;

    public SqlStatsSample Sql => Recommendation.Sql;

    public string SqlId => Sql.SqlId;

    public int Score => Recommendation.Score;

    /// <summary>이유 요약: 점수 큰 이유 둘 + 「외 n」.</summary>
    public string ReasonText
    {
        get
        {
            var r = Recommendation.Reasons;
            var head = string.Join(" · ", r.Take(2).Select(x => x.Title));
            return r.Count > 2 ? head + $" 외 {r.Count - 2}" : head;
        }
    }

    public string Schema => Sql.ParsingSchema ?? string.Empty;

    public string Preview => PlanControlItem.Preview(Sql.SqlText, 160);

    /// <summary>묶음·인스턴스·plan_hash 까지 같은 줄을 찾는 열쇠.</summary>
    public (int?, string, long, bool) Key => (Sql.InstanceId, Sql.SqlId, Sql.PlanHashValue, Sql.IsLiteralGroup);

    /// <summary>실행당 경과(초, 소수 둘째 자리). 실행 0 이면 null.</summary>
    public decimal? ElapsedPerExec => Recommendation.ElapsedPerExecSeconds is { } s && double.IsFinite(s) ? Math.Round((decimal)s, 3) : null;

    /// <summary>실행당 논리 읽기(정수). 실행 0 이면 null.</summary>
    public long? GetsPerExec => Recommendation.GetsPerExec is { } g && double.IsFinite(g) ? (long)Math.Round(g) : null;
}

/// <summary>
/// SQL 모니터 「튜닝 추천」 탭(TSK-14-17). 공유 풀 통계(gv$sqlstats 상위 N)에 규칙(<see cref="TuningRecommendationRules"/>)을 적용해 튜닝할 SQL 을 점수순으로 보인다.
/// <para>부하: 새로 고침은 수동만(자동 주기 없음). 후보 한 번 + 리터럴 묶음 한 번 + 점수 상위 <see cref="TuningRecommendationRules.PlanCheckCount"/> 개 계획,
/// 고른 줄은 전문·계획을 더 읽는다. 모두 모니터 세션의 같은 관문을 지난다. 「내부 스키마 제외」 를 바꾸면 다시 묻지 않고 받은 후보로 다시 매긴다.</para>
/// </summary>
public sealed partial class TuningRecommendationsViewModel : ObservableObject
{
    /// <summary>튜닝 도우미 명령(TSK-06-05). 편집기 현재 문장을 연다.</summary>
    public const string AssistantCommand = "oranda.tuning.assistant";

    private readonly SqlMonitorViewModel _owner;
    private readonly Dictionary<string, IReadOnlyList<PlanFindingLine>> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fullTexts = new(StringComparer.Ordinal);
    private readonly List<SqlStatsSample> _extra = [];
    private TuningCandidateSet? _set;

    [ObservableProperty]
    private TuningRecommendationItem? _selectedItem;

    [ObservableProperty]
    private bool _excludeInternal = true;

    [ObservableProperty]
    private string _statusText = "「새로 고침」을 누르면 공유 풀(v$sqlstats) 상위 SQL 을 규칙으로 살펴 튜닝할 SQL 을 점수순으로 보입니다(자동 새로 고침 없음).";

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRefresh))]
    private bool _isBusy;

    public TuningRecommendationsViewModel(SqlMonitorViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SqlMonitorViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(CanRefresh));
            }
        };
    }

    public ObservableCollection<TuningRecommendationItem> Items { get; } = [];

    public SqlMonitorViewModel Owner => _owner;

    public bool CanRefresh => _owner.IsOpen && !IsBusy;

    public bool HasSelection => SelectedItem is not null;

    /// <summary>한 번이라도 읽었는가.</summary>
    public bool IsLoaded => _set is not null;

    /// <summary>「튜닝 도우미로 열기」 단추(명령이 등록돼 있을 때만).</summary>
    public bool CanOpenAssistant => _owner.CanRunCommand(AssistantCommand);

    /// <summary>「자식 커서」 단추.</summary>
    public bool CanOpenChildCursors => _owner.CanOpenChildCursors;

    /// <summary>후보를 읽고 점수 매긴다(수동).</summary>
    public Task RefreshAsync(CancellationToken ct = default) => LoadAsync(focus: null, ct);

    /// <summary>
    /// 이 sql_id 를 골라 보인다(세션 모니터 바로 가기). 아직 읽지 않았으면 먼저 읽고, 목록에 없으면 그 SQL 만 따로 읽어 맨 아래에 더한다
    /// (규칙에 안 걸렸으면 점수 0 으로 「걸린 규칙 없음」).
    /// </summary>
    public async Task FocusAsync(string sqlId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlId);
        if (!IsLoaded)
        {
            await LoadAsync(sqlId, ct);
            return;
        }

        if (!Select(sqlId))
        {
            await LoadOneAsync(sqlId, ct);
        }
    }

    /// <summary>모니터 세션이 바뀌었다(열림·닫힘·다른 접속): 받은 것을 모두 비운다.</summary>
    internal void Reset()
    {
        _set = null;
        _plans.Clear();
        _fullTexts.Clear();
        _extra.Clear();
        _owner.PostToUi(() =>
        {
            Items.Clear();
            SelectedItem = null;
            DetailText = string.Empty;
            OnPropertyChanged(nameof(IsLoaded));
            OnPropertyChanged(nameof(CanRefresh));
        });
    }

    /// <summary>「편집기로 열기」: SQL 전문을 새 편집기 탭에(머리 주석에 점수·이유). 실행하지 않는다.</summary>
    public async Task OpenInEditorAsync(CancellationToken ct = default)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        var text = await FullTextAsync(item, ct);
        var sb = new StringBuilder();
        sb.Append("-- 튜닝 추천: sql_id ").Append(item.SqlId).Append(" · 점수 ").Append(item.Score.ToString(CultureInfo.InvariantCulture)).AppendLine();
        foreach (var r in item.Recommendation.Reasons)
        {
            sb.Append("--   ").Append(r.Title).Append(": ").AppendLine(r.Evidence.ReplaceLineEndings(" "));
        }

        sb.AppendLine(text);
        _owner.OpenSqlText($"recommend_{item.SqlId}.sql", sb.ToString());
        StatusText = $"SQL {item.SqlId} 을(를) 새 편집기 탭에 넣었습니다(실행하지 않음).";
    }

    /// <summary>「튜닝 도우미로 열기」: SQL 전문을 새 편집기 탭에 넣고 그 문장으로 튜닝 도우미(기존 명령)를 연다.</summary>
    public async Task OpenInAssistantAsync(CancellationToken ct = default)
    {
        if (SelectedItem is not { } item || !CanOpenAssistant)
        {
            return;
        }

        var text = await FullTextAsync(item, ct);
        _owner.OpenSqlText($"tuning_{item.SqlId}.sql", text);
        await _owner.RunShellCommandAsync(AssistantCommand, null);
    }

    /// <summary>「자식 커서」: 고른 sql_id 의 자식 커서 문서(기존 명령).</summary>
    public Task OpenChildCursorsAsync() =>
        SelectedItem is { } item && CanOpenChildCursors
            ? _owner.RunShellCommandAsync(SqlMonitorViewModel.ChildCursorsCommand, item.SqlId)
            : Task.CompletedTask;

    /// <summary>고른 줄의 이유 전체·근거 수치·SQL(전문을 읽었으면 전문).</summary>
    public static string FormatDetail(TuningRecommendationItem item, string? fullText)
    {
        ArgumentNullException.ThrowIfNull(item);
        var s = item.Sql;
        var sb = new StringBuilder();
        sb.Append("SQL ID ").Append(s.SqlId);
        if (s.IsLiteralGroup)
        {
            sb.Append(" (리터럴 묶음의 표본 — 같은 모양 sql_id ").Append(s.SimilarSqlCount.ToString("N0", CultureInfo.InvariantCulture)).Append("개 합)");
        }

        sb.Append(" · 점수 ").Append(item.Score.ToString(CultureInfo.InvariantCulture)).AppendLine();
        sb.Append("스키마 ").Append(s.ParsingSchema ?? "?").Append(" · plan_hash ").Append(s.PlanHashValue.ToString(CultureInfo.InvariantCulture))
          .Append(" · 실행 ").Append(s.Executions.ToString("N0", CultureInfo.InvariantCulture))
          .Append(" · 경과 ").Append((s.ElapsedMicros / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture)).Append(" s")
          .Append(" · CPU ").Append((s.CpuMicros / 1_000_000d).ToString("0.00", CultureInfo.InvariantCulture)).Append(" s")
          .Append(" · buffer_gets ").Append(s.BufferGets.ToString("N0", CultureInfo.InvariantCulture))
          .Append(" · disk_reads ").Append(s.DiskReads.ToString("N0", CultureInfo.InvariantCulture))
          .Append(" · 행 ").Append(s.RowsProcessed.ToString("N0", CultureInfo.InvariantCulture))
          .Append(" · 자식 ").Append(s.VersionCount.ToString("N0", CultureInfo.InvariantCulture)).AppendLine();
        sb.AppendLine();
        sb.AppendLine("[추천 이유]");
        if (item.Recommendation.Reasons.Count == 0)
        {
            sb.AppendLine("(걸린 규칙 없음 — 지금 통계로는 튜닝 우선순위가 낮습니다)");
        }

        foreach (var r in item.Recommendation.Reasons)
        {
            sb.Append('+').Append(r.Points.ToString(CultureInfo.InvariantCulture)).Append("  ").Append(r.Title).Append(" — ").AppendLine(r.Evidence);
        }

        sb.AppendLine();
        sb.AppendLine(fullText is null ? "[SQL 앞부분]" : "[SQL 전문]");
        sb.AppendLine(fullText ?? s.SqlText);
        return sb.ToString();
    }

    partial void OnExcludeInternalChanged(bool value)
    {
        if (_set is not null)
        {
            Apply(focus: SelectedItem?.SqlId);
        }
    }

    partial void OnSelectedItemChanged(TuningRecommendationItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanOpenAssistant));
        OnPropertyChanged(nameof(CanOpenChildCursors));
        if (value is null)
        {
            DetailText = string.Empty;
            return;
        }

        DetailText = FormatDetail(value, _fullTexts.GetValueOrDefault(value.SqlId));
        _ = LoadSelectedAsync(value);
    }

    private async Task LoadAsync(string? focus, CancellationToken ct)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Exception? failure = null;
        TuningCandidateSet? set = null;
        var plans = new Dictionary<string, IReadOnlyList<PlanFindingLine>>(StringComparer.Ordinal);
        StatusText = "튜닝 추천: 공유 풀 통계를 읽는 중…";
        try
        {
            var options = Options();
            await _owner.RunTuningAsync(async (tuning, session) =>
            {
                set = await tuning.GetTuningCandidatesAsync(session, new TuningCandidateQuery(), ct);

                // 점수 상위 몇 개만 계획을 더 읽는다(부하 제한). 계획을 못 읽으면 계획 규칙만 건너뛴다.
                foreach (var r in TuningRecommendationRules.Rank(set, options).Where(r => !r.Sql.IsLiteralGroup).Take(TuningRecommendationRules.PlanCheckCount))
                {
                    try
                    {
                        plans[r.Sql.SqlId] = await tuning.GetPlanFindingsAsync(session, r.Sql.InstanceId, r.Sql.SqlId, r.Sql.PlanHashValue, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        // 계획이 밀려났거나 딕셔너리 권한이 없다.
                    }
                }
            }, ex => failure = ex, ct);
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() => StatusText = "취소했습니다.");
        }
        finally
        {
            _owner.PostToUi(() => IsBusy = false);
        }

        _owner.PostToUi(() =>
        {
            if (failure is not null)
            {
                StatusText = FailureText(failure);
                return;
            }

            if (set is null)
            {
                return;
            }

            _set = set;
            _plans.Clear();
            _extra.Clear();
            foreach (var p in plans)
            {
                _plans[p.Key] = p.Value;
            }

            OnPropertyChanged(nameof(IsLoaded));
            Apply(focus);
        });

        if (focus is not null && set is not null && failure is null && !Items.Any(i => i.SqlId == focus))
        {
            await LoadOneAsync(focus, ct);
        }
    }

    /// <summary>목록에 없는 SQL 하나를 따로 읽어 더한다.</summary>
    private async Task LoadOneAsync(string sqlId, CancellationToken ct)
    {
        Exception? failure = null;
        TuningCandidateSet? one = null;
        IReadOnlyList<PlanFindingLine>? plan = null;
        await _owner.RunTuningAsync(async (tuning, session) =>
        {
            one = await tuning.GetTuningCandidatesAsync(session, new TuningCandidateQuery { SqlIds = [sqlId], MinExecutions = 0, IncludeLiteralGroups = false }, ct);
            if (one.Rows.OrderByDescending(r => r.ElapsedMicros).FirstOrDefault() is { } top)
            {
                try
                {
                    plan = await tuning.GetPlanFindingsAsync(session, top.InstanceId, top.SqlId, top.PlanHashValue, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    plan = null;
                }
            }
        }, ex => failure = ex, ct);

        _owner.PostToUi(() =>
        {
            if (failure is not null)
            {
                StatusText = FailureText(failure);
                return;
            }

            if (one?.Rows.OrderByDescending(r => r.ElapsedMicros).FirstOrDefault() is not { } row)
            {
                StatusText = $"SQL {sqlId} 은(는) 공유 풀에 없습니다(밀려났을 수 있습니다).";
                return;
            }

            if (plan is not null)
            {
                _plans[sqlId] = plan;
            }

            _extra.Add(row);
            Apply(sqlId);
            if (SelectedItem?.Recommendation.Reasons.Count == 0)
            {
                StatusText = $"SQL {sqlId} 은(는) 추천 규칙에 걸리지 않았습니다(맨 아래에 더함).";
            }
        });
    }

    /// <summary>받은 후보로 다시 매겨 목록을 채운다. 고른 줄(또는 <paramref name="focus"/>)을 다시 고른다.</summary>
    private void Apply(string? focus)
    {
        if (_set is not { } set)
        {
            return;
        }

        var previous = SelectedItem?.Key;
        var ranked = TuningRecommendationRules.Rank(set, Options(), _plans).ToList();
        foreach (var s in _extra.Where(x => !ranked.Any(r => r.Sql.SqlId == x.SqlId)))
        {
            ranked.Add(TuningRecommendationRules.Evaluate(s, 0, _plans.GetValueOrDefault(s.SqlId)));
        }

        Items.Clear();
        foreach (var r in ranked)
        {
            Items.Add(new TuningRecommendationItem(r));
        }

        SelectedItem = (focus is not null ? Items.FirstOrDefault(i => i.SqlId == focus) : null)
            ?? (previous is { } k ? Items.FirstOrDefault(i => i.Key == k) : null);
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"튜닝 추천 {ranked.Count}건 · 후보 {set.Rows.Count}줄·리터럴 묶음 {set.LiteralGroups.Count}개 중{(ExcludeInternal ? " · 내부 스키마 제외" : string.Empty)} · {DateTime.Now:HH:mm:ss} 조회");
    }

    private bool Select(string sqlId)
    {
        if (Items.FirstOrDefault(i => i.SqlId == sqlId) is not { } item)
        {
            return false;
        }

        SelectedItem = item;
        return true;
    }

    /// <summary>고른 줄의 전문과(아직이면) 계획을 읽어 이유를 다시 매긴다.</summary>
    private async Task LoadSelectedAsync(TuningRecommendationItem item)
    {
        var id = item.SqlId;
        var needText = !_fullTexts.ContainsKey(id);
        var needPlan = !item.Sql.IsLiteralGroup && !_plans.ContainsKey(id);
        if (!needText && !needPlan)
        {
            return;
        }

        string? text = null;
        IReadOnlyList<PlanFindingLine>? plan = null;
        try
        {
            if (needText)
            {
                await _owner.RunMonitorQueryAsync(async (monitor, session) =>
                    text = (await monitor.GetSqlDetailAsync(session, new CursorRef(id, null, item.Sql.InstanceId), default)).FullText, _ => { }, default);
            }

            if (needPlan)
            {
                await _owner.RunTuningAsync(async (tuning, session) =>
                    plan = await tuning.GetPlanFindingsAsync(session, item.Sql.InstanceId, id, item.Sql.PlanHashValue, default), _ => { }, default);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _owner.PostToUi(() =>
        {
            if (text is not null)
            {
                _fullTexts[id] = text;
            }

            if (plan is not null)
            {
                _plans[id] = plan;
                var again = TuningRecommendationRules.Evaluate(item.Sql, item.Sql.IsLiteralGroup ? 0 : _set?.TotalElapsedMicros ?? 0, plan);
                if (again.Score != item.Score && Items.IndexOf(item) is var index and >= 0)
                {
                    var updated = new TuningRecommendationItem(again);
                    Items[index] = updated;
                    SelectedItem = updated;
                    return;
                }
            }

            if (ReferenceEquals(SelectedItem, item))
            {
                DetailText = FormatDetail(item, _fullTexts.GetValueOrDefault(id));
            }
        });
    }

    private async Task<string> FullTextAsync(TuningRecommendationItem item, CancellationToken ct)
    {
        if (_fullTexts.TryGetValue(item.SqlId, out var known))
        {
            return known;
        }

        string? text = null;
        await _owner.RunMonitorQueryAsync(async (monitor, session) =>
            text = (await monitor.GetSqlDetailAsync(session, new CursorRef(item.SqlId, null, item.Sql.InstanceId), ct)).FullText, _ => { }, ct);
        if (text is not null)
        {
            _fullTexts[item.SqlId] = text;
        }

        return text ?? item.Sql.SqlText;
    }

    private TuningRecommendationOptions Options() => new()
    {
        ExcludeInternalSchemas = ExcludeInternal,
    };

    private static string FailureText(Exception ex) => MonitorAccessState.IsPrivilegeError(ex)
        ? "튜닝 추천을 읽지 못했습니다: " + SqlMonitorViewModel.Describe(ex) + " — 모니터 접속 계정에 gv$sqlstats·gv$sql_plan 조회 권한(SELECT_CATALOG_ROLE 등)이 필요합니다."
        : "튜닝 추천을 읽지 못했습니다: " + SqlMonitorViewModel.Describe(ex);
}
