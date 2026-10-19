using System;
using System.Threading;
using System.Threading.Tasks;
using Oranda.Core.Settings;
using Oranda.Core.Storage;

namespace Oranda.App.Shell.Settings;

/// <summary>
/// 실행 중 앱이 쓰는 설정 한 벌(<see cref="Current"/>)과 바꾸고 저장하는 길(<see cref="UpdateAsync"/>).
/// 저장하면 <see cref="ISettingsStore"/> 가 바뀐 섹션마다 <c>SettingsChanged</c> 를 발행한다.
/// </summary>
public sealed class SettingsService(ISettingsStore store)
{
    /// <summary>글자 크게/작게 한 단계(pt).</summary>
    public const double ZoomStep = 1;

    public const double MinEditorFontSize = 8;

    public const double MaxEditorFontSize = 32;

    public const double DefaultEditorFontSize = 13;

    private AppSettings _current = new();

    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>설정 파일이 새 버전이라 저장하지 않는다.</summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>파일을 읽을 때 알린 말(손상 백업 등). 없으면 null.</summary>
    public string? Notice { get; private set; }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        var doc = await store.LoadAsync(ct).ConfigureAwait(false);
        Volatile.Write(ref _current, doc.Value);
        IsReadOnly = doc.IsReadOnly;
        Notice = doc.Notice;
        return doc.Value;
    }

    /// <summary>값을 바꾸고 저장한다. 바뀐 값이 먼저 <see cref="Current"/> 가 된 뒤 이벤트가 나간다.</summary>
    public async Task UpdateAsync(Func<AppSettings, AppSettings> change, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current);
        Volatile.Write(ref _current, next);
        await store.SaveAsync(next, ct).ConfigureAwait(false);
    }

    public Task SetThemeAsync(Theme theme, CancellationToken ct = default) =>
        UpdateAsync(s => s with { Ui = s.Ui with { Theme = theme } }, ct);

    /// <summary>에디터·그리드·출력 글자 크기를 한 단계 바꾼다(8~32).</summary>
    public Task ZoomAsync(double delta, CancellationToken ct = default) =>
        UpdateAsync(s => s with { Editor = s.Editor with { FontSize = Math.Clamp(s.Editor.FontSize + delta, MinEditorFontSize, MaxEditorFontSize) } }, ct);

    public Task ZoomResetAsync(CancellationToken ct = default) =>
        UpdateAsync(s => s with { Editor = s.Editor with { FontSize = DefaultEditorFontSize } }, ct);
}
