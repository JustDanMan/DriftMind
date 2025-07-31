using Azure.AI.OpenAI;
using OpenAI.Embeddings;

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
    private readonly string _embeddingModel = "text-embedding-ada-002";

    public EmbeddingService(AzureOpenAIClient azureOpenAIClient, ILogger<EmbeddingService> logger)
    {
        _embeddingClient = azureOpenAIClient.GetEmbeddingClient(_embeddingModel);
        _logger = logger;
    }

    public async Task<IReadOnlyList<float>> GenerateEmbeddingAsync(string text)
    {
        try
        {
            var response = await _embeddingClient.GenerateEmbeddingAsync(text);
            return response.Value.ToFloats().ToArray();
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
}
