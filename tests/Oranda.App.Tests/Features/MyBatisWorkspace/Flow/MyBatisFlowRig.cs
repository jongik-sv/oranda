using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Oranda.App.Features.MyBatisWorkspace.Editor;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Parameters;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Flow;

/// <summary>
/// TSK-04-01 흐름 시험의 공용 준비물: 앱 조립(ShellHarness)을 띄우고, 거기서 해석한 MyBatis 훅·계산 허브·시나리오 저장소를 그대로 쓴다.
/// 저장 확인 창만 시험용 응답기로 바꾼다(창을 띄우면 시험이 멈춘다). 파일은 모두 스크래치 임시 폴더에 둔다.
/// </summary>
internal sealed class MyBatisFlowRig : IDisposable
{
    /// <summary>사용자 매퍼(사내 파일, 저장소 밖). 없으면 그 시험은 아무것도 하지 않고 끝난다(공개 저장소라 복사하지 않는다).</summary>
    internal const string UserMapperPath = @"C:\Users\jongik.jang\project\oranda\docs\oranda1\CrGalExeRstMapper.xml";

    private readonly ShellHarness _harness;

    private MyBatisFlowRig(string root)
    {
        Root = root;
        Directory.CreateDirectory(Root);
        _harness = new ShellHarness();
        Host = SqlEditorHost.Current;
        Hooks = new MyBatisHooks(_harness.Services);
        Host.MyBatisHooks = Hooks;
        Host.Dialogs = Dialogs;
        Save = (MyBatisSaveHandler)Hooks.SaveHandler!;
        Save.Presenter = Presenter;
        Save.Watch.Attach(Host); // 시작 작업이 아직 안 돌았어도 감시가 이 호스트의 작업 공간을 따르게
        Hub = _harness.Get<MyBatisEvalHub>();
    }

    public string Root { get; }

    public ShellHarness Harness => _harness;

    public SqlEditorHost Host { get; }

    public MyBatisHooks Hooks { get; }

    public MyBatisSaveHandler Save { get; }

    public MyBatisEvalHub Hub { get; }

    public RecordingPresenter Presenter { get; } = new();

    public FlowDialogs Dialogs { get; } = new();

    public static MyBatisFlowRig Create() => new(Path.Combine(Path.GetTempPath(), "oranda-mbflow-" + Guid.NewGuid().ToString("N")));

    public T Get<T>()
        where T : class => _harness.Get<T>();

    /// <summary>임시 폴더에 매퍼 파일을 쓴다(UTF-8, BOM 없음).</summary>
    public string Write(string name, string text, string? subFolder = null)
    {
        var dir = subFolder is null ? Root : Path.Combine(Root, subFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>임시 폴더에 원본 바이트 그대로 복사한다(사용자 매퍼 → 복사본).</summary>
    public string CopyIn(string source, string name, string? subFolder = null)
    {
        var dir = subFolder is null ? Root : Path.Combine(Root, subFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.Copy(source, path, overwrite: true);
        File.SetAttributes(path, FileAttributes.Normal);
        return path;
    }

    public async Task<(SqlWorkspaceViewModel Workspace, MyBatisWorkspaceSession Session)> OpenAsync(string path)
    {
        var file = await Host.OpenWorkspaceFileAsync(path, default);
        Assert.NotNull(file);
        var ws = Host.ActiveWorkspace!;
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(ws.SourcePath!));
        return (ws, Assert.IsType<MyBatisWorkspaceSession>(ws.MyBatis));
    }

    /// <summary>탭의 문장 편집기(분할 포함)를 창에 올린다. 훅의 편집기 공장이 서비스(계산 허브·설정)를 넘겨 만든다.</summary>
    public (MyBatisStatementEditorView View, Window Window) ShowEditor(SqlEditorViewModel tab, MyBatisWorkspaceSession session, double width = 1200, double height = 640)
    {
        var view = Assert.IsType<MyBatisStatementEditorView>(Hooks.EditorViewFactory!.CreateEditor(tab, session));
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump();
        window.UpdateLayout();
        return (view, window);
    }

    public static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// 라이트·다크 PNG 를 <c>ORANDA_RENDER_DIR</c> 아래에 쓴다(환경 변수가 없으면 아무것도 안 한다).
    /// 픽셀을 그리려면 Headless 가 Skia 로 돌아야 하므로 같은 환경 변수가 있을 때만 켠다(HeadlessAppBuilder).
    /// </summary>
    public static void Render(Window window, string name, int width, int height)
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
                Pump();
                window.UpdateLayout();
                using var bmp = new RenderTargetBitmap(new PixelSize(width, height));
                bmp.Render(window);
#pragma warning disable CS0618 // 렌더 확인용: 새 오버로드가 없는 헤드리스 비트맵
                bmp.Save(Path.Combine(dir, $"{name}-{tone}.png"));
#pragma warning restore CS0618
            }
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
            Pump();
        }
    }

    /// <summary>컨트롤 하나를 창에 올려 라이트·다크 PNG 로 쓴다(<c>ORANDA_RENDER_DIR</c> 가 없으면 아무것도 안 한다).</summary>
    public static void RenderControl(Control content, string name, int width, int height)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORANDA_RENDER_DIR")))
        {
            return;
        }

        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        Pump();
        window.UpdateLayout();
        Render(window, name, width, height);
        window.Close();
    }

    /// <summary>두 파일을 <c>git diff --no-index -U0</c> 로 견줘 바뀐 줄 범위(옛 파일 기준, 1부터)와 줄 수를 돌려준다.</summary>
    public static GitDiffSummary GitDiff(string before, string after)
    {
        var psi = new ProcessStartInfo("git", ["diff", "--no-index", "--no-color", "-U0", "--", before, after])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        var hunks = new List<(int Start, int Count)>();
        int added = 0, removed = 0;
        foreach (var line in output.Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                // @@ -a,b +c,d @@
                var minus = line.Split(' ')[1][1..].Split(',');
                var start = int.Parse(minus[0], System.Globalization.CultureInfo.InvariantCulture);
                var count = minus.Length > 1 ? int.Parse(minus[1], System.Globalization.CultureInfo.InvariantCulture) : 1;
                hunks.Add((start, count));
            }
            else if (line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                added++;
            }
            else if (line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal))
            {
                removed++;
            }
        }

        return new GitDiffSummary(hunks, added, removed, output);
    }

    public void Dispose()
    {
        _harness.Dispose();
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                }

                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>1부터 세는 줄 번호 <paramref name="line"/> 의 문자 위치가 속한 줄 번호 범위 확인용: 문자 위치 → 줄 번호.</summary>
    public static int LineOf(string text, int offset) => 1 + text.Take(offset).Count(c => c == '\n');
}

internal sealed record GitDiffSummary(IReadOnlyList<(int Start, int Count)> Hunks, int Added, int Removed, string Raw);

/// <summary>「바뀌는 부분」 창 대신: 받은 요청을 적어 두고 정해진 답(기본 = 전부 저장)을 돌려준다.</summary>
internal sealed class RecordingPresenter : ISaveDiffPresenter
{
    public List<SaveDiffRequest> Requests { get; } = [];

    /// <summary>null 을 돌려주면 사용자가 취소한 것이다.</summary>
    public Func<SaveDiffRequest, SaveDiffResult?> Answer { get; set; } = r => new SaveDiffResult(r.Items, r.ShowEveryTime);

    public Task<SaveDiffResult?> ShowAsync(SaveDiffRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(Answer(request));
    }
}

/// <summary>대화 상자 대신: 알림 글을 적어 두고, 목록 고르기는 정해진 답을 돌려준다.</summary>
internal sealed class FlowDialogs : IEditorDialogs
{
    public List<string> Messages { get; } = [];

    /// <summary>목록에서 고를 위치(음수 = 취소).</summary>
    public int Pick { get; set; }

    public Task<string?> PickOpenFileAsync(CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PickSaveFileAsync(string suggestedName, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> PromptAsync(string title, string initial, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<string?> ChooseAsync(string title, IReadOnlyList<string> items, CancellationToken ct) =>
        Task.FromResult<string?>(Pick < 0 ? null : items[Pick]);

    public Task<bool> ConfirmAsync(string message, CancellationToken ct) => Task.FromResult(false);

    public Task<bool> PreviewEditAsync(string title, string before, string after, CancellationToken ct) => Task.FromResult(false);

    public Task<SaveChoice> AskSaveChangesAsync(string tabName, CancellationToken ct) => Task.FromResult(SaveChoice.Cancel);

    public Task ShowMessageAsync(string title, string message, CancellationToken ct)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }
}
