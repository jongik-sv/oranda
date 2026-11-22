using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Oranda.App.Features.ConnectionManager;
using Oranda.App.Shell;
using Oranda.App.Shell.Settings;
using Oranda.Core.Ai.Profiles;
using Oranda.Core.Ai.Setup;
using Oranda.Core.Events;
using Oranda.Core.Security;

namespace Oranda.App.Features.AiSetup.Contract;

/// <summary>
/// 도우미 창 하나가 단계들에 넘기는 부품과 단계 사이 값. 창 틀이 열 때 하나 만들어 모든 단계에 같은 객체를 준다.
/// 서비스가 없으면(null) 그 동작은 꺼지고 단계 화면이 안내한다(시험·조립이 덜 된 경우). 화면 스레드에서만 쓴다.
/// </summary>
public sealed class AiSetupContext
{
    private AiSetupPath? _chosenPath;

    /// <param name="settings">설정 읽기·쓰기(프로필 저장·이어 하기 기억).</param>
    public AiSetupContext(SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
    }

    /// <summary>설정 읽기·쓰기.</summary>
    public SettingsService Settings { get; }

    /// <summary>도우미를 연 곳.</summary>
    public AiSetupEntry Entry { get; init; }

    /// <summary>실패 띠에서 열었을 때의 까닭. 그 밖은 null.</summary>
    public AiSetupErrorKind? Reason { get; init; }

    /// <summary>화면 글 공급자. 없으면 앱 글 입구(<c>UiText.Current</c>)를 본다.</summary>
    public IShellText? Text { get; init; }

    /// <summary>화면 언어가 영어다. 단계 글은 이 값으로 고른다(<see cref="AiSetupText"/>).</summary>
    public bool IsEnglish => Text is { } text ? text.Language == "en" : AiSetupText.CurrentIsEnglish;

    /// <summary>접속 목록 읽기·저장(AI 를 쓸 접속). null 이면 그 단계는 건너뛸 수 있게 끝남으로 둔다.</summary>
    public IConnectionService? Connections { get; init; }

    /// <summary>claude 찾기·설치·로그인·시험(구현 = Platform).</summary>
    public IClaudeSetupService? Claude { get; init; }

    /// <summary>회사 AI 서버의 모델 목록·연결 시험.</summary>
    public IAiProfileProbe? Probe { get; init; }

    /// <summary>열쇠 저장소.</summary>
    public ICredentialStore? Credentials { get; init; }

    /// <summary>프로필을 저장한 뒤 <c>SettingsChanged("ai")</c> 를 낼 버스.</summary>
    public IEventBus? Bus { get; init; }

    /// <summary>앱 서비스(외부 에이전트 연결 창처럼 다른 기능을 부를 때). 없으면 null.</summary>
    public IServiceProvider? Services { get; init; }

    /// <summary>클립보드에 글을 쓴다. 기본은 아무것도 안 함.</summary>
    public Func<string, Task> CopyText { get; init; } = _ => Task.CompletedTask;

    /// <summary>주소를 브라우저로 연다. 열었으면 true. 기본은 열지 않음(false).</summary>
    public Func<string, bool> OpenUrl { get; init; } = _ => false;

    /// <summary>이 PC 의 운영체제(직접 설치 한 줄 고르기).</summary>
    public AiHostPlatform Platform { get; init; } = AiProfilePresets.CurrentPlatform;

    /// <summary>Oranda 판(진단 글).</summary>
    public string AppVersion { get; init; } = "";

    /// <summary>고른 갈래. 방법 고르기 단계가 넣고, 창 틀이 <see cref="ChosenPathChanged"/> 를 보고 단계 목록을 바꾼다.</summary>
    public AiSetupPath? ChosenPath
    {
        get => _chosenPath;
        set
        {
            if (_chosenPath == value)
            {
                return;
            }

            _chosenPath = value;
            ChosenPathChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 갈래 B 에서 만든(또는 고른) 프로필 id. 연결 시험·해 보기가 쓴다. 갈래 A 는 null(Claude Code).
    /// 이어 하기 때 창 틀이 <c>ai.setup.lastProfileId</c> 에서 채운다(목록에 없으면 null).
    /// </summary>
    public Guid? CreatedProfileId { get; set; }

    /// <summary>「AI 를 쓸 접속」 에서 켠 접속 id 목록. 해 보기가 연습 탭 접속을 고를 때 쓴다.</summary>
    public IReadOnlyList<Guid> EnabledConnectionIds { get; set; } = [];

    /// <summary><see cref="ChosenPath"/> 가 바뀌었다.</summary>
    public event EventHandler? ChosenPathChanged;
}
