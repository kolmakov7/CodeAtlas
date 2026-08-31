using CodeRag.Git.Auth;
using CodeRag.Git.Services;
using CodeRag.Git.Stores;
using CodeRag.Git.Watching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CodeRag.Git.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует Git, Proxy/SOCKS5, Auth, RepositoryService, Polling, FS watcher.
    /// </summary>
    public static IServiceCollection AddGit(this IServiceCollection services, Action<GitOptions>? configure = null)
    {
        if (configure is not null) services.Configure(configure);
        else services.AddOptions<GitOptions>().ValidateOnStart();

        services.PostConfigure<GitOptions>(o =>
        {
            var dataDir = Environment.GetEnvironmentVariable("CODERAG__DataDir") ?? "/data";
            var expected = Path.Combine(dataDir.TrimEnd('/'), "repos");
            if (o.ReposRoot == "/data/repos" && expected != "/data/repos") o.ReposRoot = expected;
            var keysExpected = Path.Combine(dataDir.TrimEnd('/'), "keys", "ssh");
            if (o.SshKeysDir == "/data/keys/ssh" && keysExpected != "/data/keys/ssh") o.SshKeysDir = keysExpected;
        });
        services.Configure<GitOptions>(o => { });

        // Core git
        services.AddSingleton<ICredentialProtector, NoopCredentialProtector>();
        services.AddSingleton<IGitService, GitService>();

        // Stores via SqliteConnectionFactory (requires AddInfrastructure beforehand)
        services.AddSingleton<IProxyStore, ProxyStore>();
        services.AddSingleton<ICredentialStore, CredentialStore>();

        // Repository lifecycle
        services.AddSingleton<IRepositoryService, RepositoryService>();

        // Polling per-repo timer
        services.AddHostedService<RepositoryPollingService>();

        // File watcher optional
        services.AddSingleton<RepositoryFileWatcherFactory>();

        return services;
    }

    /// <summary>Хелпер для AddGit с IHostBuilder.</summary>
    public static IServiceCollection AddGit(this IServiceCollection services, GitOptions options)
        => services.AddGit(o =>
        {
            o.GitExecutable = options.GitExecutable;
            o.DefaultTimeoutSeconds = options.DefaultTimeoutSeconds;
            o.ReposRoot = options.ReposRoot;
            o.SshKeysDir = options.SshKeysDir;
            o.EnableFileWatcher = options.EnableFileWatcher;
            o.DefaultSyncIntervalSeconds = options.DefaultSyncIntervalSeconds;
            o.NoProxy = options.NoProxy;
        });
}
