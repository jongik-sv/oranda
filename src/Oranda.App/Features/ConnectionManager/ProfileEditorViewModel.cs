using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.App.Features.Ai.Agent;
using Oranda.App.Features.Ai.Settings;
using Oranda.App.Features.Localization;
using Oranda.App.ViewModels;
using Oranda.Core.Connection;
using Oranda.Platform.Storage;

namespace Oranda.App.Features.ConnectionManager;

/// <summary>접속 방식(CONN-01). 저장 형식은 모두 <c>dataSource</c> 문자열 하나다.</summary>
public enum ConnectionMode
{
    /// <summary>호스트·포트·서비스 이름(또는 SID)을 따로 입력. 서비스 이름은 Easy Connect 문자열, SID 는 접속 기술자로 합쳐 저장.</summary>
    HostPortService,

    /// <summary>Easy Connect Plus 문자열을 그대로 입력(<c>host:port/service?옵션</c>, <c>tcps://…</c>, 접속 기술자 포함).</summary>
    EasyConnect,

    /// <summary>tnsnames.ora 별칭(+ TNS 디렉터리).</summary>
    TnsAlias,
}

/// <summary><c>dataSource</c> 문자열과 접속 방식 사이의 변환.</summary>
public static partial class DataSourceComposer
{
    [GeneratedRegex(@"^(?:(?<scheme>tcps?)://)?(?<host>[A-Za-z0-9_.\-]+)(?::(?<port>\d{1,5}))?/(?<service>[A-Za-z0-9_.\-$#]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HostPortPattern();

    // 공백을 뺀 단순 SID 기술자: 주소 하나(TCP·TCPS)와 CONNECT_DATA 의 SID 하나뿐. 다른 키가 있으면 사용자 지정으로 둔다.
    [GeneratedRegex(@"^\(DESCRIPTION=\(ADDRESS=\(PROTOCOL=(?<proto>TCPS?)\)\(HOST=(?<host>[A-Za-z0-9_.\-]+)\)\(PORT=(?<port>\d{1,5})\)\)\(CONNECT_DATA=\(SID=(?<sid>[A-Za-z0-9_.\-$#]+)\)\)\)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SidDescriptorPattern();

    /// <summary>
    /// 호스트·포트·서비스를 한 문자열로. 포트가 비면 생략(기본 1521). TLS 면 <c>tcps://</c>.
    /// SID 는 Easy Connect 로 쓸 수 없어 접속 기술자로 만든다(포트가 비면 1521, TLS 면 <c>PROTOCOL=TCPS</c>).
    /// </summary>
    public static string Compose(bool tls, string host, string? port, string service, bool useSid = false)
    {
        if (useSid)
        {
            var p = string.IsNullOrWhiteSpace(port) ? "1521" : port.Trim();
            return $"(DESCRIPTION=(ADDRESS=(PROTOCOL={(tls ? "TCPS" : "TCP")})(HOST={host.Trim()})(PORT={p}))(CONNECT_DATA=(SID={service.Trim()})))";
        }

        var scheme = tls ? "tcps://" : string.Empty;
        var portPart = string.IsNullOrWhiteSpace(port) ? string.Empty : ":" + port.Trim();
        return $"{scheme}{host.Trim()}{portPart}/{service.Trim()}";
    }

    /// <summary>단순한 <c>[tcps://]host[:port]/service</c> 이면 나눠 준다.</summary>
    public static bool TryParseHostPort(string dataSource, out bool tls, out string host, out string port, out string service)
    {
        tls = false;
        host = port = service = string.Empty;
        var m = HostPortPattern().Match(ConnectStringText.Normalize(dataSource));
        if (!m.Success)
        {
            return false;
        }

        tls = m.Groups["scheme"].Value.Equals("tcps", StringComparison.OrdinalIgnoreCase);
        host = m.Groups["host"].Value;
        port = m.Groups["port"].Value;
        service = m.Groups["service"].Value;
        return true;
    }

    /// <summary>
    /// 호스트·포트 방식으로 보일 수 있는 문자열이면 나눠 준다: <c>[tcps://]host[:port]/service</c> 또는 단순 SID 기술자
    /// (공백은 무시, <paramref name="sid"/> = true).
    /// </summary>
    public static bool TryParseBasic(string dataSource, out bool tls, out string host, out string port, out string service, out bool sid)
    {
        sid = false;
        var text = ConnectStringText.Normalize(dataSource);
        if (TryParseHostPort(text, out tls, out host, out port, out service))
        {
            return true;
        }

        var m = SidDescriptorPattern().Match(string.Concat(text.Where(c => !char.IsWhiteSpace(c))));
        if (!m.Success)
        {
            return false;
        }

        tls = m.Groups["proto"].Value.Equals("TCPS", StringComparison.OrdinalIgnoreCase);
        host = m.Groups["host"].Value;
        port = m.Groups["port"].Value;
        service = m.Groups["sid"].Value;
        sid = true;
        return true;
    }

    /// <summary>저장된 문자열을 보고 편집기에서 보일 방식을 고른다(여러 줄·주석·별칭은 정리한 한 줄로 판별).</summary>
    public static ConnectionMode Detect(string dataSource)
    {
        dataSource = ConnectStringText.Normalize(dataSource);
        if (dataSource.Length == 0)
        {
            return ConnectionMode.HostPortService;
        }

        if (TryParseBasic(dataSource, out _, out _, out _, out _, out _))
        {
            return ConnectionMode.HostPortService;
        }

        return dataSource.AsSpan().IndexOfAny("/:(=?") >= 0 ? ConnectionMode.EasyConnect : ConnectionMode.TnsAlias;
    }

    /// <summary>
    /// 저장된 프로필의 연결 문자열(SQL*Plus·SQLcl 식 <c>user@접속 문자열 [AS 역할]</c>). 비밀번호는 넣지 않는다.
    /// </summary>
    public static string Describe(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Describe(profile.User, profile.ProxyUser, profile.DataSource, profile.Auth);
    }

    /// <summary>
    /// 연결 문자열 미리보기: <c>ft@localhost:1522/FREEPDB1</c>, TCPS 면 <c>ft@tcps://host:2484/svc</c>, 프록시면 <c>app[ft]@…</c>.
    /// 비밀번호 칸은 받지 않으며, 접속 문자열이나 사용자에 비밀이 섞여 있으면(저장 때 거부되는 값) 빈 글자를 돌려준다.
    /// </summary>
    public static string Describe(string? user, string? proxyUser, string? dataSource, AuthMode auth)
    {
        var target = ConnectStringText.Normalize(dataSource);
        var who = user?.Trim() ?? string.Empty;
        var proxy = proxyUser?.Trim() ?? string.Empty;
        if (target.Length == 0 || ConnectionProfileValidator.ContainsSecret(target) || who.Contains('/', StringComparison.Ordinal) || proxy.Contains('/', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (proxy.Length > 0)
        {
            who = $"{proxy}[{who}]";
        }

        var text = who.Length == 0 ? target : $"{who}@{target}";
        return auth == AuthMode.Default ? text : $"{text} AS {auth.ToString().ToUpperInvariant()}";
    }
}

/// <summary>환경 라벨 색 규칙(ui-shell.md 4.1): 빨강 계열은 운영만 쓴다.</summary>
public static class LabelColorRules
{
    /// <summary>운영이 아닌 접속에 빨강 계열을 넣었을 때(입력·고르기 같은 글).</summary>
    public const string RedReservedMessage = "빨강 계열 색은 운영 접속만 씁니다. 다른 색을 고르세요.";

    /// <summary>바이트 셋을 <c>#RRGGBB</c>(대문자)로.</summary>
    public static string ToHex(byte r, byte g, byte b) => string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");

    /// <summary>화면에 보일 라벨 색: 운영은 항상 빨강, 나머지는 사용자 색(형식이 맞을 때) 또는 환경 기본색.</summary>
    public static string Effective(ConnectionEnvironment environment, string? color) => environment switch
    {
        ConnectionEnvironment.Production => "#C42B1C",
        _ when TryParse(color?.Trim(), out var r, out var g, out var b) => ToHex(r, g, b),
        ConnectionEnvironment.Test => "#B65A00",
        _ => "#107C10",
    };

    /// <summary><c>#RRGGBB</c> 를 읽는다. 형식이 틀리면 false.</summary>
    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (hex is not { Length: 7 } || hex[0] != '#')
        {
            return false;
        }

        return byte.TryParse(hex.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out r)
            && byte.TryParse(hex.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out g)
            && byte.TryParse(hex.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out b);
    }

    /// <summary>색상 각도 345°~15°, 채도 50% 이상이면 빨강 계열.</summary>
    public static bool IsRedFamily(string? hex)
    {
        if (!TryParse(hex, out var r, out var g, out var b))
        {
            return false;
        }

        double rf = r / 255d, gf = g / 255d, bf = b / 255d;
        var max = Math.Max(rf, Math.Max(gf, bf));
        var min = Math.Min(rf, Math.Min(gf, bf));
        var delta = max - min;
        if (delta == 0)
        {
            return false;
        }

        var lightness = (max + min) / 2;
        var saturation = delta / (1 - Math.Abs((2 * lightness) - 1));
        double hue;
        if (max == rf)
        {
            hue = 60 * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            hue = 60 * (((bf - rf) / delta) + 2);
        }
        else
        {
            hue = 60 * (((rf - gf) / delta) + 4);
        }

        if (hue < 0)
        {
            hue += 360;
        }

        return saturation >= 0.5 && (hue >= 345 || hue <= 15);
    }
}

/// <summary>
/// 프로필 편집기(접속 관리자 오른쪽): 접속 방식·인증·프록시·환경·읽기 전용·팩·NLS(CONN-01~03, 06, 08).
/// 비밀번호는 이 모델이 메모리에서만 들고 있다가 저장 때 <see cref="IConnectionService"/> 로 넘긴다.
/// </summary>
public sealed partial class ProfileEditorViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    /// <summary>접속 설명(여러 줄). 저장 때 앞뒤 공백을 빼고, 비면 null.</summary>
    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>그룹 칸이 제안하는 기존 폴더 경로(접속 관리자가 채운다). 목록에 없는 경로를 직접 입력해도 된다.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _knownGroups = [];

    [ObservableProperty]
    private ConnectionEnvironment _environment = ConnectionEnvironment.Development;

    [ObservableProperty]
    private string _colorText = string.Empty;

    [ObservableProperty]
    private ConnectionMode _mode = ConnectionMode.HostPortService;

    [ObservableProperty]
    private string _host = string.Empty;

    [ObservableProperty]
    private string _port = "1521";

    [ObservableProperty]
    private string _service = string.Empty;

    [ObservableProperty]
    private bool _useTls;

    /// <summary>서비스 칸이 SID 인지(아니면 서비스 이름). SID 면 접속 기술자로 저장한다.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServiceKindIndex))]
    private bool _useSid;

    [ObservableProperty]
    private string _easyConnect = string.Empty;

    [ObservableProperty]
    private string _tnsAlias = string.Empty;

    [ObservableProperty]
    private string _tnsAdmin = string.Empty;

    /// <summary>TNS 디렉터리의 tnsnames.ora 에서 읽은 별칭(별칭 칸이 제안한다). 직접 입력해도 된다.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _tnsAliases = [];

    /// <summary>고른 별칭의 접속 요약(<c>host:port/service</c>) 또는 TNS 디렉터리 안내. 보일 것이 없으면 빈 글자.</summary>
    [ObservableProperty]
    private string _tnsAliasDetail = string.Empty;

    /// <summary>이름 칸을 별칭으로 채웠는지(별칭을 바꾸면 이름도 따라 바꾼다. 사람이 고친 이름은 건드리지 않는다).</summary>
    private string? _nameFromAlias;

    [ObservableProperty]
    private string _walletLocation = string.Empty;

    [ObservableProperty]
    private string _user = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private bool _savePassword = true;

    [ObservableProperty]
    private AuthMode _auth = AuthMode.Default;

    [ObservableProperty]
    private string _proxyUser = string.Empty;

    [ObservableProperty]
    private bool _readOnly;

    [ObservableProperty]
    private LicensePack _licensePack = LicensePack.None;

    [ObservableProperty]
    private decimal _connectTimeoutSeconds = 15;

    [ObservableProperty]
    private decimal _keepAliveSeconds;

    [ObservableProperty]
    private string _nlsDateFormat = string.Empty;

    [ObservableProperty]
    private string _nlsTimestampFormat = string.Empty;

    [ObservableProperty]
    private string _nlsLanguage = string.Empty;

    [ObservableProperty]
    private string _nlsTerritory = string.Empty;

    [ObservableProperty]
    private string _nlsSort = string.Empty;

    /// <summary>값이 바뀌면 다시 계산하는 화면용 속성(이 이름들의 변경 알림은 다시 계산을 부르지 않는다).</summary>
    private static readonly string[] Derived =
    [
        nameof(IsDirty), nameof(ShowRequired), nameof(NameMissing), nameof(UserMissing), nameof(HostMissing), nameof(ServiceMissing),
        nameof(EasyConnectMissing), nameof(EasyConnectError), nameof(TnsAliasMissing), nameof(HasRequiredFields), nameof(ConnectionStringPreview),
        nameof(IsHostPortMode), nameof(IsEasyConnectMode), nameof(IsTnsMode), nameof(UsesWallet), nameof(CanPickColor),
        nameof(EffectiveColor), nameof(ColorLabel), nameof(ColorWarning),
    ];

    /// <summary>저장된 값을 채우는 중(변경으로 치지 않는다).</summary>
    private bool _loading;

    /// <summary>편집을 시작할 때의 폼 값. 지금 값과 다르면 <see cref="IsDirty"/>.</summary>
    private FormState _baseline;

    private ConnectionAiAccessViewModel? _aiAccess;

    /// <summary>「AI 허용 수준」 영역을 보인다: aiTuning 기능이 켜져 있을 때만(앱 밖에서는 보이지 않는다).</summary>
    public bool AiVisible { get; set; } = (Avalonia.Application.Current as App)?.Services?.GetService<Oranda.Core.Features.IFeatureGate>()
        ?.IsEnabled(Oranda.Core.Features.FeatureIds.AiTuning) ?? false;

    /// <summary>「AI」 쪽 모델(AI 허용 수준·EXPLAIN 허용). 환경 라벨이 바뀌면 상한이 따라 바뀐다.</summary>
    public ConnectionAiAccessViewModel AiAccess => _aiAccess ??= CreateAiAccess();

    private ConnectionAiAccessViewModel CreateAiAccess()
    {
        var model = new ConnectionAiAccessViewModel(Original with { Environment = Environment }) { ConnectionName = Name.Trim() };
        model.PropertyChanged += (_, _) => RefreshDerived();
        return model;
    }

    partial void OnNameChanged(string value)
    {
        if (_aiAccess is not null)
        {
            _aiAccess.ConnectionName = value.Trim();
        }
    }

    partial void OnEnvironmentChanged(ConnectionEnvironment value)
    {
        if (_aiAccess is not null)
        {
            _aiAccess.Environment = value;
        }
    }

    public ProfileEditorViewModel()
    {
        _baseline = Capture();
    }

    /// <summary>편집 시작 때의 프로필(Id·credentialRef·마지막 접속 시각을 이어 받는다). 새 프로필이면 Id 만 새로 발급한 빈 값.</summary>
    public ConnectionProfile Original { get; private set; } = new(string.Empty, string.Empty, string.Empty, Id: Guid.NewGuid());

    /// <summary>새 프로필인지(저장 전).</summary>
    public bool IsNew { get; private set; } = true;

    /// <summary>접속 방식 선택지(화면 콤보).</summary>
    public IReadOnlyList<ChoiceItem<ConnectionMode>> Modes { get; } =
    [
        new(ConnectionMode.HostPortService, "호스트·포트"),
        new(ConnectionMode.TnsAlias, "TNS"),
        new(ConnectionMode.EasyConnect, "사용자 지정"),
    ];

    public IReadOnlyList<ChoiceItem<ConnectionEnvironment>> Environments { get; } =
    [
        new(ConnectionEnvironment.Development, "개발"),
        new(ConnectionEnvironment.Test, "테스트"),
        new(ConnectionEnvironment.Production, "운영"),
    ];

    public IReadOnlyList<ChoiceItem<AuthMode>> AuthModes { get; } =
    [
        new(AuthMode.Default, "기본"),
        new(AuthMode.SysDba, "SYSDBA"),
        new(AuthMode.SysOper, "SYSOPER"),
        new(AuthMode.SysBackup, "SYSBACKUP"),
        new(AuthMode.SysDg, "SYSDG"),
        new(AuthMode.SysKm, "SYSKM"),
    ];

    public IReadOnlyList<ChoiceItem<LicensePack>> Packs { get; } =
    [
        new(LicensePack.None, "없음"),
        new(LicensePack.Diagnostics, "Diagnostics Pack"),
        new(LicensePack.DiagnosticsAndTuning, "Diagnostics + Tuning Pack"),
    ];

    public bool IsHostPortMode => Mode == ConnectionMode.HostPortService;

    public bool IsEasyConnectMode => Mode == ConnectionMode.EasyConnect;

    public bool IsTnsMode => Mode == ConnectionMode.TnsAlias;

    /// <summary>서비스 칸 라벨 콤보(0 = 서비스 이름, 1 = SID).</summary>
    public int ServiceKindIndex
    {
        get => UseSid ? 1 : 0;
        set
        {
            if (value is 0 or 1) // 콤보가 선택을 비울 때(-1)는 바꾸지 않는다
            {
                UseSid = value == 1;
            }
        }
    }

    /// <summary>
    /// 지갑 폴더를 쓰는 방식인지: TNS 별칭(ADB 등), Easy Connect 문자열(<c>tcps://</c>), TLS 를 켠 호스트·포트·서비스.
    /// TNS 디렉터리는 TNS 별칭에서만 쓴다(<see cref="IsTnsMode"/>). 쓰지 않는 칸은 화면에서 숨기고 저장하지 않는다.
    /// </summary>
    public bool UsesWallet => !IsHostPortMode || UseTls;

    /// <summary>운영은 색을 바꿀 수 없다(항상 빨강).</summary>
    public bool CanPickColor => Environment != ConnectionEnvironment.Production;

    /// <summary>라벨 색 펼침 창의 견본(빨강 계열 없음). 이름은 지금 언어.</summary>
    public IReadOnlyList<LabelColorPreset> ColorPresets { get; } =
    [
        new(UiText.Get("Connection.color.blue", "파랑"), "#0063B1"),
        new(UiText.Get("Connection.color.navy", "남색"), "#2E3A87"),
        new(UiText.Get("Connection.color.teal", "청록"), "#00737A"),
        new(UiText.Get("Connection.color.green", "초록"), "#107C10"),
        new(UiText.Get("Connection.color.olive", "올리브"), "#5A7400"),
        new(UiText.Get("Connection.color.orange", "주황"), "#B65A00"),
        new(UiText.Get("Connection.color.brown", "갈색"), "#7A5230"),
        new(UiText.Get("Connection.color.purple", "보라"), "#6B3FA0"),
        new(UiText.Get("Connection.color.magenta", "자주"), "#B4009E"),
        new(UiText.Get("Connection.color.gray", "회색"), "#5D5D5D"),
    ];

    /// <summary>라벨 색 단추의 견본 색(<c>#RRGGBB</c>).</summary>
    public string EffectiveColor => LabelColorRules.Effective(Environment, ColorText);

    /// <summary>라벨 색 단추 글자: 사용자 색이면 그 값, 아니면 「기본 색」.</summary>
    public string ColorLabel => !CanPickColor || IsBlank(ColorText)
        ? UiText.Get("Connection.color.default", "기본 색")
        : ColorText.Trim().ToUpperInvariant();

    /// <summary>운영이 아닌데 빨강 계열이면 경고 글(저장도 거부된다). 아니면 빈 글자.</summary>
    public string ColorWarning => CanPickColor && LabelColorRules.IsRedFamily(ColorText.Trim()) ? LabelColorRules.RedReservedMessage : string.Empty;

    /// <summary>견본·색 고르기로 고른 색을 넣는다. null 이면 환경 기본색(빈 값). 운영이면 무시한다.</summary>
    public void PickColor(string? hex)
    {
        if (CanPickColor)
        {
            ColorText = hex?.Trim().ToUpperInvariant() ?? string.Empty;
        }
    }

    /// <summary>편집을 시작한 뒤 값을 바꿨는지(되돌리면 다시 false). 비밀번호 칸에 글자가 있으면 저장할 것이 있으므로 true.</summary>
    public bool IsDirty { get; private set; }

    /// <summary>필수 칸 표시를 보일지. 새 접속은 무엇이든 입력하기 전까지 빨간 표시를 늘어놓지 않는다.</summary>
    public bool ShowRequired => !IsNew || IsDirty;

    public bool NameMissing => ShowRequired && IsBlank(Name);

    public bool UserMissing => ShowRequired && IsBlank(User);

    public bool HostMissing => ShowRequired && IsHostPortMode && IsBlank(Host);

    public bool ServiceMissing => ShowRequired && IsHostPortMode && IsBlank(Service);

    public bool EasyConnectMissing => ShowRequired && IsEasyConnectMode && IsBlank(EasyConnect);

    public bool TnsAliasMissing => ShowRequired && IsTnsMode && IsBlank(TnsAlias);

    /// <summary>사용자 지정 접속 문자열의 괄호 짝이 맞지 않으면 칸 아래 안내 글(저장도 거부된다). 아니면 빈 글자.</summary>
    public string EasyConnectError => IsEasyConnectMode && !IsBlank(EasyConnect) && !ConnectStringText.Parse(EasyConnect).ParenthesesBalanced
        ? UiText.Get("Connection.custom.parens", "접속 문자열의 괄호 짝이 맞지 않습니다. 여는 괄호와 닫는 괄호 수를 확인하세요.")
        : string.Empty;

    /// <summary>필수 칸(이름, 접속 방식에 따른 호스트·서비스 / 접속 문자열 / TNS 별칭, 사용자)이 다 찼는지. 접속·시험 단추가 이 값을 본다.</summary>
    public bool HasRequiredFields => !IsBlank(Name) && !IsBlank(User) && Mode switch
    {
        ConnectionMode.HostPortService => !IsBlank(Host) && !IsBlank(Service),
        ConnectionMode.EasyConnect => !IsBlank(EasyConnect),
        _ => !IsBlank(TnsAlias),
    };

    /// <summary>지금 폼 값의 연결 문자열(읽기 전용 미리보기). 비밀번호는 절대 넣지 않는다.</summary>
    public string ConnectionStringPreview => DataSourceComposer.Describe(User, ProxyUser, CurrentDataSource(), Auth);

    /// <summary>새 프로필 편집기(새 Id, 개발 환경).</summary>
    public static ProfileEditorViewModel ForNew() => new();

    /// <summary>저장된 프로필로 편집기를 채운다(비밀번호는 비워 둔다: 저장 여부만 <see cref="SavePassword"/> 로 보인다).</summary>
    public static ProfileEditorViewModel From(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var vm = new ProfileEditorViewModel { Original = profile, IsNew = false };
        vm._loading = true;
        try
        {
            vm.Load(profile);
        }
        finally
        {
            vm._loading = false;
        }

        vm._aiAccess = vm.CreateAiAccess(); // 기준 값을 잡기 전에 만들어 둔다(나중에 만들면 저장된 허용 수준이 변경으로 보인다)
        vm._baseline = vm.Capture();
        vm.RefreshDerived();
        return vm;
    }

    /// <summary>값이 바뀌면 변경 여부·필수 칸·미리보기를 다시 알린다.</summary>
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } name && Array.IndexOf(Derived, name) < 0)
        {
            RefreshDerived();
        }
    }

    /// <summary>
    /// TNS 디렉터리가 바뀌면 tnsnames.ora 의 별칭을 읽어 별칭 칸 제안으로 둔다. 편집 중이면(불러오는 중이 아니면) 미리 채운다:
    /// 호스트·접속 문자열이 비어 있으면 접속 방식을 「TNS 별칭」 으로 바꾸고, 별칭이 하나뿐이고 칸이 비어 있으면 그 별칭을 넣는다.
    /// </summary>
    partial void OnTnsAdminChanged(string value)
    {
        var file = TnsResolver.FindTnsNames(value.Trim());
        IReadOnlyList<string> aliases = [];
        if (file is not null)
        {
            try
            {
                aliases = [.. WalletImporter.ReadAliases(File.ReadAllText(file)).Order(StringComparer.OrdinalIgnoreCase)];
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        TnsAliases = aliases;
        if (!_loading && aliases.Count > 0)
        {
            if (Mode != ConnectionMode.TnsAlias && IsBlank(Host) && IsBlank(EasyConnect))
            {
                Mode = ConnectionMode.TnsAlias;
            }

            if (IsBlank(TnsAlias) && aliases.Count == 1)
            {
                TnsAlias = aliases[0];
            }
        }

        UpdateTnsAliasDetail();
    }

    /// <summary>
    /// tnsnames.ora 항목을 통째로(<c>별칭 = (DESCRIPTION=…)</c>) 붙여 넣으면 이름이 비었거나 앞서 별칭으로 채운 이름일 때 그 별칭을 이름으로 제안한다.
    /// 저장·접속은 별칭을 뗀 기술자를 쓴다(<see cref="ConnectStringText"/>).
    /// </summary>
    partial void OnEasyConnectChanged(string value)
    {
        if (!_loading && ConnectStringText.Parse(value).Alias is { } alias && (IsBlank(Name) || Name == _nameFromAlias))
        {
            Name = alias;
            _nameFromAlias = Name;
        }
    }

    /// <summary>별칭을 고르면 접속 요약을 보이고, 이름이 비었거나 앞서 별칭으로 채운 이름이면 이름도 별칭으로 채운다.</summary>
    partial void OnTnsAliasChanged(string value)
    {
        if (!_loading && !IsBlank(value) && (IsBlank(Name) || Name == _nameFromAlias))
        {
            Name = value.Trim();
            _nameFromAlias = Name;
        }

        UpdateTnsAliasDetail();
    }

    private void UpdateTnsAliasDetail()
    {
        var dir = TnsAdmin.Trim();
        if (dir.Length == 0)
        {
            TnsAliasDetail = string.Empty;
        }
        else if (TnsResolver.FindTnsNames(dir) is null)
        {
            TnsAliasDetail = UiText.Get("Connection.tns.noFile", "이 폴더에 tnsnames.ora 가 없습니다.");
        }
        else if (IsBlank(TnsAlias))
        {
            TnsAliasDetail = UiText.Get("Connection.tns.count", "tnsnames.ora 별칭 {0}개")
                .Replace("{0}", TnsAliases.Count.ToString(CultureInfo.CurrentCulture), StringComparison.Ordinal);
        }
        else if (TnsResolver.TryResolve(dir, TnsAlias, out var descriptor))
        {
            TnsAliasDetail = TnsResolver.Summarize(descriptor) is { } summary ? "→ " + summary : string.Empty;
        }
        else
        {
            TnsAliasDetail = UiText.Get("Connection.tns.unknown", "tnsnames.ora 에 이 별칭이 없습니다.");
        }
    }

    private void RefreshDerived()
    {
        IsDirty = Password.Length > 0 || Capture() != _baseline;
        foreach (var name in Derived)
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>미리보기용 접속 문자열(입력이 모자라면 빈 글자, 검증 오류는 내지 않는다).</summary>
    private string CurrentDataSource() => Mode switch
    {
        ConnectionMode.HostPortService => IsBlank(Host) || IsBlank(Service) ? string.Empty : DataSourceComposer.Compose(UseTls, Host, Port, Service, UseSid),
        ConnectionMode.EasyConnect => EasyConnect.Trim(),
        _ => TnsAlias.Trim(),
    };

    private static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>비밀번호를 뺀 폼 값 전체(변경 여부 비교용).</summary>
    private FormState Capture() => new(
        Name, Group, Environment, ColorText, Mode, Host, Port, Service, UseTls, UseSid, EasyConnect, TnsAlias, TnsAdmin, WalletLocation,
        User, SavePassword, Auth, ProxyUser, ReadOnly, LicensePack, ConnectTimeoutSeconds, KeepAliveSeconds,
        NlsDateFormat, NlsTimestampFormat, NlsLanguage, NlsTerritory, NlsSort, Comment, _aiAccess?.Draft.AiAccess, _aiAccess?.Draft.AllowExplainPlan);

    private sealed record FormState(
        string Name, string Group, ConnectionEnvironment Environment, string ColorText, ConnectionMode Mode, string Host, string Port,
        string Service, bool UseTls, bool UseSid, string EasyConnect, string TnsAlias, string TnsAdmin, string WalletLocation, string User,
        bool SavePassword, AuthMode Auth, string ProxyUser, bool ReadOnly, LicensePack LicensePack, decimal ConnectTimeoutSeconds,
        decimal KeepAliveSeconds, string NlsDateFormat, string NlsTimestampFormat, string NlsLanguage, string NlsTerritory, string NlsSort,
        string Comment, Oranda.Core.Ai.Access.AiAccessLevel? AiAccess, bool? AiExplain);

    /// <summary>검증을 거쳐 프로필로 만든다. 문제가 있으면 null 과 문제 목록.</summary>
    public ConnectionProfile? TryBuild(out IReadOnlyList<string> errors)
    {
        var list = new List<string>();
        var dataSource = BuildDataSource(list);

        string? color = null;
        if (Environment != ConnectionEnvironment.Production && !string.IsNullOrWhiteSpace(ColorText))
        {
            var text = ColorText.Trim();
            if (!LabelColorRules.TryParse(text, out _, out _, out _))
            {
                list.Add("색은 #RRGGBB 형식으로 입력하세요(예: #3366CC).");
            }
            else if (LabelColorRules.IsRedFamily(text))
            {
                list.Add(LabelColorRules.RedReservedMessage);
            }
            else
            {
                color = text.ToUpperInvariant();
            }
        }

        if (!double.IsFinite((double)ConnectTimeoutSeconds) || ConnectTimeoutSeconds < 1 || ConnectTimeoutSeconds > 3600)
        {
            list.Add("접속 제한 시간은 1~3600 초입니다.");
        }

        if (KeepAliveSeconds < 0 || KeepAliveSeconds > 86400)
        {
            list.Add("연결 유지 간격은 0(끔) 또는 60 초 이상입니다.");
        }

        var nls = new NlsSettings
        {
            DateFormat = NullIfBlank(NlsDateFormat),
            TimestampFormat = NullIfBlank(NlsTimestampFormat),
            Language = NullIfBlank(NlsLanguage),
            Territory = NullIfBlank(NlsTerritory),
            Sort = NullIfBlank(NlsSort),
        };

        var tnsAdmin = IsTnsMode ? NullIfBlank(TnsAdmin) : null;
        var ai = AiAccess.Result;
        if (!AiVisible && AiAccess.NeedsNameConfirmation)
        {
            // 영역이 숨겨져 이름을 다시 입력할 수 없다: 올라가는 수준을 저장하지 않고 환경의 기본값(운영은 끔)으로 둔다.
            ai = ai with { AiAccess = null };
        }
        else if (!AiAccess.CanSave)
        {
            list.Add("AI 허용 수준을 올리려면 「AI」 쪽에서 접속 이름을 다시 입력하세요.");
        }

        var profile = Original with
        {
            Name = Name.Trim(),
            User = User.Trim(),
            DataSource = dataSource,
            Auth = Auth,
            TnsAdmin = tnsAdmin,
            WalletLocation = UsesWallet ? NullIfBlank(WalletLocation) : null,
            ConnectTimeoutSeconds = (int)ConnectTimeoutSeconds,
            Environment = Environment,
            ReadOnly = ReadOnly,
            LicensePack = LicensePack,
            KeepAliveSeconds = (int)KeepAliveSeconds,
            Nls = nls == new NlsSettings() ? null : nls,
            ProxyUser = NullIfBlank(ProxyUser),
            Color = color,
            Group = NullIfBlank(Group),
            Comment = NullIfBlank(Comment),
            AiAccess = ai.AiAccess,
            AllowExplainPlan = ai.AllowExplainPlan,
        };

        list.AddRange(ConnectionProfileValidator.Validate(profile));
        errors = list;
        if (list.Count == 0)
        {
            RevokeAgentRunsIfAiChanged(profile);
            return profile;
        }

        return null;
    }

    /// <summary>
    /// 환경 라벨·AI 허용 수준·EXPLAIN 허용이 바뀐 접속의 진행 중인 AI 요청을 폐기한다. 요청은 시작 때의 접속 사본으로 판단하므로 바뀐 설정이 따라가지 않는다.
    /// 저장하지 않고 접속 시험만 해도 폐기될 수 있다(안전한 쪽으로 틀린다).
    /// </summary>
    private void RevokeAgentRunsIfAiChanged(ConnectionProfile profile)
    {
        if (!IsNew
            && (profile.Environment != Original.Environment || profile.AiAccess != Original.AiAccess || profile.AllowExplainPlan != Original.AllowExplainPlan))
        {
            AppAgentSessionSource.Current?.RevokeProfile(Original.Id);
        }
    }


    private void Load(ConnectionProfile p)
    {
        Name = p.Name;
        Group = p.Group ?? string.Empty;
        Comment = p.Comment ?? string.Empty;
        Environment = p.Environment;
        ColorText = p.Color ?? string.Empty;
        User = p.User;
        Auth = p.Auth;
        ProxyUser = p.ProxyUser ?? string.Empty;
        ReadOnly = p.ReadOnly;
        LicensePack = p.LicensePack;
        ConnectTimeoutSeconds = p.ConnectTimeoutSeconds;
        KeepAliveSeconds = p.KeepAliveSeconds;
        TnsAdmin = p.TnsAdmin ?? string.Empty;
        WalletLocation = p.WalletLocation ?? string.Empty;
        SavePassword = p.CredentialRef is not null;
        NlsDateFormat = p.Nls?.DateFormat ?? string.Empty;
        NlsTimestampFormat = p.Nls?.TimestampFormat ?? string.Empty;
        NlsLanguage = p.Nls?.Language ?? string.Empty;
        NlsTerritory = p.Nls?.Territory ?? string.Empty;
        NlsSort = p.Nls?.Sort ?? string.Empty;

        Mode = DataSourceComposer.Detect(p.DataSource);
        switch (Mode)
        {
            case ConnectionMode.HostPortService:
                if (DataSourceComposer.TryParseBasic(p.DataSource, out var tls, out var host, out var port, out var service, out var sid))
                {
                    UseTls = tls;
                    UseSid = sid;
                    Host = host;
                    Port = port;
                    Service = service;
                }

                break;
            case ConnectionMode.EasyConnect:
                EasyConnect = p.DataSource;
                break;
            default:
                TnsAlias = p.DataSource;
                break;
        }
    }

    private string BuildDataSource(List<string> errors)
    {
        switch (Mode)
        {
            case ConnectionMode.HostPortService:
                if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Service))
                {
                    errors.Add("호스트와 서비스 이름을 입력하세요.");
                    return string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(Port) && (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535))
                {
                    errors.Add("포트는 1~65535 사이 숫자입니다.");
                    return string.Empty;
                }

                return DataSourceComposer.Compose(UseTls, Host, Port, Service, UseSid);
            case ConnectionMode.EasyConnect:
                if (string.IsNullOrWhiteSpace(EasyConnect))
                {
                    errors.Add("접속 문자열을 입력하세요.");
                }
                else if (EasyConnectError is { Length: > 0 } parens)
                {
                    errors.Add(parens);
                }

                // 사람이 적은 여러 줄 모양 그대로 저장한다(다시 열면 같은 모양). 접속·시험 때만 한 줄로 정리한다.
                return EasyConnect.Trim();
            default:
                if (string.IsNullOrWhiteSpace(TnsAlias))
                {
                    errors.Add("TNS 별칭을 입력하세요.");
                }

                return TnsAlias.Trim();
        }
    }

    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>라벨 색 견본 하나(이름 + <c>#RRGGBB</c>).</summary>
public sealed record LabelColorPreset(string Name, string Hex)
{
    /// <summary>풍선 도움말·낭독기 이름.</summary>
    public string Tip => $"{Name} {Hex}";
}

/// <summary>콤보 상자 한 줄(값 + 글자).</summary>
public sealed record ChoiceItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}
