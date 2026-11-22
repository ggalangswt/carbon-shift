using System.Text.Json;
namespace CarbonShift;

public interface ILocalStore
{
    Task<T?> ReadAsync<T>(string key, CancellationToken ct = default);
    Task WriteAsync<T>(string key, T value, CancellationToken ct = default);
}

public sealed class LocalStore : ILocalStore
{
    private readonly string root;
    public LocalStore(string? directory = null) =>
        root = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CarbonShift");
    private string PathFor(string key) => Path.Combine(root, key + ".json");
    public async Task<T?> ReadAsync<T>(string key, CancellationToken ct = default)
    {
        try
        {
            using var stream = File.OpenRead(PathFor(key));
            return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return default; }
    }
    public async Task WriteAsync<T>(string key, T value, CancellationToken ct = default)
    {
        Directory.CreateDirectory(root);
        string path = PathFor(key), temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}

