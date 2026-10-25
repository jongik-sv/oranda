using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;

namespace Oranda.App.Tests.Features.Localization;

/// <summary>
/// TSK-09-11: 상태 표시줄·접속 콤보의 글자가 언어를 따른다. 영어 화면에 한글이 남지 않고, 한국어로 돌아오면 원래 글자이며,
/// 코드에 한글 문구가 다시 박히지 않는다. <c>ORANDA_RENDER_DIR</c> 이 있으면 영어 라이트·다크 PNG 를 남긴다.
/// </summary>
[Collection("UiLanguage")]
public partial class StatusBarLanguageTests
{
    [GeneratedRegex("[\\uAC00-\\uD7A3]")]
    private static partial Regex Hangul();

    private static async Task SetLanguageAsync(ShellHarness h, string code)
    {
        await h.Get<SettingsService>().UpdateAsync(s => s with { Ui = s.Ui with { Language = code } });
        await HeadlessHost.WaitUntil(() => h.Get<IShellText>().Language == code);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>상태 표시줄의 모든 글자·도움말·낭독 이름과 접속 콤보의 빈 글·도움말을 모은다.</summary>
    private static List<string> VisibleStrings(ShellHarness h)
    {
        var all = new List<string>();
        var root = h.Window.FindControl<ItemsControl>("StatusBar")!;
        foreach (var c in root.GetVisualDescendants().OfType<Control>().Prepend(root))
        {
            if (c is TextBlock { Text: { Length: > 0 } t })
            {
                all.Add(t);
            }

            if (ToolTip.GetTip(c) is string tip)
            {
                all.Add(tip);
            }

            var name = Avalonia.Automation.AutomationProperties.GetName(c);
            if (!string.IsNullOrEmpty(name))
            {
                all.Add(name);
            }
        }

        foreach (var combo in h.Window.GetVisualDescendants().OfType<ConnectionComboView>().Select(v => v.Combo))
        {
            all.Add(combo.PlaceholderText ?? string.Empty);
            all.Add(ToolTip.GetTip(combo) as string ?? string.Empty);
            all.Add(Avalonia.Automation.AutomationProperties.GetName(combo) ?? string.Empty);
        }

        return all;
    }

    [Fact]
    public Task English_status_bar_and_connection_combo_have_no_korean_and_switch_back_to_korean() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        var host = SqlEditorHost.Current;
        host.NewDocument("select 1\nfrom dual");
        Dispatcher.UIThread.RunJobs();

        var ko = VisibleStrings(h);
        Assert.Contains("접속 안 됨", ko);
        Assert.Contains("줄 1, 열 1", ko);

        await SetLanguageAsync(h, "en");
        var en = VisibleStrings(h);
        Assert.Empty(en.Where(s => Hangul().IsMatch(s)));
        Assert.Contains("Not connected", en);
        Assert.Contains("Ln 1, Col 1", en);
        Assert.Contains("Open Connection Manager", en);
        Shots(h);

        // 선택도 영어.
        host.Active!.Select(9, 3);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Ln 2, Col 4 (3 selected)", VisibleStrings(h));

        await SetLanguageAsync(h, "ko");
        var back = VisibleStrings(h);
        Assert.Contains("접속 안 됨", back);
        Assert.Contains("줄 2, 열 4 (선택 3자)", back);
        Assert.DoesNotContain(back, s => s.Contains("Not connected", StringComparison.Ordinal));
    });

    [Fact]
    public Task English_run_summary_and_environment_chip_texts_have_no_korean() => HeadlessHost.Run(async () =>
    {
        using var h = new ShellHarness();
        await SetLanguageAsync(h, "en");
        try
        {
            Assert.Equal("Production", ConnectionStatusViewModel.EnvironmentTextOf(ConnectionEnvironment.Production));
            Assert.Equal("Test", ConnectionStatusViewModel.EnvironmentTextOf(ConnectionEnvironment.Test));
            Assert.Equal("Development", ConnectionStatusViewModel.EnvironmentTextOf(ConnectionEnvironment.Development));
            Assert.Equal(string.Empty, ConnectionStatusViewModel.EnvironmentTextOf(null));

            var ok = new ExecutionResult(new SqlStatement("select 1", default, StatementKind.Query), ExecutionStatus.Succeeded)
            {
                RowsAffected = 1234,
                Elapsed = TimeSpan.FromSeconds(1.5),
            };
            Assert.Equal("1,234 rows · 1.5 s", ExecutionState.DescribeRun(ok));
            Assert.Equal("Done · 1.5 s", ExecutionState.DescribeRun(ok with { RowsAffected = null }));
            Assert.Equal("Canceled", ExecutionState.DescribeRun(ok with { Status = ExecutionStatus.Cancelled }));
            Assert.Equal("Error · 1.5 s", ExecutionState.DescribeRun(ok with { Status = ExecutionStatus.Failed }));
            Assert.Equal("ORA-00942 · 1.5 s", ExecutionState.DescribeRun(ok with { Status = ExecutionStatus.Failed, Error = new DbError("ORA-00942", "x") { Number = 942 } }));
            Assert.Equal("Rejected", ExecutionState.DescribeRun(ok with { Status = ExecutionStatus.Denied }));
        }
        finally
        {
            await SetLanguageAsync(h, "ko");
        }

        Assert.Equal("운영", ConnectionStatusViewModel.EnvironmentTextOf(ConnectionEnvironment.Production));
    });

    private static readonly string[] StatusSourceFiles =
    [
        "Features/ConnectionManager/ConnectionStatusItem.cs",
        "Features/ConnectionManager/ConnectionCombo.cs",
        "Features/SqlEditor/EditorStatusViewModel.cs",
        "Features/Execution/ExecutionStatusItems.cs",
        "Features/Execution/ExecutionState.cs",
    ];

    [Fact]
    public void Status_bar_sources_keep_no_korean_text_outside_the_UiText_fallbacks()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var rel in StatusSourceFiles)
        {
            var lines = File.ReadAllLines(Path.Combine(root, "src", "Oranda.App", rel.Replace('/', Path.DirectorySeparatorChar)));
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                var comment = line.IndexOf(" // ", StringComparison.Ordinal);
                if (comment >= 0)
                {
                    line = line[..comment]; // 줄 끝 주석은 제외
                }

                if (line.StartsWith("//", StringComparison.Ordinal) || !Hangul().IsMatch(line))
                {
                    continue;
                }

                // 한글 문구는 UiText 호출의 대체 글자로만 둔다(주석은 제외).
                if (line.Contains('"', StringComparison.Ordinal) && !line.Contains("UiText.", StringComparison.Ordinal))
                {
                    offenders.Add($"{rel}:{i + 1}: {line}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static void Shots(ShellHarness h)
    {
        var dir = Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        Directory.CreateDirectory(dir);
        try
        {
            foreach (var (variant, tone) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                h.Window.UpdateLayout();
                var size = new PixelSize((int)Math.Ceiling(h.Window.Bounds.Width), (int)Math.Ceiling(h.Window.Bounds.Height));
                using var bmp = new RenderTargetBitmap(size);
                bmp.Render(h.Window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"statusbar-en-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.GetFiles(dir.FullName, "Oranda.sln*").Length > 0)
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("저장소 루트를 찾지 못했다.");
    }
}
