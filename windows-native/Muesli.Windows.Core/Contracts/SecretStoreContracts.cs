namespace Muesli.Windows.Services;

public interface ISecretStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);
    bool IsConfigured(string key);
}

public sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? Read(string key) => _values.GetValueOrDefault(key);
    public void Write(string key, string value) => _values[key] = value;
    public void Delete(string key) => _values.Remove(key);
    public bool IsConfigured(string key) =>
        _values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
}
