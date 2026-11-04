using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Settings;

namespace Oranda.App.Features.Ai.Settings;

/// <summary>PATH 에서 실행 파일 이름을 찾은 결과. 찾기만 하고 실행하지 않는다.</summary>
public enum AiCustomCliInstallState
{
    /// <summary>아직 확인하지 않았거나 확인할 수단·이름이 없다.</summary>
    Unknown,

    /// <summary>다른 스레드에서 찾는 중이다.</summary>
    Checking,

    /// <summary>실행 파일을 찾았다.</summary>
    Found,

    /// <summary>찾지 못했다.</summary>
    NotFound,
}

/// <summary>
/// 명령 문자열을 분해한 결과 가운데 화면이 보이는 것. 분해 규칙은 Core 의 <c>AiCustomCliCommand</c> 가 정하고 이 쪽에는 복제하지 않는다.
/// </summary>
/// <param name="Executable">실행 파일 이름 또는 절대 경로. 분해하지 못했거나 아직 분해하지 않았으면 null.</param>
/// <param name="Arguments">분해한 인자(자리표시 그대로). 분해하지 못했으면 빈 목록.</param>
/// <param name="ParseError">분해하지 못한 이유(고정된 글). 분해했으면 null.</param>
/// <param name="AutoApproveOptions">알려진 자동 승인 옵션이 든 인자.</param>
/// <param name="SecretEnvironmentNames">값이 있는데 이름이 비밀로 보이는 환경 변수 이름.</param>
/// <param name="PromptInArguments"><c>{prompt}</c> 를 써서 글이 명령줄에 실린다.</param>
/// <param name="RiskyEnvironmentNames">프로그램 로딩·런타임 동작을 바꾸는 환경 변수 이름(<c>LD_PRELOAD</c>·<c>NODE_OPTIONS</c>·<c>PATH</c> 등).</param>
public sealed record AiCustomCliCheck(
    string? Executable,
    IReadOnlyList<string> Arguments,
    string? ParseError,
    IReadOnlyList<string> AutoApproveOptions,
    IReadOnlyList<string> SecretEnvironmentNames,
    bool PromptInArguments,
    IReadOnlyList<string>? RiskyEnvironmentNames = null)
{
    /// <summary>로딩·런타임 동작을 바꾸는 환경 변수 이름(없으면 빈 목록).</summary>
    public IReadOnlyList<string> RiskyEnvironmentNames { get; init; } = RiskyEnvironmentNames ?? [];

    /// <summary>아직 분해하지 않은 상태(분해기가 연결되기 전 · 명령이 비었을 때).</summary>
    public static AiCustomCliCheck None { get; } = new(null, [], null, [], [], false);
}

/// <summary>명령 틀 목록의 한 줄: 틀과 화면에 보일 글(이름 · 확인됨/미확인).</summary>
public sealed record AiCustomCliExampleChoice(AiCustomCliExample Example, string Label);

/// <summary>
/// 등록 목록의 한 줄(편집 중인 값). 설정의 <see cref="AiCustomCli"/> 는 불변이라 화면은 이 행을 고치고, 저장 때 <see cref="ToModel"/> 로 바꾼다.
/// 불러온 값은 다듬지 않는다(공백·빈 칸을 그대로 두어야 아무것도 건드리지 않은 설정이 바뀐 것으로 보이지 않는다).
/// </summary>
public sealed partial class AiCustomCliRow : ObservableObject
{
    public AiCustomCliRow(AiCustomCli source)
    {
        System.ArgumentNullException.ThrowIfNull(source);
        Id = source.Id;
        _name = source.Name;
        _command = source.Command;
    }

    /// <summary>안정 id. 이름·명령을 고쳐도 바뀌지 않는다.</summary>
    public string Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name;

    [ObservableProperty]
    private string _command;

    /// <summary>실행 파일을 PATH 에서 찾은 결과.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFound), nameof(IsNotFound), nameof(IsChecking))]
    private AiCustomCliInstallState _installState;

    /// <summary>명령 분해 결과(경고 표시용).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasParseError))]
    private AiCustomCliCheck _check = AiCustomCliCheck.None;

    /// <summary>이 행이 지금 고른 기타 CLI 인가(드롭다운 현재 줄과 같다).</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>이 명령에 대해 항상 보이는 경고 줄(자동 승인 옵션 · 비밀 환경 변수 · <c>{prompt}</c> 노출 · 분해 실패 이유).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    private IReadOnlyList<string> _warnings = [];

    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>명령 틀 표가 이 CLI 에 붙인 안내(저장하지 않고 명령이 바뀔 때마다 다시 정한다. 분해기가 못 잡는 위험을 알릴 때 쓴다).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemplateNote))]
    private string? _templateNote;

    public bool HasTemplateNote => !string.IsNullOrEmpty(TemplateNote);

    /// <summary>가장 최근에 시작한 설치 확인(시험이 끝나기를 기다린다).</summary>
    internal System.Threading.Tasks.Task PendingCheck { get; set; } = System.Threading.Tasks.Task.CompletedTask;

    public bool IsFound => InstallState == AiCustomCliInstallState.Found;

    public bool IsNotFound => InstallState == AiCustomCliInstallState.NotFound;

    public bool IsChecking => InstallState == AiCustomCliInstallState.Checking;

    public bool HasParseError => Check.ParseError is not null;

    /// <summary>이름이 비었으면 id 로 대신 보인다.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    /// <summary>화면에서 방금 더한 초안 줄이다(아무것도 쓰지 않으면 저장하지 않는다).</summary>
    internal bool Draft { get; init; }

    /// <summary>삭제된 행이 늦게 끝난 확인 결과를 받지 않도록 하는 표지.</summary>
    internal bool Removed { get; set; }

    /// <summary>찾기 결과가 오래된 것인지 가리는 번호. 명령이 바뀔 때마다 올라간다.</summary>
    internal int CheckVersion { get; set; }

    public AiCustomCli ToModel() => new() { Id = Id, Name = Name, Command = Command };
}
