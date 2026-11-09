using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.Core.Connection;
using Oranda.Core.Settings;
using Oranda.Core.Shell;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 창 「드라이버」 탭의 모델(Tibero 드라이버 지정 DU2, 결정25): 「Tibero 드라이버(TDP.NET Core) 위치」 칸·[찾아보기…]·[확인]·[지우기]·상태 줄·안내.
/// [확인] 은 <see cref="IDbmsDriverStatus.Check"/> 로 칸의 경로만 사전 검사한다(올리지 않음). 칸의 값은 설정 창의 「저장」이 <c>drivers.tiberoPath</c> 로 쓰고,
/// [지우기] 는 칸을 비우고 바로 저장한다. 이 탭은 Tibero 가 접속 대화상자에 보이든 아니든 항상 있다.
/// 로더(<see cref="IDbmsDriverStatus"/> 구현)가 없으면 [확인] 은 흐리고 상태 줄이 이유(이 빌드에 Tibero 없음 / 로더 없음)를 말한다.
/// </summary>
public sealed partial class DriverSettingsPage : ObservableObject
{

    private readonly SettingsService _settings;
    private readonly IShellText _text;
    private readonly IDbmsDriverStatus? _status;
    private readonly bool _tiberoIncluded;
    private string? _saved;
    private DriverCheck? _last;

    /// <param name="settings">저장소(지우기가 바로 쓴다).</param>
    /// <param name="text">화면 글자 조회.</param>
    /// <param name="status">Tibero 드라이버 상태(로더). null = 구현 없음.</param>
    /// <param name="tiberoIncluded">이 빌드에 Tibero 공급자 어셈블리가 있나(없으면 상태 줄이 「이 빌드에는 Tibero 가 포함되어 있지 않음」).</param>
    public DriverSettingsPage(SettingsService settings, IShellText text, IDbmsDriverStatus? status, bool tiberoIncluded = true)
    {
        _settings = settings;
        _text = text;
        _status = status;
        _tiberoIncluded = tiberoIncluded;
        _saved = DriverPathSource.Normalize(settings.Current.Drivers.TiberoPath);
        _path = _saved ?? string.Empty;
        if (status is not null)
        {
            _last = Guard(() => status.Current);
        }

        Recompute();
    }

    /// <summary>칸의 경로(dll·폴더·nupkg).</summary>
    [ObservableProperty]
    private string _path;

    /// <summary>상태 줄(결과별 한 줄. 다시 시작해야 적용되면 그 문구가 덧붙는다).</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>상태 줄 아래 보조 문장(<see cref="DriverCheck.Reason"/>). 없으면 빈 글자.</summary>
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    [ObservableProperty]
    private string _statusDetail = string.Empty;

    /// <summary>쓸 수 없는 상태(또는 로더 없음)라 오류 색으로 보인다.</summary>
    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatusDetail => StatusDetail.Length > 0;

    /// <summary>로더가 있어 [확인] 을 누를 수 있다.</summary>
    public bool CanCheck => _status is not null;

    /// <summary>칸을 바꿨는데 아직 저장하지 않았다.</summary>
    public bool IsDirty => DriverPathSource.Normalize(Path) != _saved;

    /// <summary>찾아보기(파일: dll·nupkg)가 부르는 선택 길. 화면이 넣는다(시험은 가짜). 취소하면 null.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    /// <summary>찾아보기(폴더)가 부르는 선택 길. 화면이 넣는다.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    // ---- 글자 ----

    public string TabTitle => _text.Lookup("Shell.settings.tab.drivers");

    public string Title => L("Shell.settings.drivers.title");

    public string PathName => L("Shell.settings.drivers.pathName");

    public string Placeholder => L("Shell.settings.drivers.placeholder");

    public string BrowseText => L("Shell.settings.drivers.browse");

    public string BrowseFolderText => L("Shell.settings.drivers.browseFolder");

    public string CheckText => L("Shell.settings.drivers.check");

    public string ClearText => L("Shell.settings.drivers.clear");

    public string NoticeText => L("Shell.settings.drivers.notice");

    public string FileDialogTitle => L("Shell.settings.drivers.fileDialogTitle");

    public string FolderDialogTitle => L("Shell.settings.drivers.folderDialogTitle");

    public string FileTypeName => L("Shell.settings.drivers.fileTypeName");

    private string L(string key) => _text.Lookup(key);

    // ---- 동작 ----

    /// <summary>[확인]: 칸의 경로를 사전 검사하고 결과를 상태 줄에 보인다.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private void Check()
    {
        if (_status is null)
        {
            return;
        }

        var path = DriverPathSource.Normalize(Path);
        _last = Guard(() => _status.Check(path));
        Recompute();
    }

    /// <summary>[찾아보기…]: 파일(dll·nupkg)을 골라 칸에 넣고 검사한다.</summary>
    [RelayCommand]
    private Task BrowseFileAsync() => BrowseAsync(PickFile);

    /// <summary>[폴더…]: 폴더를 골라 칸에 넣고 검사한다.</summary>
    [RelayCommand]
    private Task BrowseFolderAsync() => BrowseAsync(PickFolder);

    private async Task BrowseAsync(Func<Task<string?>>? pick)
    {
        if (pick is null || await pick().ConfigureAwait(true) is not { Length: > 0 } picked)
        {
            return;
        }

        Path = picked;
        Check();
    }

    /// <summary>[지우기]: 칸을 비우고 설정에도 바로 쓴다(저장소가 읽기 전용이면 칸만 비운다).</summary>
    [RelayCommand]
    private async Task ClearAsync()
    {
        Path = string.Empty;
        if (!_settings.IsReadOnly)
        {
            try
            {
                await _settings.UpdateAsync(s => s with { Drivers = s.Drivers with { TiberoPath = null } }).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                _last = new DriverCheck(DriverState.NotFound, Reason: ex.Message);
                Recompute();
                return;
            }
        }

        _saved = DriverPathSource.Normalize(_settings.Current.Drivers.TiberoPath);
        if (_status is not null)
        {
            _last = Guard(() => _status.Check(null));
        }

        Recompute();
    }

    /// <summary>설정 창 「저장」이 쓸 값: 칸을 바꿨으면 <c>TiberoPath</c> 만 바꾼 사본, 아니면 그대로(다른 곳에서 바뀐 값을 덮지 않는다).</summary>
    public DriverSettings ToSettings(DriverSettings current) =>
        IsDirty ? current with { TiberoPath = DriverPathSource.Normalize(Path) } : current;

    /// <summary>저장 직후에 부른다: 바뀜 표시를 지우고 「다시 시작하면 적용」 상태를 다시 읽는다.</summary>
    public void AcceptSaved()
    {
        _saved = DriverPathSource.Normalize(Path);
        OnPropertyChanged(nameof(IsDirty));
        Recompute();
    }

    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(IsDirty));

    private static DriverCheck? Guard(Func<DriverCheck> check)
    {
        try
        {
            return check();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 로더가 검사 중 예외를 내도 설정 창은 열려 있어야 한다: 이유만 보인다.
            return new DriverCheck(DriverState.NotFound, Reason: ex.Message);
        }
    }

    // ---- 상태 줄 ----

    private void Recompute()
    {
        if (_status is null)
        {
            StatusText = L(_tiberoIncluded ? "Shell.settings.drivers.state.noLoader" : "Shell.settings.drivers.state.noTibero");
            StatusDetail = string.Empty;
            StatusIsError = true;
            return;
        }

        if (_last is not { } check)
        {
            StatusText = string.Empty;
            StatusDetail = string.Empty;
            StatusIsError = false;
            return;
        }

        var main = Describe(check);
        if (_status.RestartRequired)
        {
            main = string.Format(CultureInfo.CurrentCulture, L("Shell.settings.drivers.state.restart"), main);
        }

        StatusText = main;
        // 「쓸 수 없음({Reason})」 은 이유가 본문에 들어 있다: 보조 문장으로 되풀이하지 않는다.
        StatusDetail = check.State == DriverState.Unusable ? string.Empty : check.Reason ?? string.Empty;
        StatusIsError = check.State is not (DriverState.Ok or DriverState.Unverified or DriverState.Missing);
    }

    /// <summary>검사 결과 한 줄(다시 시작 안내와 보조 문장 제외). 시험이 문구를 직접 확인한다.</summary>
    internal string Describe(DriverCheck check) => check.State switch
    {
        DriverState.Ok => string.Format(CultureInfo.CurrentCulture, L("Shell.settings.drivers.state.ok"), check.Version),
        DriverState.Unverified => string.Format(CultureInfo.CurrentCulture, L("Shell.settings.drivers.state.unverified"), check.Version),
        DriverState.FrameworkBuild => L("Shell.settings.drivers.state.framework"),
        DriverState.NotFound => L("Shell.settings.drivers.state.notFound"),
        DriverState.WrongAssembly => L("Shell.settings.drivers.state.wrongAssembly"),
        DriverState.Unusable => string.Format(CultureInfo.CurrentCulture, L("Shell.settings.drivers.state.unusable"), check.Reason),
        _ => L("Shell.settings.drivers.state.missing"),
    };
}
