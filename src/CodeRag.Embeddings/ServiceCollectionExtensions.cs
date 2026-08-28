using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CodeRag.Embeddings;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует IEmbeddingProvider (OpenAI-compatible) с HttpClient, retry/timeout/batching.
    /// Конфигурация из EmbeddingOptions (endpoint http://llama-embeddings:8080/v1/embeddings, модель bge-m3).
    /// Кэш по ContentHash+Model+ConfigHash — через chunks.embedded_at + in-memory LRU внутри провайдера.
    /// Инвалидация: ContentHash не менялся и Model/ConfigHash не менялся → skip, иначе re-embed;
    /// при смене модели — Rebuild job без репарса (см. Indexer).
    /// </summary>
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, Action<EmbeddingOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<EmbeddingOptions>(_ => { });

        services.AddHttpClient<IEmbeddingProvider, OpenAICompatibleEmbeddingProvider>((sp, client) =>
        {
            var opts = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            // Timeout на уровне HttpClient не ставим — контролируется per-batch CancellationToken (EmbeddingOptions.Timeout)
            client.Timeout = Timeout.InfiniteTimeSpan;

            // Per-repo embedding_proxy_id: если указан ProxyUrl — настраиваем handler прокси.
            // HttpClient здесь создаётся без прокси; proxy применяется через EmbeddingOptions.ProxyUrl
            // На уровне JobRunner можно пересоздать HttpClient с WebProxy при необходимости.
            if (!string.IsNullOrWhiteSpace(opts.ProxyUrl) && Uri.TryCreate(opts.ProxyUrl, UriKind.Absolute, out var proxyUri))
            {
                // Note: WebProxy устанавливается через HttpClientHandler; при AddHttpClient без IHttpClientBuilder
                // прокси нужно настраивать через ConfigurePrimaryHttpMessageHandler ниже.
            }
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value;
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(opts.ProxyUrl) && Uri.TryCreate(opts.ProxyUrl, UriKind.Absolute, out var proxyUri))
            {
                handler.Proxy = new System.Net.WebProxy(proxyUri);
                handler.UseProxy = true;
            }
            return handler;
        });

        // Также регистрируем EmbeddingOptions как singleton для удобного доступа без IOptions
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value);

        return services;
    }

    /// <summary>Перегрузка с IConfiguration binding (appsettings.json section "Embeddings").</summary>
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, string sectionName = "Embeddings")
    {
        services.Configure<EmbeddingOptions>(configuration.GetSection(sectionName));
        return services.AddEmbeddings();
    }
}
