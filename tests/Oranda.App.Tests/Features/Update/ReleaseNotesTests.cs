using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.Localization;
using Oranda.App.Features.Update;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.Update;

/// <summary>
/// 도움말 &gt; 새 기능(릴리스 노트): 명령 위치, 포함된 노트 수, 실행 중인 버전이 맨 위, 노트 없는 버전의 문구, 「모든 릴리스 보기」 주소.
/// <c>ORANDA_RENDER_DIR</c> 가 있으면 ko/en·라이트/다크 PNG(<c>release-notes-*.png</c>)를 그 폴더에 쓴다.
/// </summary>
[Collection("UiLanguage")]
public sealed class ReleaseNotesTests
{
    private static readonly ReleaseNoteEntry[] Sample =
    [
        new("0.1.1", "# Oranda 0.1.1\n\n- 첫째"),
        new("0.1.10", "# Oranda 0.1.10\n\n- 열번째"),
        new("0.1.2", "# Oranda 0.1.2\n\n- 둘째"),
    ];

    private static ShellText Text(string language)
    {
        var text = new ShellText(language: language);
        UiText.Current = text;
        return text;
    }

    private static ReleaseNotesWindow Show(ReleaseNotesViewModel vm)
    {
        var window = new ReleaseNotesWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    private static T Named<T>(Control root, string name)
        where T : Control => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static IEnumerable<string> SectionVersions(ReleaseNotesWindow window) =>
        Named<StackPanel>(window, "SectionsPanel").Children.OfType<SelectableTextBlock>().Select(b => b.Name!["Notes_".Length..]);

    // ---- 등록 ----

    [Fact]
    public void Release_notes_command_is_in_the_help_menu_right_above_check_update_and_in_the_palette()
    {
        var commands = ShellTestHelpers.AppContributions.Commands;
        var notes = commands.Single(c => c.Definition.Id == "oranda.help.releaseNotes").Definition;
        var check = commands.Single(c => c.Definition.Id == "oranda.help.checkUpdate").Definition;

        Assert.Equal(CommandCategory.Help, notes.Category);
        Assert.Equal(["menu:help/releaseNotes@880"], notes.Placements);
        Assert.True(notes.ShowInPalette);
        Assert.Null(notes.When);
        Assert.Equal("새 기능(릴리스 노트)", new ShellText(language: "ko").Lookup("Cmd.oranda.help.releaseNotes"));
        Assert.Equal("What's New (Release Notes)", new ShellText(language: "en").Lookup("Cmd.oranda.help.releaseNotes"));
        Assert.True(Order(notes) < Order(check));
        Assert.Equal(Order(check) / 100, Order(notes) / 100); // 같은 묶음: 사이에 구분선이 끼지 않는다

        static int Order(CommandDefinition c) => int.Parse(c.Placements[0][(c.Placements[0].IndexOf('@', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Embedded_notes_match_the_files_in_docs_release_notes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Oranda.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var files = Directory.GetFiles(Path.Combine(dir.FullName, "docs", "release-notes"), "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var embedded = ReleaseNotesCatalog.Load();

        Assert.NotEmpty(files);
        Assert.Equal(files.Length, embedded.Count);
        Assert.Equal(files, embedded.Select(e => e.Version).Order(StringComparer.Ordinal));
        Assert.All(embedded, e => Assert.False(string.IsNullOrWhiteSpace(e.Markdown)));
    }

    // ---- 순서·문구 ----

    [Fact]
    public void Running_version_comes_first_then_older_notes_newest_first()
    {
        var selection = ReleaseNotesCatalog.Select(Sample, "0.1.10-beta+abc");

        Assert.Equal("0.1.10", selection.Current?.Version);
        Assert.Equal(["0.1.10", "0.1.2", "0.1.1"], selection.Ordered.Select(e => e.Version));
    }

    [Fact]
    public void Newer_notes_than_the_running_version_are_not_listed()
    {
        var selection = ReleaseNotesCatalog.Select(Sample, "0.1.2");

        Assert.Equal(["0.1.2", "0.1.1"], selection.Ordered.Select(e => e.Version));
    }

    [Fact]
    public Task Window_puts_the_running_version_notes_on_top_without_a_message() => HeadlessHost.Run(() =>
    {
        var vm = new ReleaseNotesViewModel(Text("ko"), Sample, "0.1.2", installed: true, ReleaseNotesCatalog.ReleasesUrl(null));
        var window = Show(vm);
        try
        {
            Assert.Equal("새 기능(릴리스 노트)", window.Title);
            Assert.Equal(["0.1.2", "0.1.1"], SectionVersions(window));
            Assert.False(Named<TextBlock>(window, "MessageText").IsVisible);
            var top = Named<SelectableTextBlock>(window, "Notes_0.1.2");
            Assert.Equal(["Oranda 0.1.2", string.Empty, "• 둘째"], top.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Window_says_there_are_no_notes_for_an_installed_version_that_has_none() => HeadlessHost.Run(() =>
    {
        var vm = new ReleaseNotesViewModel(Text("ko"), Sample, "9.9.9", installed: true, ReleaseNotesCatalog.ReleasesUrl(null));
        var window = Show(vm);
        try
        {
            var message = Named<TextBlock>(window, "MessageText");
            Assert.True(message.IsVisible);
            Assert.Equal("이 버전의 릴리스 노트가 없습니다", message.Text);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Window_for_a_local_build_shows_the_latest_note_on_top_with_a_one_line_notice() => HeadlessHost.Run(() =>
    {
        var vm = new ReleaseNotesViewModel(Text("ko"), Sample, "0.0.0", installed: false, ReleaseNotesCatalog.ReleasesUrl(null));
        var window = Show(vm);
        try
        {
            Assert.Equal("이 빌드 버전 0.0.0 의 노트는 없습니다", Named<TextBlock>(window, "MessageText").Text);
            Assert.Equal(["0.1.10", "0.1.2", "0.1.1"], SectionVersions(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Window_without_any_embedded_note_shows_only_the_no_notes_message() => HeadlessHost.Run(() =>
    {
        var vm = new ReleaseNotesViewModel(Text("ko"), [], "0.1.2", installed: false, ReleaseNotesCatalog.ReleasesUrl(null));
        var window = Show(vm);
        try
        {
            Assert.Equal("이 버전의 릴리스 노트가 없습니다", Named<TextBlock>(window, "MessageText").Text);
            Assert.Empty(SectionVersions(window));
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 모든 릴리스 보기 ----

    [Theory]
    [InlineData(UpdateFeedKind.GitHub, "https://github.com/acme/tool", "https://github.com/acme/tool/releases")]
    [InlineData(UpdateFeedKind.Web, "https://example.com/feed", "https://github.com/jongik-sv/oranda/releases")]
    [InlineData(UpdateFeedKind.Folder, "/srv/releases", "https://github.com/jongik-sv/oranda/releases")]
    public void Releases_url_follows_a_github_feed_and_falls_back_to_the_repository(UpdateFeedKind kind, string location, string expected) =>
        Assert.Equal(expected, ReleaseNotesCatalog.ReleasesUrl(new UpdateFeed(kind, location)));

    [Fact]
    public Task All_releases_link_opens_the_releases_page_in_the_browser() => HeadlessHost.Run(() =>
    {
        var opened = new List<string>();
        var vm = new ReleaseNotesViewModel(Text("ko"), Sample, "0.1.2", installed: true, ReleaseNotesCatalog.ReleasesUrl(null)) { OpenUrl = url => { opened.Add(url); return true; } };
        var window = Show(vm);
        try
        {
            var link = Named<HyperlinkButton>(window, "AllReleasesLink");
            Assert.Equal("모든 릴리스 보기", link.Content);
            link.Command!.Execute(null);

            Assert.Equal(["https://github.com/jongik-sv/oranda/releases"], opened);
        }
        finally
        {
            window.Close();
        }
    });

    // ---- 렌더 ----

    [Theory]
    [InlineData("ko")]
    [InlineData("en")]
    public Task Renders_light_and_dark_png_when_a_render_dir_is_given(string lang) => HeadlessHost.Run(() =>
    {
        var vm = new ReleaseNotesViewModel(Text(lang), ReleaseNotesCatalog.Load(), "0.1.2", installed: true, ReleaseNotesCatalog.ReleasesUrl(null));
        var window = Show(vm);
        try
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
                    window.UpdateLayout();
                    var size = new PixelSize(Math.Max(1, (int)window.Bounds.Width), Math.Max(1, (int)window.Bounds.Height));
                    using var bmp = new RenderTargetBitmap(size);
                    bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                    bmp.Save(Path.Combine(dir, $"release-notes-{lang}-{tone}.png"));
#pragma warning restore CS0618
                }
            }
            finally
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            window.Close();
        }
    });
}
