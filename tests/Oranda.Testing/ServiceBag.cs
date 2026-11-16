namespace Oranda.Testing;

/// <summary>시험용 <see cref="IServiceProvider"/>: 넣어 둔 형식만 돌려주고, 없으면 null.</summary>
public sealed class ServiceBag : IServiceProvider
{
    private readonly Dictionary<Type, object?> _services = [];

    public ServiceBag Add<T>(T? service)
        where T : class
    {
        _services[typeof(T)] = service;
        return this;
    }

    public object? GetService(Type serviceType) => _services.GetValueOrDefault(serviceType);
}
