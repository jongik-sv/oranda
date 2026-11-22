using Oranda.Core.Ai.Setup;

namespace Oranda.Core.Tests.Ai.Setup;

/// <summary>도우미 이동 규칙(<see cref="AiSetupFlow"/>): 갈래별 단계·[다음]/[이전]·갈래 바꾸기·이어 하기.</summary>
public class AiSetupFlowTests
{
    [Fact]
    public void Each_path_has_the_handoff_steps_in_order()
    {
        Assert.Equal([AiSetupStepId.Choose], AiSetupFlow.StepsFor(null));
        Assert.Equal(
            [AiSetupStepId.Choose, AiSetupStepId.Install, AiSetupStepId.Login, AiSetupStepId.Test, AiSetupStepId.Access, AiSetupStepId.TryIt],
            AiSetupFlow.StepsFor(AiSetupPath.ClaudeAccount));
        Assert.Equal(
            [AiSetupStepId.Choose, AiSetupStepId.Server, AiSetupStepId.Test, AiSetupStepId.Access, AiSetupStepId.TryIt],
            AiSetupFlow.StepsFor(AiSetupPath.CompanyServer));
        Assert.Equal([AiSetupStepId.Choose, AiSetupStepId.AskAdmin], AiSetupFlow.StepsFor(AiSetupPath.NotSure));
        Assert.Equal([AiSetupStepId.Choose], AiSetupFlow.StepsFor((AiSetupPath)99));
    }

    [Fact]
    public void Next_is_enabled_only_when_the_current_step_is_done_and_back_is_off_on_the_first_step()
    {
        var flow = new AiSetupFlow();
        Assert.False(flow.CanGoNext);
        Assert.False(flow.CanGoBack);
        Assert.False(flow.GoNext());

        flow.ChoosePath(AiSetupPath.ClaudeAccount);
        Assert.True(flow.CanGoNext);
        Assert.True(flow.GoNext());
        Assert.Equal(AiSetupStepId.Install, flow.Current);
        Assert.True(flow.CanGoBack);
        Assert.False(flow.CanGoNext);

        flow.SetComplete(AiSetupStepId.Install, true);
        Assert.True(flow.CanGoNext);
        flow.SetComplete(AiSetupStepId.Install, false);
        Assert.False(flow.CanGoNext);

        Assert.True(flow.GoBack());
        Assert.Equal(AiSetupStepId.Choose, flow.Current);
        Assert.True(flow.CanGoNext);
    }

    [Fact]
    public void The_last_step_finishes_instead_of_going_next()
    {
        var flow = new AiSetupFlow();
        flow.ChoosePath(AiSetupPath.NotSure);
        flow.GoNext();

        Assert.True(flow.IsLastStep);
        Assert.False(flow.CanFinish);
        flow.SetComplete(AiSetupStepId.AskAdmin, true);
        Assert.True(flow.CanFinish);
        Assert.False(flow.CanGoNext);
    }

    [Fact]
    public void Changing_the_path_clears_the_later_steps_but_choosing_the_same_path_keeps_them()
    {
        var flow = new AiSetupFlow();
        flow.ChoosePath(AiSetupPath.ClaudeAccount);
        flow.GoNext();
        flow.SetComplete(AiSetupStepId.Install, true);
        flow.GoNext();
        flow.SetComplete(AiSetupStepId.Login, true);

        flow.ChoosePath(AiSetupPath.ClaudeAccount);
        Assert.True(flow.IsComplete(AiSetupStepId.Login));
        Assert.Equal(AiSetupStepId.Login, flow.Current);

        var changes = 0;
        flow.Changed += (_, _) => changes++;
        flow.ChoosePath(AiSetupPath.CompanyServer);
        Assert.Equal(1, changes);
        Assert.Equal([AiSetupStepId.Choose], flow.Completed);
        Assert.Equal(AiSetupStepId.Choose, flow.Current);

        flow.SetComplete(AiSetupStepId.Login, true); // 이 갈래에 없는 단계는 무시
        Assert.DoesNotContain(AiSetupStepId.Login, flow.Completed);
    }

    [Fact]
    public void Resume_restores_the_step_with_earlier_steps_done()
    {
        var flow = AiSetupFlow.Restore(AiSetupPath.CompanyServer, AiSetupStepId.Access);

        Assert.Equal(AiSetupPath.CompanyServer, flow.Path);
        Assert.Equal(AiSetupStepId.Access, flow.Current);
        Assert.Equal(new HashSet<AiSetupStepId> { AiSetupStepId.Choose, AiSetupStepId.Server, AiSetupStepId.Test }, flow.Completed.ToHashSet());
        Assert.False(flow.IsComplete(AiSetupStepId.Access));
        Assert.True(flow.CanGoBack);
    }

    [Fact]
    public void Choosing_the_same_path_after_resume_keeps_the_resumed_step()
    {
        var flow = AiSetupFlow.Restore(AiSetupPath.ClaudeAccount, AiSetupStepId.Login);

        flow.ChoosePath(AiSetupPath.ClaudeAccount);

        Assert.Equal(AiSetupStepId.Login, flow.Current);
        Assert.True(flow.IsComplete(AiSetupStepId.Install));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(AiSetupPath.ClaudeAccount, null)]
    [InlineData(null, AiSetupStepId.Login)]
    [InlineData(AiSetupPath.NotSure, AiSetupStepId.Login)] // 갈래에 없는 단계
    [InlineData((AiSetupPath)42, AiSetupStepId.Choose)]
    [InlineData(AiSetupPath.ClaudeAccount, (AiSetupStepId)42)]
    public void Unknown_resume_values_start_over(AiSetupPath? path, AiSetupStepId? step)
    {
        var flow = AiSetupFlow.Restore(path, step);

        Assert.Null(flow.Path);
        Assert.Equal(AiSetupStepId.Choose, flow.Current);
        Assert.Empty(flow.Completed);
    }
}
