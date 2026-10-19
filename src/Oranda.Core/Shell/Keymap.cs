namespace Oranda.Core.Shell;

/// <summary>
/// 키맵 파일 <c>keymaps/{id}.json</c> (data-model.md 2.4 + commands.md 4장 <c>mac</c> 필드).
/// 프리셋은 앱에 든 임베디드 리소스, 사용자 키맵은 <c>{data}/keymaps/{id}.json</c>(<see cref="BasedOn"/> + 차이).
/// </summary>
public sealed record Keymap
{
    /// <summary>현재 1. TODO(연결): Core <c>SchemaVersions.Keymap</c> 로 바꾼다.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>프리셋 <c>default</c>, <c>orange5</c>, <c>plsqldev</c>, <c>sqldev</c> (= 파일 이름).</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>바탕 프리셋 id. 프리셋 자신은 null.</summary>
    public string? BasedOn { get; init; }

    public IReadOnlyList<KeyBinding> Bindings { get; init; } = [];
}

/// <summary>바인딩 한 줄 = (명령, <see cref="When"/>) 하나.</summary>
public sealed record KeyBinding
{
    /// <summary>명령 ID.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// 키 조합 목록. 표기 <c>Primary+Ctrl+Alt+Shift+Meta+키</c>(키 = Avalonia <c>Key</c> 이름), 연속 키는 공백 하나.
    /// <c>[]</c> = 바탕 키 지움.
    /// </summary>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>포커스 키(commands.md 2.2). null = 전역.</summary>
    public string? When { get; init; }

    /// <summary>macOS 에서 <see cref="Keys"/> 대신 쓰는 키 목록. null = <see cref="Keys"/> 그대로.</summary>
    public IReadOnlyList<string>? Mac { get; init; }

    /// <summary>플랫폼에 맞는 키 목록.</summary>
    public IReadOnlyList<string> KeysFor(bool macOS) => macOS && Mac is not null ? Mac : Keys;
}
