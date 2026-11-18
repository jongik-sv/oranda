using Avalonia.Threading;
using Oranda.App.Features.Execution;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell.Composition;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;

namespace Oranda.App.Features.Flashback;

/// <summary>
/// VERSIONS 결과 그리드 하나의 바뀐 칸 표시. 받은 행은 자체 복사본에 덧붙여(한 번 읽은 행은 다시 읽지 않는다, 메모리 한도로 페이지가 빠져도 그대로)
/// 행이 늘면 계산을 한 번 미뤄 모아서 한다(모두 가져오기 중에도 페이지마다 다시 돌지 않는다). 그리기는 계산 결과만 읽는다.
/// </summary>
public sealed class VersionsHighlight
{
    /// <summary>결과 요약 줄 안내.</summary>
    public const string Notice = "앞 버전과 값이 달라진 칸은 배경색으로 보입니다.";

    private readonly ResultGridModel _model;
    private readonly VersionsLayout _layout;
    private readonly bool? _newestFirst;
    private readonly List<object?[]?> _rows = [];
    private IReadOnlyDictionary<int, int[]> _changed = new Dictionary<int, int[]>();
    private bool _pending;
    private bool _noticed;

    private VersionsHighlight(ResultGridModel model, VersionsLayout layout, bool? newestFirst)
    {
        _model = model;
        _layout = layout;
        _newestFirst = newestFirst;
    }

    /// <summary>
    /// 결과 영역에 새 결과가 붙을 때(<see cref="SqlResultPaneViewModel.ResultAttached"/>) 그 문장이 단일 표 <c>VERSIONS BETWEEN</c> 조회면
    /// 같은 키 행끼리 앞 버전과 값이 다른 데이터 칸 바탕을 칠하게 건다(「이 행 변경 이력」 결과와 직접 쓴 VERSIONS 질의 둘 다).
    /// 키 = 일반 실행 결과와 같은 판정의 PK(<see cref="FlashbackCommandHandler.PlainTargetAsync"/>), 없으면 ROWID 열, 둘 다 없으면 칠하지 않는다.
    /// <see cref="ResultGridSettingsStartup"/> 가 부른다.
    /// </summary>
    public static void Install(IServiceProvider services)
    {
        if (services is ServiceContainer container)
        {
            SqlResultPaneViewModel.ResultAttached = (pane, model) => _ = TryAttachAsync(container, pane, model);
        }
    }

    /// <summary>
    /// 문장이 단일 표 VERSIONS 조회면 그 SELECT 목록 항목(결과 열과 1:1 로 맞으면 원래 열 이름, 아니면 null 목록), 아니면 null.
    /// AS OF·유효 시간(PERIOD)·여러 표·DB 링크 결과는 대상이 아니다.
    /// </summary>
    public static IReadOnlyList<string?>? VersionsSources(string text, int columnCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        var shape = FlashbackResultAnalyzer.Analyze(text);
        if (shape is not { IsPastValues: true, Kind: FlashbackClauseKind.Versions })
        {
            return null;
        }

        var items = SelectAnalyzer.Analyze(text).Items;
        return items.Count == columnCount && !items.Any(i => i.IsStar) ? [.. items.Select(i => i.Column)] : new string?[columnCount];
    }

    /// <summary>
    /// <paramref name="model"/> 에 바뀐 칸 표시를 꽂는다. <paramref name="keys"/> = 키 열 번호(비면 결과 전체를 한 키로 본다).
    /// <paramref name="sources"/> = <see cref="VersionsSources"/>. <paramref name="newestFirst"/> = <see cref="VersionsDiff.OrderNewestFirst"/>.
    /// 지금 읽힌 행은 바로 계산한다.
    /// </summary>
    public static VersionsHighlight Attach(ResultGridModel model, IReadOnlyList<int> keys, IReadOnlyList<string?>? sources, bool? newestFirst = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        // ROWID 형 열은 별칭이 붙어도 ROWID 로 본다(견주지 않는다).
        var known = Enumerable.Range(0, model.Columns.Count)
            .Select(i => (sources is not null && i < sources.Count ? sources[i] : null) ?? (model.Columns[i].Kind == ColumnDataKind.RowId ? "ROWID" : null))
            .ToArray();
        var layout = VersionsDiff.Layout([.. model.Columns.Select(c => c.Name)], known, keys);
        var h = new VersionsHighlight(model, layout, newestFirst);
        model.CellChanged = h.IsChanged;
        model.Changed += (_, _) => h.Schedule();
        h.Recompute();
        return h;
    }

    /// <summary>결과 영역에 붙은 새 결과가 VERSIONS 조회면 키를 찾아 표시를 꽂는다. 대상이 아니거나 키를 못 찾으면 아무것도 하지 않는다.</summary>
    internal static async Task TryAttachAsync(ServiceContainer container, SqlResultPaneViewModel pane, ResultGridModel model)
    {
        try
        {
            if (pane.Statement is not { } statement || VersionsSources(statement.Text, model.Columns.Count) is not { } sources)
            {
                return;
            }

            var keys = await KeysAsync(container, statement, model, sources).ConfigureAwait(true);
            if (keys is not null && ReferenceEquals(pane.Model, model))
            {
                var highlight = Attach(model, keys, sources, VersionsDiff.OrderNewestFirst(statement.Text));
                // 「차이 요약」 입구와 AI 띠(설계 §4.2): 결과 그리드 위 자리에 붙는다. 이 결과를 만든 세션의 접속 기준으로 보낸다.
                var profile = ExecutionCommands.ActiveTabSession(container)?.Profile;
                var table = FlashbackResultAnalyzer.Analyze(statement.Text).Table is { Name: { } name } t ? (t.Owner is { Length: > 0 } owner ? owner + "." + name : name) : "?";
                pane.FeatureStrip = new Ai.FlashbackDiffSummaryViewModel(new Oranda.App.Features.Ai.InPlace.AiInPlaceHost(container), () => highlight.SummaryInput(table), () => profile);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 표시는 덤이다: 실패하면 칠하지 않고 결과는 그대로 둔다.
        }
    }

    /// <summary>키 열 번호: PK(일반 실행 결과와 같은 판정) → ROWID 열 → null(칠하지 않음).</summary>
    private static async Task<IReadOnlyList<int>?> KeysAsync(ServiceContainer container, Oranda.Core.Execution.SqlStatement statement, ResultGridModel model,
                                                            IReadOnlyList<string?> sources)
    {
        try
        {
            // 결과가 붙는 때의 활성 편집기 탭 세션 = 방금 이 문장을 실행한 세션.
            if (ExecutionCommands.ActiveTabSession(container) is { } session
                && container.GetService<IRowEditor>() is { } editor
                && container.GetService<IMetadataProvider>() is { } metadata)
            {
                var (target, _) = await FlashbackCommandHandler.PlainTargetAsync(editor, metadata, session, statement, model.Columns, CancellationToken.None).ConfigureAwait(true);
                if (target is not null)
                {
                    return [.. target.KeyPositions.Select(p => target.Ordinals[p])];
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 메타데이터를 못 읽으면 ROWID 열로 넘어간다.
        }

        var rowId = Enumerable.Range(0, model.Columns.Count).FirstOrDefault(i =>
            model.Columns[i].Kind == ColumnDataKind.RowId
            || string.Equals(model.Columns[i].Name, "ROWID", StringComparison.OrdinalIgnoreCase)
            || string.Equals(i < sources.Count ? sources[i] : null, "ROWID", StringComparison.OrdinalIgnoreCase), -1);
        return rowId >= 0 ? [rowId] : null;
    }

    /// <summary>
    /// 「차이 요약」(AI, 설계 §4.2) 입력: 지금까지 받은 행을 같은 키끼리 시간 순으로, 버전마다 앞 버전과 달라진 칸(앞뒤 값)을 담는다.
    /// 값은 여기서 그대로 담고, 보낼지는 프롬프트가 허용 수준으로 정한다.
    /// </summary>
    internal Ai.FlashbackDiffInput SummaryInput(string tableName)
    {
        Recompute();
        var names = _model.Columns.Select(c => c.Name).ToList();
        var versions = new List<Ai.FlashbackVersion>();
        foreach (var list in VersionsDiff.Ordered(_layout, _rows, _newestFirst))
        {
            object?[]? prev = null;
            foreach (var index in list)
            {
                var row = _rows[index]!;
                var changes = prev is null ? [] : _layout.Data
                    .Where(d => !FlashbackDiff.SameValue(prev[d], row[d]))
                    .Select(d => new Ai.FlashbackVersionChange(names[d], Text(prev[d]), Text(row[d])))
                    .ToList();
                versions.Add(new Ai.FlashbackVersion(At(_layout.Operation), At(_layout.StartTime), At(_layout.StartScn), changes)
                {
                    KeyText = _layout.Keys.Count == 0 ? null : string.Join(", ", _layout.Keys.Select(k => names[k] + "=" + (Text(row[k]) ?? "NULL"))),
                });
                prev = row;

                string At(int ordinal) => ordinal >= 0 ? Text(row[ordinal]) ?? string.Empty : string.Empty;
            }
        }

        return new Ai.FlashbackDiffInput(tableName, [.. _layout.Keys.Select(k => names[k])], versions);

        static string? Text(object? value) => value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>이 칸이 앞 버전과 값이 다른가(계산해 둔 것만 읽는다).</summary>
    public bool IsChanged(long viewRow, int ordinal)
    {
        var src = _model.SourceRowOf(viewRow);
        return src is >= 0 and <= int.MaxValue && _changed.TryGetValue((int)src, out var cols) && Array.IndexOf(cols, ordinal) >= 0;
    }

    /// <summary>행이 늘었으면 계산을 한 번 미룬다(여러 페이지가 잇달아 와도 쉴 때 한 번).</summary>
    private void Schedule()
    {
        if (_pending || _model.LoadedRowCount == _rows.Count)
        {
            return;
        }

        _pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _pending = false;
            Recompute();
        }, DispatcherPriority.Background);
    }

    /// <summary>새로 받은 행을 복사본에 덧붙이고 바뀐 칸을 다시 계산한다. 결과가 바뀌었을 때만 다시 그린다.</summary>
    private void Recompute()
    {
        var loaded = Math.Min(_model.LoadedRowCount, int.MaxValue);
        if (loaded == _rows.Count)
        {
            return;
        }

        var width = _model.Columns.Count;
        for (var r = _rows.Count; r < loaded; r++)
        {
            var values = new object?[width];
            var ok = true;
            for (var c = 0; c < width && ok; c++)
            {
                ok = _model.TryGetSourceValue(r, c, out values[c]);
            }

            _rows.Add(ok ? values : null); // 이미 메모리에서 빠진 행(드묾)은 건너뛴다
        }

        var before = _changed;
        _changed = VersionsDiff.Changed(_layout, _rows, _newestFirst);
        if (!_noticed && _changed.Count > 0)
        {
            _noticed = true;
            _model.ReportNotice(Notice);
        }

        if (before.Count > 0 || _changed.Count > 0)
        {
            _model.NotifyCellBackgroundChanged();
        }
    }
}
