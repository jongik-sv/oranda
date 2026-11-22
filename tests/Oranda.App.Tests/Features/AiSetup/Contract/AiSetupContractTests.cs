using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Features;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.AiSetup.Contract;

/// <summary>도우미 계약(W1): aiTuning 에 묶이지 않는 네임스페이스, 단계 제목 한/영, 단계 사이 값 알림.</summary>
public sealed class AiSetupContractTests
{
    [Fact]
    public void The_setup_namespace_is_common_and_not_tied_to_ai_tuning()
    {
        Assert.Null(FeatureCatalog.FeatureFor(AiSetupIds.OpenCommand, typeof(AiSetupContext)));
        Assert.Null(FeatureCatalog.FeatureFor(null, "Oranda.App.Features.AiSetup.Wizard.AiSetupWindow", "Oranda.App.Features.AiSetup.Wizard"));
        Assert.Equal(FeatureIds.AiTuning, FeatureCatalog.FeatureFor(null, "Oranda.App.Features.Ai.Chat.X", "Oranda.App.Features.Ai.Chat"));
    }

    [Fact]
    public void Every_step_and_path_has_a_title_in_both_languages()
    {
        // 모르는 값은 이름 그대로 나오므로, 아는 값은 한/영이 서로 다르고 비어 있지 않아야 한다.
        foreach (var step in Enum.GetValues<AiSetupStepId>())
        {
            Assert.NotEqual(AiSetupText.StepTitle(step, english: false), AiSetupText.StepTitle(step, english: true));
        }

        foreach (var path in Enum.GetValues<AiSetupPath>())
        {
            Assert.NotEqual(AiSetupText.PathTitle(path, english: false), AiSetupText.PathTitle(path, english: true));
        }

        Assert.Equal("로그인", AiSetupText.StepTitle(AiSetupStepId.Login, english: false));
        Assert.Equal("Sign in", AiSetupText.StepTitle(AiSetupStepId.Login, english: true));
        Assert.NotEqual(AiSetupText.WindowTitle(false), AiSetupText.WindowTitle(true));
    }

    [Fact]
    public void Choosing_a_path_on_the_context_tells_the_window_once()
    {
        var context = new AiSetupContext(new SettingsService(new InMemorySettingsStore()));
        var changes = 0;
        context.ChosenPathChanged += (_, _) => changes++;

        context.ChosenPath = AiSetupPath.CompanyServer;
        context.ChosenPath = AiSetupPath.CompanyServer;

        Assert.Equal(1, changes);
        Assert.Equal(AiSetupPath.CompanyServer, context.ChosenPath);
        Assert.Empty(context.EnabledConnectionIds);
        Assert.False(context.OpenUrl("https://ai.example.com/v1"));
    }
}
