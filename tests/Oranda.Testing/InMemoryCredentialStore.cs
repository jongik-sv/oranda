using Oranda.Core.Security;

namespace Oranda.Testing;

/// <summary>메모리 자격 증명 저장소.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, byte[]> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => _items.Keys;

    public Task SaveAsync(string key, ReadOnlyMemory<byte> secret)
    {
        _items[key] = secret.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> LoadAsync(string key) =>
        Task.FromResult(_items.TryGetValue(key, out var value) ? (byte[]?)value.ToArray() : null);

    public Task DeleteAsync(string key)
    {
        _items.Remove(key);
        return Task.CompletedTask;
    }
}
