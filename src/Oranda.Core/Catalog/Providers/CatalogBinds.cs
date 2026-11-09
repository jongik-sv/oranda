using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Oranda.Core.Catalog.Providers;

/// <summary>
/// <see cref="CatalogBind"/> 만들기. 값 null 은 공급자가 DB NULL 로 묶는다.
/// <see cref="Defer"/> 는 목록을 처음 읽을 때 만든다 — 인자 검사(CheckSqlId 등)가 세션 확인·라이선스 관문·명령 잠금 뒤에 나던 순서를 지킨다.
/// </summary>
[SuppressMessage("Naming", "CA1720:Identifiers should not contain type names", Justification = "바인드 형 이름(Int·Long·Double)은 CatalogValues 와 같은 짝이다.")]
public static class CatalogBinds
{
    /// <summary>바인드 없음.</summary>
    public static IReadOnlyList<CatalogBind> None { get; } = [];

    /// <summary>처음 읽을 때 <paramref name="factory"/> 를 부르는 목록(한 번만, 실패하면 다음에 다시 부른다). 공급자는 명령을 만든 뒤 묶을 때 읽는다.</summary>
    public static IReadOnlyList<CatalogBind> Defer(Func<IReadOnlyList<CatalogBind>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return new Deferred(factory);
    }

    /// <summary>글 값 그대로.</summary>
    public static CatalogBind Text(string name, string? value) => new(name, value, CatalogBindKind.Text);

    /// <summary>글 값: 비었거나 공백뿐이면 null, 아니면 앞뒤 공백을 뗀 값(모니터·튜닝 바인드 규칙).</summary>
    public static CatalogBind Trimmed(string name, string? value) =>
        new(name, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), CatalogBindKind.Text);

    public static CatalogBind Int(string name, int? value) => new(name, value, CatalogBindKind.Int32);

    public static CatalogBind Long(string name, long? value) => new(name, value, CatalogBindKind.Int64);

    public static CatalogBind Double(string name, double? value) => new(name, value, CatalogBindKind.Double);

    public static CatalogBind Date(string name, DateTime? value) => new(name, value, CatalogBindKind.Date);

    private sealed class Deferred(Func<IReadOnlyList<CatalogBind>> factory) : IReadOnlyList<CatalogBind>
    {
        private IReadOnlyList<CatalogBind>? _binds;

        private IReadOnlyList<CatalogBind> Binds => _binds ??= factory();

        public int Count => Binds.Count;

        public CatalogBind this[int index] => Binds[index];

        public IEnumerator<CatalogBind> GetEnumerator() => Binds.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
