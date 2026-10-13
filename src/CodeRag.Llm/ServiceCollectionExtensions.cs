using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CodeRag.Llm;

public static class ServiceCollectionExtensions
{
    /// <summary>Named HttpClient used by the chat-completion client.</summary>
    public const string ClientName = "llm";

    /// <summary>
    /// Registers the LLM chat-completion abstraction. Options come from the "Llm" configuration
    /// section (settings table / env / appsettings, hot-reloaded via IOptionsMonitor). When no
    /// endpoint is configured the client reports <c>IsConfigured = false</c> and generation degrades
    /// gracefully instead of throwing at resolution time.
    /// </summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, IConfiguration configuration, string sectionName = LlmOptions.SectionName)
    {
        services.Configure<LlmOptions>(configuration.GetSection(sectionName));
        return services.AddLlm();
    }

    /// <summary>Registers the LLM client with an explicit options configurator (test-friendly).</summary>
    public static IServiceCollection AddLlm(this IServiceCollection services, Action<LlmOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<LlmOptions>(_ => { });

        // Timeout is enforced per-request via CancellationToken; the client timeout is infinite.
        // The primary handler applies the optional proxy from options.
        services.AddHttpClient(ClientName)
            .ConfigureHttpClient(c => c.Timeout = System.Threading.Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp =>
            {
                var proxyUrl = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LlmOptions>>().Value.ProxyUrl;
                if (string.IsNullOrWhiteSpace(proxyUrl) || !Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
                    return new System.Net.Http.HttpClientHandler();
                var handler = new System.Net.Http.HttpClientHandler { UseProxy = true };
                handler.Proxy = new System.Net.WebProxy(proxyUri) { UseDefaultCredentials = false };
                return handler;
            });

        services.AddSingleton<IChatCompletionClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<LlmOptions>>();
            var logger = sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OpenAICompatibleChatClient>>();
            return new OpenAICompatibleChatClient(factory, options, logger);
        });

        return services;
    }
}
