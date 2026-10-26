using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Shell;
using Oranda.App.ViewModels;

namespace Oranda.App.Features.Update;

/// <summary>릴리스 노트 창에 그릴 버전 하나: 버전 글과 <see cref="ReleaseNotesFormatter"/> 로 바꾼 줄 목록.</summary>
public sealed record ReleaseNoteSection(string Version, IReadOnlyList<NoteLine> Lines);

/// <summary>
/// 도움말 &gt; 새 기능(릴리스 노트) 창의 모델. 실행 중인 버전의 노트를 맨 위에, 포함된 이전 노트를 새 버전부터 잇는다.
/// 실행 중인 버전의 노트가 없으면 맨 위에 한 줄 안내를 두고 포함된 최신 노트가 그 아래 처음 온다.
/// </summary>
public sealed partial class ReleaseNotesViewModel : ViewModelBase
{
    private readonly IShellText _text;

    /// <param name="entries">포함된 노트 전체(순서 무관).</param>
    /// <param name="runningVersion">지금 실행 중인 앱 버전(<c>0.1.2</c> 꼴, 꼬리표는 무시).</param>
    /// <param name="installed">설치본이면 true, 개발 실행본·로컬 빌드면 false.</param>
    /// <param name="releasesUrl">「모든 릴리스 보기」 가 여는 주소.</param>
    public ReleaseNotesViewModel(IShellText text, IReadOnlyList<ReleaseNoteEntry> entries, string runningVersion, bool installed, string releasesUrl)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(entries);
        _text = text;
        ReleasesUrl = releasesUrl;
        var selection = ReleaseNotesCatalog.Select(entries, runningVersion);
        Sections = [.. selection.Ordered.Select(e => new ReleaseNoteSection(e.Version, ReleaseNotesFormatter.Format(e.Markdown)))];
        Message = selection.Current is not null
            ? null
            : installed || entries.Count == 0
                ? text.Lookup("ReleaseNotes.noNotes")
                : string.Format(System.Globalization.CultureInfo.CurrentCulture, text.Lookup("ReleaseNotes.buildNoNote"), runningVersion);
    }

    public string TitleText => _text.Lookup("ReleaseNotes.title");

    public string CloseText => _text.Lookup("Update.btn.close");

    public string AllReleasesText => _text.Lookup("ReleaseNotes.allReleases");

    /// <summary>실행 중인 버전의 노트가 없을 때의 안내 한 줄(있으면 null).</summary>
    public string? Message { get; }

    public bool HasMessage => Message is not null;

    /// <summary>화면 순서대로: 실행 중인 버전(있으면) → 이전 버전을 새 버전부터.</summary>
    public IReadOnlyList<ReleaseNoteSection> Sections { get; }

    public string ReleasesUrl { get; }

    /// <summary>주소를 기본 브라우저로 연다. 파일 열기와 같은 운영체제 호출을 쓰며, 시험이 바꾼다.</summary>
    public Func<string, bool> OpenUrl { get; set; } = FileLauncher.Open;

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void OpenReleases() => OpenUrl(ReleasesUrl);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
