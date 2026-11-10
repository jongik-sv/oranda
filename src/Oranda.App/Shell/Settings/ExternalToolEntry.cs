using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Oranda.App.Features.Localization;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 설정 「드라이버」 탭 「외부 적재 도구」 묶음의 도구 한 줄(XL1·XL3): 경로 칸(비우면 자동으로 찾기, 찾은 위치는 워터마크)·[찾아보기…]·[도구 확인]·결과 줄·「설치 방법」.
/// [도구 확인] 은 <see cref="IExternalToolProbe.CheckAsync"/> 로 칸의 경로(비었으면 자동으로 찾은 도구)를 실행해 버전 줄을 읽는다.
/// 칸의 값은 설정 창의 「저장」이 <c>drivers.sqlLoaderPath</c>·<c>drivers.tbLoaderPath</c> 로 쓴다(<see cref="DriverSettingsPage.ToSettings"/>).
/// 화면 글은 한국어로 만들고 영어 화면이면 문구 표(<c>literals-en.json</c>)의 영어를 쓴다.
/// </summary>
public sealed partial class ExternalToolEntry : ObservableObject
{
    private readonly IExternalToolProbe? _probe;
    private readonly IShellText _text;
    private string? _saved;

    /// <param name="kind">도구 종류.</param>
    /// <param name="savedPath">설정에 저장된 경로(null = 자동으로 찾기).</param>
    /// <param name="probe">도구 찾기·확인. null = 서비스 없음([도구 확인] 이 흐리다).</param>
    /// <param name="text">화면 언어.</param>
    public ExternalToolEntry(ExternalLoaderKind kind, string? savedPath, IExternalToolProbe? probe, IShellText text)
    {
        Kind = kind;
        _probe = probe;
        _text = text;
        _saved = DriverPathSource.Normalize(savedPath);
        _path = _saved ?? string.Empty;
        _placeholder = T(AutoPlain);
        GuideSteps = (kind == ExternalLoaderKind.SqlLoader ? SqlLoaderGuide : TbLoaderGuide).Select(T).ToArray();
        if (probe is null)
        {
            _statusText = T(NoProbe);
            _statusIsError = true;
        }
        else
        {
            RefreshPlaceholder();
        }
    }

    public ExternalLoaderKind Kind { get; }

    /// <summary>칸의 경로(실행 파일 또는 폴더). 비우면 자동으로 찾는다.</summary>
    [ObservableProperty]
    private string _path;

    /// <summary>칸이 비었을 때 보이는 안내: 「비워 두면 자동으로 찾기」와 지금 찾은 위치.</summary>
    [ObservableProperty]
    private string _placeholder;

    /// <summary>[도구 확인] 결과 줄. 확인 전이면 빈 글자.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>결과 줄 아래 보조 문장(버전 줄 또는 살펴본 곳). 없으면 빈 글자.</summary>
    [NotifyPropertyChangedFor(nameof(HasStatusDetail))]
    [ObservableProperty]
    private string _statusDetail = string.Empty;

    /// <summary>쓸 수 없는 결과(또는 서비스 없음)라 오류 색으로 보인다.</summary>
    [ObservableProperty]
    private bool _statusIsError;

    public bool HasStatusDetail => StatusDetail.Length > 0;

    /// <summary>도구 확인 서비스가 있어 [도구 확인] 을 누를 수 있다.</summary>
    public bool CanCheck => _probe is not null;

    /// <summary>저장할 값(앞뒤 공백을 걷어 내고 빈 값은 null = 자동으로 찾기).</summary>
    public string? NormalizedPath => DriverPathSource.Normalize(Path);

    /// <summary>칸을 바꿨는데 아직 저장하지 않았다.</summary>
    public bool IsDirty => NormalizedPath != _saved;

    /// <summary>[찾아보기…] 가 부르는 파일 선택 길. 화면이 넣는다(시험은 가짜). 취소하면 null.</summary>
    public Func<Task<string?>>? PickFile { get; set; }

    /// <summary>도구 실행 파일 이름(확장자 없음). 파일 선택창의 거르기에 쓴다.</summary>
    public string ExecutableName => Kind == ExternalLoaderKind.SqlLoader ? "sqlldr" : "tbloader";

    // ---- 글자 ----

    public string Title => T(Kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader (Oracle 대상)" : "tbLoader (Tibero 대상, 실험)");

    public string PathName => T(Kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader 위치" : "tbLoader 위치");

    public string BrowseText => T("찾아보기…");

    public string CheckText => T("도구 확인");

    public string GuideHeader => T("설치 방법");

    public string FileDialogTitle => T(Kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader 실행 파일 고르기" : "tbLoader 실행 파일 고르기");

    public string FileTypeName => T(Kind == ExternalLoaderKind.SqlLoader ? "SQL*Loader 실행 파일" : "tbLoader 실행 파일");

    /// <summary>「설치 방법」 안의 단계별 안내(번호가 붙은 글).</summary>
    public IReadOnlyList<string> GuideSteps { get; }

    // ---- 동작 ----

    /// <summary>[도구 확인]: 칸의 경로(비었으면 자동으로 찾은 도구)를 실행해 버전 줄을 읽고 결과 줄에 보인다.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync(CancellationToken ct)
    {
        if (_probe is null)
        {
            return;
        }

        StatusText = T(Checking);
        StatusDetail = string.Empty;
        StatusIsError = false;
        ExternalToolInfo info;
        try
        {
            info = await _probe.CheckAsync(Kind, NormalizedPath, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Empty;
            return;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 확인 서비스가 예외를 내도 설정 창은 열려 있어야 한다: 이유만 보인다.
            StatusText = F(CheckFailed, ex.Message);
            StatusIsError = true;
            return;
        }

        Show(info);
        RefreshPlaceholder();
    }

    /// <summary>[찾아보기…]: 실행 파일을 골라 칸에 넣고 확인한다.</summary>
    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (PickFile is null || await PickFile().ConfigureAwait(true) is not { Length: > 0 } picked)
        {
            return;
        }

        Path = picked;
        if (CheckCommand.CanExecute(null))
        {
            await CheckCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }

    /// <summary>저장 직후에 부른다: 바뀜 표시를 지운다.</summary>
    internal void AcceptSaved()
    {
        _saved = NormalizedPath;
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnPathChanged(string value) => OnPropertyChanged(nameof(IsDirty));

    /// <summary>확인 결과 → 결과 줄. 시험이 문구를 직접 확인한다.</summary>
    internal void Show(ExternalToolInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.Usable)
        {
            StatusText = F(Usable, info.Path!);
            StatusDetail = info.VersionLine?.Trim() ?? string.Empty;
            StatusIsError = false;
            return;
        }

        StatusText = info.Problem is { Length: > 0 } problem ? T(problem) : T(NotFound);
        StatusDetail = info.Path is null ? Searched() : string.Empty;
        StatusIsError = true;
    }

    /// <summary>칸이 비었을 때 자동으로 찾을 도구 위치를 워터마크에 보인다(프로세스를 띄우지 않는다).</summary>
    private void RefreshPlaceholder()
    {
        if (_probe is null)
        {
            return;
        }

        try
        {
            var found = _probe.Locate(Kind, null);
            Placeholder = found.Path is { Length: > 0 } path ? F(AutoFound, path) : T(AutoNotFound);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Placeholder = T(AutoPlain);
        }
    }

    /// <summary>찾지 못했을 때 살펴본 곳(칸이 비었을 때만, 적은 경로가 틀렸으면 그 경로 하나라 되풀이하지 않는다).</summary>
    private string Searched()
    {
        if (_probe is null || NormalizedPath is not null)
        {
            return string.Empty;
        }

        try
        {
            var places = _probe.Locate(Kind, null).Searched;
            return places.Count == 0 ? string.Empty : F(SearchedPlaces, string.Join(", ", places));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return string.Empty;
        }
    }

    private string T(string korean) => Translate(_text, korean);

    private string F(string korean, string arg) => string.Format(CultureInfo.CurrentCulture, T(korean), arg);

    /// <summary>영어 화면이면 문구 표(<c>literals-en.json</c>)의 영어로, 아니면(또는 표에 없으면) 그대로.</summary>
    internal static string Translate(IShellText text, string korean) =>
        text.Language == "en" && LiteralTranslator.Table.TryGetValue(korean, out var english) ? english : korean;

    // ---- 한국어 글(영어는 literals-en.json) ----

    internal const string AutoPlain = "비워 두면 자동으로 찾기";
    internal const string AutoFound = "비워 두면 자동으로 찾기 · 찾은 위치: {0}";
    internal const string AutoNotFound = "비워 두면 자동으로 찾기 · 지금은 찾지 못함";
    internal const string NoProbe = "도구를 확인하는 서비스가 아직 준비되지 않았습니다.";
    internal const string Checking = "도구 확인 중…";
    internal const string Usable = "사용할 수 있음 — {0}";
    internal const string NotFound = "도구를 찾지 못했습니다.";
    internal const string SearchedPlaces = "살펴본 곳: {0}";
    internal const string CheckFailed = "도구를 확인하지 못했습니다: {0}";

    internal static readonly string[] SqlLoaderGuide =
    [
        "1. Oracle Instant Client 다운로드 페이지(oracle.com)에서 같은 버전의 Basic(또는 Basic Light) 패키지와 Tools 패키지를 받습니다. 대상 DB 버전보다 낮지 않은 클라이언트를 권장합니다.",
        "2. 두 패키지를 같은 폴더(예: C:\\oracle\\instantclient_23_x)에 풉니다. 그 폴더에 sqlldr.exe 가 생깁니다.",
        "3. 이 칸에 sqlldr.exe 의 경로나 그 폴더를 적거나, 그 폴더를 PATH 환경 변수에 넣습니다. 칸을 비워 두면 PATH·ORACLE_HOME 등에서 찾습니다.",
        "4. Windows 에서는 Microsoft Visual C++ 재배포 패키지가 필요할 수 있습니다. 도구가 실행되지 않으면 설치하세요.",
        "5. 「도구 확인」을 눌러 버전 줄이 보이는지 확인합니다.",
        "접속은 앱 접속 프로필의 접속 주소(EZConnect 또는 TNS 별칭)를 그대로 씁니다. TNS 별칭이면 tnsnames.ora 가 든 폴더를 TNS_ADMIN 환경 변수로 알려 주세요.",
    ];

    internal static readonly string[] TbLoaderGuide =
    [
        "1. Tibero 클라이언트 설치본(또는 서버 설치본)을 설치합니다. tbLoader 는 TB_HOME\\client\\bin\\tbloader.exe 에 있습니다.",
        "2. TB_HOME 환경 변수가 있으면 칸을 비워 두어도 자동으로 찾습니다. 없으면 이 칸에 tbloader.exe 의 경로나 그 폴더를 적습니다.",
        "3. 「도구 확인」을 눌러 버전 줄이 보이는지 확인합니다.",
        "tbLoader 적재는 실험 기능입니다. 실제 Tibero DB 에서 아직 시험하지 않았으니, 중요한 작업은 기본 방식으로 넣거나 결과를 확인하세요.",
    ];
}
