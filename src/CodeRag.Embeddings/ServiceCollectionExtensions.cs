using System.Net;
using CodeRag.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CodeRag.Embeddings;

public static class ServiceCollectionExtensions
{
    /// <summary>Named HttpClient for the code embedding domain.</summary>
    public const string CodeClientName = "embeddings-code";

    /// <summary>Named HttpClient for the text embedding domain.</summary>
    public const string TextClientName = "embeddings-text";

    /// <summary>
    /// Registers dual embedding providers (code + text) with independent HttpClient, retry/timeout/batching.
    /// Options come from EmbeddingOptions (Code / Text profiles). Each domain is an isolated vector space:
    /// its own endpoint, model, dimensions, and in-process LRU cache (keyed by content+model+config).
    /// A missing endpoint for one domain degrades gracefully (that domain yields no vectors) without
    /// affecting the other domain or DI resolution.
    /// </summary>
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, Action<EmbeddingOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        else
            services.Configure<EmbeddingOptions>(_ => { });

        RegisterProviders(services);
        services.PostConfigure<EmbeddingOptions>(opts => opts.ApplyDomainDefaults());
        return services;
    }

    /// <summary>Binds EmbeddingOptions from a configuration section and registers dual providers.</summary>
    public static IServiceCollection AddEmbeddings(this IServiceCollection services, IConfiguration configuration, string sectionName = EmbeddingOptions.SectionName)
    {
        services.Configure<EmbeddingOptions>(configuration.GetSection(sectionName));
        RegisterProviders(services);

        // 1) Legacy single-model fallback: if only the top-level section (Embeddings:Endpoint etc.) is set
        //    and a domain profile has no endpoint of its own, mirror the legacy values into both domains so
        //    existing single-model deployments keep working. Explicit Code/Text sections always win.
        services.PostConfigure<EmbeddingOptions>(opts => ApplyLegacyFallback(configuration, opts));
        // 2) Per-domain defaults for anything still unset (Code → llama/bge-m3, Text → jina text).
        services.PostConfigure<EmbeddingOptions>(opts => opts.ApplyDomainDefaults());
        return services;
    }

    private static void RegisterProviders(IServiceCollection services)
    {
        // Per-domain named HttpClients. Timeout is handled per-batch via CancellationToken, so the
        // HttpClient timeout is set to infinite. The primary handler applies an optional proxy.
        services.AddHttpClient(CodeClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.ProxyUrl));

        services.AddHttpClient(TextClientName)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(sp => CreateHandler(sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value.ProxyUrl));

        services.AddSingleton<IEmbeddingProviderFactory>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var options = sp.GetRequiredService<IOptionsMonitor<EmbeddingOptions>>();
            var logger = sp.GetRequiredService<ILogger<OpenAICompatibleEmbeddingProvider>>();
            var code = new OpenAICompatibleEmbeddingProvider(factory.CreateClient(CodeClientName), EmbeddingDomain.Code, options, logger);
            var text = new OpenAICompatibleEmbeddingProvider(factory.CreateClient(TextClientName), EmbeddingDomain.Text, options, logger);
            return new DefaultEmbeddingProviderFactory(code, text);
        });

        // Back-compat: the single IEmbeddingProvider / IVectorDimSource resolve to the Code domain so
        // existing consumers (and the Code vector store's dim source) keep working unchanged.
        services.AddSingleton<IEmbeddingProvider>(sp => sp.GetRequiredService<IEmbeddingProviderFactory>().Code);
        services.AddSingleton<CodeRag.Domain.Ports.IVectorDimSource>(sp => sp.GetRequiredService<IEmbeddingProvider>());

        // Per-domain dim source provider, consumed by the Infrastructure vector store factory.
        services.AddSingleton<CodeRag.Domain.Ports.IVectorDimSourceProvider, DefaultVectorDimSourceProvider>();
    }

    private static HttpClientHandler CreateHandler(string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
            return new HttpClientHandler();

        var handler = new HttpClientHandler { UseProxy = true };
        if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
            handler.Proxy = new WebProxy(proxyUri) { UseDefaultCredentials = false };
        return handler;
    }

    private static void ApplyLegacyFallback(IConfiguration cfg, EmbeddingOptions opts)
    {
        var legacyEndpoint = cfg["Embeddings:Endpoint"];
        if (string.IsNullOrWhiteSpace(legacyEndpoint))
            return;

        foreach (var profile in new[] { opts.Code, opts.Text })
        {
            if (!string.IsNullOrWhiteSpace(profile.Endpoint))
                continue; // domain explicitly configured — do not override

            profile.Endpoint = legacyEndpoint;
            profile.Model ??= cfg["Embeddings:Model"];
            if (profile.Dimensions == 0 && int.TryParse(cfg["Embeddings:Dimensions"], out var dim) && dim > 0)
                profile.Dimensions = dim;
            profile.Prefix ??= cfg["Embeddings:Prefix"];
            profile.ApiKey ??= cfg["Embeddings:ApiKey"];
            if (int.TryParse(cfg["Embeddings:BatchSize"], out var bs) && bs > 0) profile.BatchSize = bs;
            if (int.TryParse(cfg["Embeddings:MaxRetries"], out var mr) && mr >= 0) profile.MaxRetries = mr;
            if (int.TryParse(cfg["Embeddings:BaseDelay"], out var bd) && bd >= 0) profile.BaseDelay = TimeSpan.FromMilliseconds(bd);
            if (TimeSpan.TryParse(cfg["Embeddings:Timeout"], out var to) && to > TimeSpan.Zero) profile.Timeout = to;
        }
    }
}
