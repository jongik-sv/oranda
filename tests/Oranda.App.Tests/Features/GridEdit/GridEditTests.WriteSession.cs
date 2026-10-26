using Oranda.App.Features.Execution;
using Oranda.App.Features.GridEdit;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.GridEdit;

// 소유: TSK-14-08. 격자 반영은 결과를 만든 세션이 지금 그 탭의 세션일 때만, 반영하는 동안 그 세션을 「실행 중」 으로 둔다.
public sealed partial class GridEditTests
{
    [Fact]
    public Task Apply_is_refused_when_the_tab_now_uses_another_session_than_the_one_that_made_the_result() => Ui.Run(async () =>
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        var other = new TestSession(readOnly: false);
        s.CurrentSession = () => other; // 결과를 만든 뒤 탭의 작업 공간이 다른 세션으로 옮겼다
        var previews = 0;
        var reported = new List<string>();
        GridEditCommands.ConfirmPreview = (_, _, _) =>
        {
            previews++;
            return Task.FromResult(true);
        };
        GridEditCommands.Report = reported.Add;
        s.SetCell(0, 1, "X");

        await Run("oranda.grid.edit.applyAndCommit");
        await Run("oranda.grid.edit.apply");
        await Run("oranda.grid.edit.previewDml");

        Assert.Empty(_exec.Dml); // 옛 세션(다른 작업 공간의 트랜잭션)에 보내지 않았다
        Assert.Equal(0, _session.Commits);
        Assert.Equal(0, previews); // 미리보기도 띄우지 않는다
        Assert.True(s.IsDirty); // 바꾼 값은 남는다
        Assert.Equal(3, reported.Count);
        Assert.All(reported, r => Assert.Contains("다시 조회", r, StringComparison.Ordinal));
        Assert.Contains("세션", s.Notice, StringComparison.Ordinal);

        // 탭이 다시 그 세션을 쓰면 반영된다.
        s.CurrentSession = () => _session;
        await Run("oranda.grid.edit.applyAndCommit");
        Assert.Single(_exec.Dml);
        Assert.Equal(1, _session.Commits);
    });

    [Fact]
    public Task Apply_is_refused_when_the_tab_has_lost_its_session() => Ui.Run(async () =>
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        GridEditCommands.Active = s;
        s.CurrentSession = () => null; // 작업 공간이 쓰던 세션이 닫혔다(접속 안 됨)
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        s.SetCell(0, 1, "X");

        await Run("oranda.grid.edit.apply");

        Assert.Empty(_exec.Dml);
    });

    [Fact]
    public Task Apply_marks_the_session_running_for_its_workspace_and_releases_it_after() => Ui.Run(async () =>
    {
        var s = await OpenAsync("SELECT empno, ename, sal FROM emp");
        var bus = new EventBus();
        var state = new ExecutionState(bus);
        bus.Publish(new SessionOpened(_session.Id, _session.Profile, _session.Server, SessionPurpose.Work));
        var other = new TestSession(readOnly: false);
        bus.Publish(new SessionOpened(other.Id, other.Profile, other.Server, SessionPurpose.Work)); // 지금 접속은 다른 세션
        var workspace = new object();
        GridEditCommands.State = state;
        GridEditCommands.Active = s;
        s.Owner = workspace;
        GridEditCommands.ConfirmPreview = (_, _, _) => Task.FromResult(true);
        bool? running = null, otherRunning = null;
        object? owner = null;
        _exec.RowsFor = _ =>
        {
            running = state.IsRunning(_session.Id);
            otherRunning = state.IsRunning(other.Id);
            owner = state.RunOwnerOf(_session.Id);
            return 1;
        };
        s.SetCell(0, 1, "X");

        await Run("oranda.grid.edit.apply");

        Assert.True(running); // 반영하는 동안 그 세션은 실행 중(세션 창 커밋·닫기 꺼짐, 같은 세션을 쓰는 다른 작업 공간은 바쁨 막대)
        Assert.False(otherRunning); // 지금 접속의 표시는 건드리지 않는다
        Assert.Same(workspace, owner);
        Assert.False(state.IsRunning(_session.Id));

        // 그 세션이 이미 다른 실행으로 바쁘면 반영하지 않는다(같은 세션에 섞어 보내지 않는다).
        state.BeginRun(_session.Id, new object());
        s.SetCell(1, 1, "Y");
        var before = _exec.Dml.Count;
        await Run("oranda.grid.edit.apply");
        Assert.Equal(before, _exec.Dml.Count);
        Assert.True(s.IsDirty);
        state.EndRun(_session.Id);
    });
}
