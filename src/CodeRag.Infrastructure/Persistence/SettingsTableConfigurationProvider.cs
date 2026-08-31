using CodeRag.Domain.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeRag.Infrastructure.Persistence;

/// <summary>
/// Читает таблицу `settings` в IConfiguration. Добавляется в конце цепочки,
/// поэтому значения из БД имеют приоритет над env и appsettings.
/// Поддерживает hot reload: SettingsController вызывает FireReload() после Upsert.
/// </summary>
public sealed class SettingsTableConfigurationProvider : ConfigurationProvider
{
    private readonly Func<IServiceProvider> _serviceProviderFactory;
    private readonly Func<ILogger<SettingsTableConfigurationProvider>> _loggerFactory;

    public SettingsTableConfigurationProvider(Func<IServiceProvider> serviceProviderFactory,
        Func<ILogger<SettingsTableConfigurationProvider>> loggerFactory)
    {
        _serviceProviderFactory = serviceProviderFactory;
        _loggerFactory = loggerFactory;
    }

    private ILogger<SettingsTableConfigurationProvider> Logger => _loggerFactory();

    public override void Load()
    {
        Data.Clear();
        ISettingsRepository? repo = null;
        try
        {
            repo = _serviceProviderFactory().GetService<ISettingsRepository>();
        }
        catch (Exception ex)
        {
            // Первый Load() происходит при builder.Build(), когда DI ещё не собран
            Logger.LogDebug("settings provider: service provider not ready: {Message}", ex.Message);
            return;
        }

        if (repo is null)
        {
            Logger.LogWarning("settings provider: ISettingsRepository not registered");
            return;
        }

        try
        {
            var values = repo.GetAllAsync().GetAwaiter().GetResult();
            foreach (var kv in values)
                Data[kv.Key] = kv.Value;
            Logger.LogInformation("settings provider: loaded {Count} values from settings table", values.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "settings provider: failed to load settings table");
        }
    }

    /// <summary>
    /// .NET 10: корень не пересоздаёт данные провайдеров при срабатывании их токена,
    /// поэтому сначала грузим данные сами, затем сигналим ChangeToken'ом
    /// (IOptionsMonitor и секции конфигурации получат невалидацию).
    /// </summary>
    public void FireReload()
    {
        Load();
        OnReload();
    }
}

public sealed class SettingsTableConfigurationSource : IConfigurationSource
{
    private readonly SettingsTableConfigurationProvider _provider;
    public SettingsTableConfigurationSource(SettingsTableConfigurationProvider provider) => _provider = provider;
    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;
}
