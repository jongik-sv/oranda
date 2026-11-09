using Oranda.Core.Catalog.Providers;

namespace Oranda.Core.Tests.Catalog.Providers;

/// <summary>
/// 기반 서비스(Catalog*Service) 시험용 가짜 질의 포트. 들어온 id 를 먼저 적고(<see cref="Entered"/>), 그다음 바인드를 읽는다(지연 바인드 순서 확인).
/// 서버 오류는 <see cref="InvalidOperationException"/>, 그중 메시지가 <see cref="NoAccessPrefix"/> 로 시작하면 「볼 수 없음」으로 본다.
/// </summary>
internal sealed class FakeCatalogPort
{
    public const string NoAccessPrefix = "NOACCESS";

    public List<string> Entered { get; } = [];

    public List<(string Id, IReadOnlyList<CatalogBind> Binds, CatalogQueryOptions Options)> Calls { get; } = [];

    public Dictionary<string, CatalogRows> Rows { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, Exception> Errors { get; } = new(StringComparer.Ordinal);

    public static CatalogRows Of(params object?[][] rows) => new([], rows);

    public static bool IsServerError(Exception ex) => ex is InvalidOperationException;

    public static bool IsNoAccess(Exception ex) => ex is InvalidOperationException && ex.Message.StartsWith(NoAccessPrefix, StringComparison.Ordinal);

    public Task<CatalogRows> QueryAsync(string id, IReadOnlyList<CatalogBind> binds, CatalogQueryOptions options)
    {
        Entered.Add(id);
        if (Errors.TryGetValue(id, out var error))
        {
            throw error;
        }

        Calls.Add((id, [.. binds], options));
        return Task.FromResult(Rows.TryGetValue(id, out var rows) ? rows : new CatalogRows([], []));
    }

    public IReadOnlyList<CatalogBind> BindsOf(string id) => Calls.Single(c => c.Id == id).Binds;
}
