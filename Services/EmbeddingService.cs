using Azure.AI.OpenAI;
using OpenAI.Embeddings;
using Microsoft.Extensions.Caching.Memory;

namespace DriftMind.Services;

public interface IEmbeddingService
{
    Task<IReadOnlyList<float>> GenerateEmbeddingAsync(string text);
    Task<List<IReadOnlyList<float>>> GenerateEmbeddingsAsync(List<string> texts);
}

public class EmbeddingService : IEmbeddingService
{
    private readonly EmbeddingClient _embeddingClient;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly IMemoryCache _cache;
    private readonly string _embeddingModel = "text-embedding-ada-002";

    public EmbeddingService(AzureOpenAIClient azureOpenAIClient, ILogger<EmbeddingService> logger, IMemoryCache cache)
    {
        _embeddingClient = azureOpenAIClient.GetEmbeddingClient(_embeddingModel);
        _logger = logger;
        _cache = cache;
    }

    public async Task<IReadOnlyList<float>> GenerateEmbeddingAsync(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("Text cannot be null or empty", nameof(text));

            // Normalize text for better cache hits
            var normalizedText = NormalizeForCaching(text);
            var cacheKey = $"embedding_{normalizedText.GetHashCode():X}";

            // Check cache first
            if (_cache.TryGetValue(cacheKey, out var cachedEmbedding) && cachedEmbedding is IReadOnlyList<float> cachedResult)
            {
                _logger.LogDebug("Cache hit for embedding query: {Query}", text);
                return cachedResult;
            }

            // Generate embedding if not cached
            _logger.LogDebug("Cache miss, generating embedding for: {Query}", text);
            var response = await _embeddingClient.GenerateEmbeddingAsync(text);
            var newEmbedding = response.Value.ToFloats().ToArray();

            // Cache with intelligent expiration
            var cacheOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(2),
                SlidingExpiration = TimeSpan.FromMinutes(30),
                Priority = CacheItemPriority.High,
                Size = 1
            };

            _cache.Set(cacheKey, newEmbedding, cacheOptions);
            _logger.LogDebug("Cached embedding for query: {Query}", text);

            return newEmbedding;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embedding for text: {Text}", text.Substring(0, Math.Min(100, text.Length)));
            throw;
        }
    }

    public async Task<List<IReadOnlyList<float>>> GenerateEmbeddingsAsync(List<string> texts)
    {
        try
        {
            var embeddings = new List<IReadOnlyList<float>>();
            
            // Batch processing for better performance
            const int batchSize = 10;
            for (int i = 0; i < texts.Count; i += batchSize)
            {
                var batch = texts.Skip(i).Take(batchSize);
                var tasks = batch.Select(text => GenerateEmbeddingAsync(text));
                var batchEmbeddings = await Task.WhenAll(tasks);
                embeddings.AddRange(batchEmbeddings);
            }
            
            return embeddings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating embeddings for {Count} texts", texts.Count);
            throw;
        }
    }

    // Normalization method for better cache hits
    private static string NormalizeForCaching(string text)
    {
        return text
            .Trim()                     // Remove whitespace
            .ToLowerInvariant()        // Lowercase
            .Replace("  ", " ")        // Remove double spaces
            .Replace("\n", " ")        // Newlines to spaces
            .Replace("\r", "")         // Remove carriage returns
            .Replace("\t", " ");       // Tabs to spaces
    }
}
