namespace Oranda.Core.Shell;

/// <summary>명령 범주 = 메뉴 묶음·팔레트 접두어 (commands.md 3장).</summary>
public enum CommandCategory
{
    File,
    Connection,
    Edit,
    Navigate,
    Format,
    Sql,
    Result,
    View,
    Database,
    Monitor,
    Tools,
    Window,
    Help,
}

/// <summary>
/// 명령 하나의 정의 (commands.md 2장). 기본 단축키는 여기 두지 않는다 — 키는 키맵 파일에만 있다.
/// </summary>
public sealed record CommandDefinition
{
    /// <summary><c>oranda.&lt;영역&gt;.&lt;동작&gt;</c>. 한 번 내보낸 ID 는 바꾸지 않는다.</summary>
    public required string Id { get; init; }

    /// <summary>제목 리소스 키. 기본 <c>Cmd.&lt;Id&gt;</c>.</summary>
    public required string Title { get; init; }

    public required CommandCategory Category { get; init; }

    /// <summary>벡터 아이콘 키. null 이면 메뉴에 아이콘 없음.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// 기본 키 범위(포커스 키, commands.md 2.2). 키맵 편집 화면이 새 바인딩의 <c>when</c> 기본값으로 쓴다.
    /// 실제 키 범위는 키맵이 정한다. null = 전역.
    /// </summary>
    public string? When { get; init; }

    /// <summary>활성 조건식(상태 키, <c>!</c> <c>&amp;&amp;</c> <c>||</c> 괄호). null = 항상.</summary>
    public string? Enablement { get; init; }

    /// <summary>메뉴·툴바·오른쪽 클릭 메뉴 위치. 예: <c>menu:sql/run@10</c>, <c>toolbar:main/run@10</c>, <c>context:grid/copy@30</c>.</summary>
    public IReadOnlyList<string> Placements { get; init; } = [];

    /// <summary>옛 ID(바뀐 경우). 키맵·설정의 옛 ID 를 이 명령으로 잇는다.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    public bool ShowInPalette { get; init; } = true;
}

/// <summary>활성 조건·키 범위 식을 지금 문맥 키로 계산한다. 구현은 셸(TSK-03-01).</summary>
public interface IContextKeys
{
    /// <summary>null·빈 식은 참.</summary>
    bool Evaluate(string? expression);
}

/// <summary>명령 실행 때 처리기에 넘기는 문맥.</summary>
/// <param name="CommandId">실제로 불린 정식 명령 ID(별칭 아님). 한 처리기가 여러 명령을 맡을 때 구분에 쓴다.</param>
/// <param name="Keys">지금 문맥 키.</param>
/// <param name="Parameter">메뉴·팔레트·시험이 넘기는 선택 인자.</param>
public sealed record CommandContext(string CommandId, IContextKeys Keys, object? Parameter = null);

/// <summary>명령 처리기. <see cref="OrandaCommandAttribute"/> 를 단 클래스가 구현하면 셸이 자동 수집한다.</summary>
public interface ICommandHandler
{
    Task ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 명령 레지스트리 (TRD 4.11). 메뉴 클릭·툴바·단축키·팔레트가 모두 <see cref="ExecuteAsync"/> 한 경로로 실행한다.
/// </summary>
public interface ICommandRegistry
{
    /// <summary>등록된 명령 전체(ID 순).</summary>
    IReadOnlyList<CommandDefinition> Commands { get; }

    /// <summary>ID 또는 별칭으로 찾는다. 없으면 null.</summary>
    CommandDefinition? Find(string idOrAlias);

    /// <summary>명령이 있고 <see cref="CommandDefinition.Enablement"/> 가 참이면 true.</summary>
    bool CanExecute(string idOrAlias, IContextKeys keys);

    /// <summary>실행했으면 true, 명령이 없거나 비활성이면 false(키맵의 모르는 ID 는 경고 로그 후 무시).</summary>
    Task<bool> ExecuteAsync(string idOrAlias, IContextKeys keys, object? parameter = null, CancellationToken cancellationToken = default);

    /// <summary>실행 중 등록(확장). 반환값을 Dispose 하면 등록이 풀린다. ID·별칭이 겹치면 <see cref="InvalidOperationException"/>.</summary>
    IDisposable Register(CommandDefinition definition, ICommandHandler handler);

    event EventHandler? CommandsChanged;
}
