using CodeRag.Domain.Exceptions;

namespace CodeRag.Domain.Entities;

/// <summary>Ключ-значение настроек (таблица settings).</summary>
public sealed class SettingsEntry
{
    public string Key { get; private set; }
    public string Value { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SettingsEntry(string key, string value, DateTimeOffset updatedAt)
    {
        Key = key;
        Value = value;
        UpdatedAt = updatedAt;
    }

    public static SettingsEntry Create(string key, string value, DateTimeOffset? updatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new DomainValidationException("Settings Key must not be empty.");
        if (value is null) throw new DomainValidationException("Settings Value must not be null.");
        return new SettingsEntry(key.Trim(), value, updatedAt ?? DateTimeOffset.UtcNow);
    }

    public void Update(string value, DateTimeOffset? at = null)
    {
        if (value is null) throw new DomainValidationException("Settings Value must not be null.");
        Value = value;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }
}
