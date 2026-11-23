using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Oranda.App.Features.AiSetup.Access;
using Oranda.App.Features.AiSetup.Contract;
using Oranda.App.Features.AiSetup.Entry;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.Ai;
using Oranda.App.Tests.Features.AiSetup.Access;
using Oranda.App.Tests.Features.AiSetup.Wizard;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Composition;

namespace Oranda.App.Tests.Features.AiSetup.Contract;

/// <summary>
/// W4 영어 한 바퀴: 갈래 A·B·C 의 단계 8개를 영어 맥락(<see cref="AiSetupContext.Text"/> = en)으로 만들어 화면에 올렸을 때 제목·글·단추에 한글이 남지 않는다.
/// 정적 언어(<c>UiText</c>·<c>LiteralTranslator</c>)는 건드리지 않는다(다른 시험과 병렬로 돌아도 흔들리지 않게). 그래서 정적 번역기를 거치는 글이 섞이면
/// 한국어로 남아 이 시험에 걸린다(단계 글은 맥락 언어만 따라야 한다). 단계마다 들어온 직후 상태만 본다.
/// </summary>
public sealed partial class AiSetupEnglishSweepTests
{
    [GeneratedRegex("[\\uAC00-\\uD7A3]")]
    private static partial Regex Hangul();

    private static IReadOnlyList<IAiSetupStepFactory> Factories() =>
        [.. typeof(AccessStep).Assembly.GetTypes()
            .Where(t => t.GetCustomAttributes<OrandaServiceAttribute>().Any(a => a.ServiceType == typeof(IAiSetupStepFactory)))
            .Select(t => (IAiSetupStepFactory)Activator.CreateInstance(t, nonPublic: true)!)];

    [Fact]
    public Task Every_step_of_every_path_is_english_in_an_english_context() => HeadlessHost.Run(async () =>
    {
        var profiles = await ProfileHarness.CreateAsync();
        var context = new AiSetupContext(profiles.Settings)
        {
            Text = new ShellText(language: "en"),
            Connections = new FakeConnections(),
            Claude = new FakeClaude(),
            Probe = profiles.Probe,
            Credentials = profiles.Credentials,
            Bus = profiles.Bus,
            AppVersion = "0.1.5",
        };
        var factories = Factories();
        var steps = Enum.GetValues<AiSetupPath>().SelectMany(p => AiSetupFlow.StepsFor(p)).Distinct().ToList();
        Assert.Equal(Enum.GetValues<AiSetupStepId>().Length, steps.Count);

        var problems = new List<string>();
        foreach (var id in steps)
        {
            using var step = Assert.Single(factories, f => f.StepId == id).Create(context);
            if (Hangul().IsMatch(step.Title))
            {
                problems.Add($"{id} 제목: {step.Title}");
            }

            var window = new Window { Width = 720, Height = 560, Content = step.View };
            window.Show();
            try
            {
                await step.EnterAsync(CancellationToken.None);
                HeadlessHost.Settle(window);
                var texts = VisibleTexts(window).ToList();
                if (texts.Count < 2)
                {
                    problems.Add($"{id}: 화면 글을 찾지 못함({texts.Count}개) — 훑기가 깨졌다");
                }

                foreach (var text in texts)
                {
                    if (Hangul().IsMatch(text))
                    {
                        problems.Add($"{id}: {text}");
                    }
                }

                await step.LeaveAsync(CancellationToken.None);
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }

        foreach (var english in new[] { AiSetupText.WindowTitle(true), AiSetupText.Back(true), AiSetupText.Next(true), AiSetupText.Finish(true), AiSetupText.Close(true), AiSetupEntryText.OpenButton(true), AiSetupEntryText.BandHint(true), AiSetupEntryText.AskMessage(true), AiSetupEntryText.Later(true) })
        {
            Assert.DoesNotMatch(Hangul(), english);
        }

        Assert.True(problems.Count == 0, "영어 화면에 한글이 남음:\n" + string.Join('\n', problems));
    });

    // 사람이 고친 입력 칸의 값은 보지 않는다(접속 이름 같은 사용자 값은 한글일 수 있다).
    private static IEnumerable<string> VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible).SelectMany(c => c switch
        {
            TextBlock { Text: { Length: > 0 } t } => [t],
            ContentControl { Content: string { Length: > 0 } s } => [s],
            TextBox { PlaceholderText: string { Length: > 0 } w } => [w],
            _ => (IEnumerable<string>)[],
        }).Concat(window.GetVisualDescendants().OfType<Control>().Select(ToolTip.GetTip).OfType<string>());
}
