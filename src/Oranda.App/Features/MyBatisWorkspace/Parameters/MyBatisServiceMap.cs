namespace Oranda.App.Features.MyBatisWorkspace.Parameters;

/// <summary>
/// 시험용 서비스 공급자: 형식 → 인스턴스 표. 두 도구 창은 <see cref="IServiceProvider"/> 생성자만 두므로(생성자 고르기 동률을 피하려고)
/// 시험은 허브·명령 레지스트리 같은 서비스를 이 표에 담아 넘긴다.
/// </summary>
internal sealed class MyBatisServiceMap : IServiceProvider
{
    private readonly Dictionary<Type, object> _items = [];

    public MyBatisServiceMap With<T>(T instance)
        where T : class
    {
        _items[typeof(T)] = instance;
        return this;
    }

    public object? GetService(Type serviceType) => _items.GetValueOrDefault(serviceType);
}
