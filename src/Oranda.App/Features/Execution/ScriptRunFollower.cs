using System.Diagnostics;
using Avalonia.Threading;
using Oranda.App.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Text;

namespace Oranda.App.Features.Execution;

/// <summary>
/// 여러 문장 스크립트 실행 중 지금 실행하는 문장을 편집기에 보인다: 실행을 시작한 탭의 커서를 그 문장 시작으로 옮기고(화면에 보이게, 포커스는 옮기지 않는다)
/// 문장 범위에 옅은 배경을 칠한다. 문장이 빨리 지나가면 <see cref="Interval"/> 안의 바뀜은 마지막 것 하나로 모아 그린다(수천 문장에서도 화면 갱신이 적다).
/// 끝·취소면 표시를 지우고, 오류로 멈추면 그 문장에 커서(오류 자리)와 오류 표시를 남긴다. 실행 중 글이 바뀌면 범위가 어긋나므로 더 따라가지 않는다.
/// 글은 바꾸지 않는다. 문장 범위는 실행 전에 정해졌으므로 커서를 옮겨도 이번 실행의 범위는 그대로다. UI 스레드에서만 부른다.
/// </summary>
internal sealed class ScriptRunFollower
{
    /// <summary>화면 갱신을 모으는 간격.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);

    private readonly SqlEditorViewModel _tab;
    private readonly IReadOnlyList<SqlStatement> _statements;
    private readonly Func<TimeSpan> _now;
    private readonly long _version;
    private TimeSpan? _lastApplied;
    private int _pending = -1;
    private bool _scheduled;
    private bool _finished;

    public ScriptRunFollower(SqlEditorViewModel tab, IReadOnlyList<SqlStatement> statements, Func<TimeSpan>? now = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(statements);
        _tab = tab;
        _statements = statements;
        long started = Stopwatch.GetTimestamp();
        _now = now ?? (() => Stopwatch.GetElapsedTime(started));
        _version = tab.Version;
    }

    /// <summary>실행 중 글이 바뀌었는가(그러면 범위가 어긋나 더 따라가지 않는다).</summary>
    private bool Stale => _tab.Version != _version;

    /// <summary>
    /// <paramref name="index"/> 번째 문장을 지금 실행한다. 마지막 갱신에서 <see cref="Interval"/> 이 지났으면 바로 그리고,
    /// 아니면 남은 시간 뒤에 그때의 마지막 문장을 한 번 그린다.
    /// </summary>
    public void Show(int index)
    {
        if (_finished || Stale || index < 0 || index >= _statements.Count)
        {
            return;
        }

        _pending = index;
        var now = _now();
        if (_lastApplied is not { } last || now - last >= Interval)
        {
            Flush();
            return;
        }

        if (!_scheduled)
        {
            _scheduled = true;
            DispatcherTimer.RunOnce(() =>
            {
                _scheduled = false;
                Flush();
            }, Interval - (now - last));
        }
    }

    /// <summary>미뤄 둔 갱신을 지금 그린다(타이머가 부르며, 시험은 직접 부른다).</summary>
    internal void Flush()
    {
        if (_finished || _pending < 0 || Stale)
        {
            return;
        }

        var stmt = _statements[_pending];
        _pending = -1;
        _lastApplied = _now();
        Mark(stmt, failed: false, caret: stmt.SourcePieces is { Count: > 0 } pieces ? pieces[0].DocumentOffset : stmt.Range.Start);
    }

    /// <summary>실행이 끝났거나 취소됐다: 표시를 지운다. 커서는 마지막으로 옮긴 자리에 둔다.</summary>
    public void End()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _tab.ClearRunningStatement();
    }

    /// <summary>오류로 멈췄다: 실패한 문장에 오류 표시를 남기고 커서를 오류 자리(<paramref name="errorOffset"/>, 문장 글 안 위치)로 옮긴다.</summary>
    public void Fail(SqlStatement failed, int? errorOffset)
    {
        ArgumentNullException.ThrowIfNull(failed);
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (Stale)
        {
            _tab.ClearRunningStatement();
            return;
        }

        Mark(failed, failed: true, caret: ExecutionController.DocumentOffset(failed, errorOffset ?? 0));
    }

    private void Mark(SqlStatement stmt, bool failed, int caret)
    {
        if (stmt.Range.Length == 0)
        {
            return; // 문서 밖에서 만든 문장
        }

        IReadOnlyList<TextRange> ranges = stmt.SourcePieces is { Count: > 0 } pieces
            ? [.. pieces.Select(p => new TextRange(p.DocumentOffset, p.Length))]
            : [stmt.Range];
        _tab.SetRunningStatement(ranges, failed);
        _tab.Select(caret, 0); // 포커스는 옮기지 않는다(Surface.SelectRange 는 커서가 보이게 스크롤만 한다)
    }
}
