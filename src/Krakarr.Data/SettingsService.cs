using System.Collections.Concurrent;
using System.Text.Json;
using Krakarr.Core.Configuration;
using Microsoft.EntityFrameworkCore;

namespace Krakarr.Data;

public interface ISettingsService
{
    Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new();
    Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new();
}

/// <summary>Settings sections stored as JSON in the database, with an in-memory cache.</summary>
public sealed class SettingsService(KrakarrDbContext db, SettingsCache cache) : ISettingsService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public async Task<T> GetAsync<T>(CancellationToken ct = default) where T : class, ISettingsSection, new()
    {
        if (cache.TryGet(T.SectionKey, out var cached))
        {
            return (T)cached;
        }

        var key = T.SectionKey;
        var entry = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        var value = entry is null ? new T() : JsonSerializer.Deserialize<T>(entry.Value, Json) ?? new T();
        cache.Set(T.SectionKey, value);
        return value;
    }

    public async Task SaveAsync<T>(T value, CancellationToken ct = default) where T : class, ISettingsSection, new()
    {
        var json = JsonSerializer.Serialize(value, Json);
        var key = T.SectionKey;
        var entry = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (entry is null)
        {
            db.Settings.Add(new() { Key = T.SectionKey, Value = json });
        }
        else
        {
            entry.Value = json;
        }

        await db.SaveChangesAsync(ct);
        cache.Set(T.SectionKey, value);
        cache.RaiseChanged(T.SectionKey);
    }
}

/// <summary>Singleton cache shared by scoped <see cref="SettingsService"/> instances.</summary>
public sealed class SettingsCache
{
    private readonly ConcurrentDictionary<string, object> _values = new();

    public event Action<string>? Changed;

    public bool TryGet(string key, out object value) => _values.TryGetValue(key, out value!);
    public void Set(string key, object value) => _values[key] = value;
    public void RaiseChanged(string key) => Changed?.Invoke(key);
}
