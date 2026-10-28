using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Tuning;

namespace Oranda.App.Features.SessionMonitor;

/// <summary>활성 세션이 실행 중인 SQL 하나의 추천 결과.</summary>
public sealed class SessionTuningHintItem(TuningRecommendation rec, IReadOnlyList<SessionItem> sessions)
{
    public TuningRecommendation Recommendation { get; } = rec;

    public IReadOnlyList<SessionItem> Sessions { get; } = sessions;

    public string SqlId => Recommendation.Sql.SqlId;

    public int Score => Recommendation.Score;

    public bool IsRecommended => Recommendation.Reasons.Count > 0;

    /// <summary>「78점 · 실행당 오래 걸림 · 큰 표 전체 스캔」 또는 「걸린 규칙 없음」.</summary>
    public string ReasonText => IsRecommended
        ? Score.ToString(CultureInfo.InvariantCulture) + "점 · " + Recommendation.Summary
        : "걸린 규칙 없음";

    /// <summary>목록 한 줄: sql_id, 세션들, 이유.</summary>
    public string Title => $"{SqlId}  [{string.Join(", ", Sessions.Select(s => s.KeyText))}]  {ReasonText}";

    /// <summary>근거 수치까지 여러 줄.</summary>
    public string Evidence => string.Join(Environment.NewLine, Recommendation.Reasons.Select(r => $"+{r.Points} {r.Title} — {r.Evidence}"));
}

/// <summary>
/// 세션 모니터 「튜닝 추천」 탭(TSK-14-17): 지금 ACTIVE 세션이 실행 중인 SQL 에 SQL 모니터와 같은 규칙(<see cref="TuningRecommendationRules"/>)을 적용한다.
/// 검사는 수동(「검사」 단추) — sql_id 로 좁혀 gv$sqlstats 를 한 번 읽는다(계획 규칙·리터럴 묶음·비중 규칙은 SQL 모니터에서만).
/// 고른 세션의 SQL 줄을 함께 고르고, 「SQL 모니터의 튜닝 추천에서 보기」 는 <c>oranda.tuning.recommend</c>(인자 sql_id)를 부른다.
/// </summary>
public sealed partial class SessionTuningHintsViewModel : ObservableObject
{
    /// <summary>SQL 모니터 튜닝 추천 명령(SqlMonitorViewModel.RecommendCommand 와 같은 값).</summary>
    public const string RecommendCommand = "oranda.tuning.recommend";

    private readonly SessionMonitorViewModel _owner;
    private bool _checked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedEvidence))]
    private SessionTuningHintItem? _selectedHint;

    [ObservableProperty]
    private string _statusText = "「검사」를 누르면 지금 활성 세션이 실행 중인 SQL 을 튜닝 추천 규칙으로 살펴봅니다.";

    [ObservableProperty]
    private string _selectedSessionText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck))]
    private bool _isBusy;

    public SessionTuningHintsViewModel(SessionMonitorViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionMonitorViewModel.SelectedSession))
            {
                SyncSelectedSession();
            }
            else if (e.PropertyName == nameof(SessionMonitorViewModel.IsOpen))
            {
                OnPropertyChanged(nameof(CanCheck));
                if (!owner.IsOpen)
                {
                    Items.Clear();
                    SelectedHint = null;
                    _checked = false;
                    SyncSelectedSession();
                }
            }
        };
    }

    public ObservableCollection<SessionTuningHintItem> Items { get; } = [];

    public bool CanCheck => _owner.IsOpen && !IsBusy;

    public bool HasSelection => SelectedHint is not null;

    public string SelectedEvidence => SelectedHint?.Evidence ?? string.Empty;

    /// <summary>「SQL 모니터의 튜닝 추천에서 보기」 단추를 보일지(SQL 모니터 기능이 켜져 있을 때만).</summary>
    public bool CanOpenInSqlMonitor => _owner.CanRunCommand(RecommendCommand);

    /// <summary>지금 목록의 ACTIVE 세션 SQL 을 검사한다. 추천에 걸린 것이 위(점수 큰 순), 안 걸린 것은 아래.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (IsBusy)
        {
            return;
        }

        var bySql = _owner.Sessions
            .Where(s => string.Equals(s.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) && s.SqlId.Length > 0)
            .GroupBy(s => s.SqlId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<SessionItem>)[.. g], StringComparer.Ordinal);
        if (bySql.Count == 0)
        {
            Items.Clear();
            SelectedHint = null;
            StatusText = "SQL 을 실행 중인 활성 세션이 없습니다(목록을 새로 고친 뒤 다시 검사하세요).";
            return;
        }

        IsBusy = true;
        Exception? failure = null;
        TuningCandidateSet? set = null;
        try
        {
            await _owner.RunTuningAsync(async (tuning, session) =>
                set = await tuning.GetTuningCandidatesAsync(session, new TuningCandidateQuery
                {
                    SqlIds = [.. bySql.Keys],
                    MinExecutions = 0,
                    IncludeLiteralGroups = false,
                    Top = Math.Max(1, bySql.Count * 4), // plan_hash·인스턴스별 줄이 여럿일 수 있다
                }, ct), ex => failure = ex, ct);
        }
        catch (OperationCanceledException)
        {
            _owner.PostToUi(() => StatusText = "취소했습니다.");
            return;
        }
        finally
        {
            _owner.PostToUi(() => IsBusy = false);
        }

        _owner.PostToUi(() =>
        {
            if (failure is not null)
            {
                StatusText = "튜닝 추천을 검사하지 못했습니다: " + FirstLine(failure.Message)
                    + (MonitorAccessState.IsPrivilegeError(failure) ? " — gv$sqlstats 조회 권한이 필요합니다." : string.Empty);
                return;
            }

            if (set is not null)
            {
                Apply(set, bySql);
            }
        });
    }

    /// <summary>「SQL 모니터의 튜닝 추천에서 보기」: 고른 줄의 sql_id 로 SQL 모니터 튜닝 추천 탭을 연다.</summary>
    public Task OpenInSqlMonitorAsync() =>
        SelectedHint is { } hint && CanOpenInSqlMonitor ? _owner.RunShellCommandAsync(RecommendCommand, hint.SqlId) : Task.CompletedTask;

    private void Apply(TuningCandidateSet set, Dictionary<string, IReadOnlyList<SessionItem>> bySql)
    {
        var options = new TuningRecommendationOptions { MinExecutions = 0 };
        var hints = new List<SessionTuningHintItem>();
        foreach (var (sqlId, sessions) in bySql)
        {
            // sql_id 에 줄이 여럿(plan_hash·인스턴스)이면 경과가 가장 큰 줄로 본다.
            if (set.Rows.Where(r => r.SqlId == sqlId).OrderByDescending(r => r.ElapsedMicros).FirstOrDefault() is not { } row)
            {
                continue;
            }

            var rec = TuningRecommendationRules.IsTarget(row, options)
                ? TuningRecommendationRules.Evaluate(row, 0)
                : new TuningRecommendation(row, 0, []);
            hints.Add(new SessionTuningHintItem(rec, sessions));
        }

        Items.Clear();
        foreach (var h in hints.OrderByDescending(h => h.Score).ThenBy(h => h.SqlId, StringComparer.Ordinal))
        {
            Items.Add(h);
        }

        _checked = true;
        var hit = Items.Count(i => i.IsRecommended);
        var missing = bySql.Count - Items.Count;
        StatusText = string.Create(CultureInfo.InvariantCulture,
            $"활성 세션 SQL {bySql.Count}개 중 튜닝 추천 {hit}개{(missing > 0 ? $" (공유 풀에 없는 {missing}개 제외)" : string.Empty)} · {DateTime.Now:HH:mm:ss} 검사");
        SyncSelectedSession();
    }

    /// <summary>세션 목록에서 고른 세션의 SQL 줄을 골라 「이 SQL 튜닝 추천: …」 을 보인다.</summary>
    private void SyncSelectedSession()
    {
        OnPropertyChanged(nameof(CanOpenInSqlMonitor));
        if (_owner.SelectedSession is not { } session || session.SqlId.Length == 0)
        {
            SelectedSessionText = string.Empty;
            return;
        }

        if (Items.FirstOrDefault(i => i.SqlId == session.SqlId) is { } hint)
        {
            SelectedHint = hint;
            SelectedSessionText = $"세션 {session.KeyText} 의 SQL {hint.SqlId} 튜닝 추천: {hint.ReasonText}";
        }
        else
        {
            SelectedSessionText = _checked
                ? $"세션 {session.KeyText} 의 SQL {session.SqlId}: 검사 결과 없음(활성 아님 또는 공유 풀에 없음)"
                : $"세션 {session.KeyText} 의 SQL {session.SqlId}: 아직 검사하지 않았습니다.";
        }
    }

    private static string FirstLine(string message)
    {
        var i = message.IndexOf('\n', StringComparison.Ordinal);
        return (i < 0 ? message : message[..i]).Trim();
    }
}
