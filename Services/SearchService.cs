using Azure;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using DriftMind.Models;

namespace DriftMind.Services;

public interface ISearchService
{
    Task InitializeIndexAsync();
    Task<bool> IndexDocumentChunksAsync(List<DocumentChunk> chunks);
    Task<SearchResults<DocumentChunk>> SearchAsync(string query, int top = 10);
    Task<SearchResults<DocumentChunk>> VectorSearchAsync(IReadOnlyList<float> queryEmbedding, int top = 10);
    Task<SearchResults<DocumentChunk>> HybridSearchAsync(string query, IReadOnlyList<float> queryEmbedding, int top = 10, string? documentId = null);
}

public class SearchService : ISearchService
{
    private readonly SearchIndexClient _indexClient;
    private readonly SearchClient _searchClient;
    private readonly ILogger<SearchService> _logger;
    private const string IndexName = "driftmind-documents";

    public SearchService(SearchIndexClient indexClient, ILogger<SearchService> logger)
    {
        _indexClient = indexClient;
        _searchClient = indexClient.GetSearchClient(IndexName);
        _logger = logger;
    }

    public async Task InitializeIndexAsync()
    {
        try
        {
            // Check if the index already exists
            try
            {
                await _indexClient.GetIndexAsync(IndexName);
                _logger.LogInformation("Index {IndexName} already exists", IndexName);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Index does not exist, create it
            }

            // Erstelle Vector Search Profile
            var vectorSearchProfile = new VectorSearchProfile("vector-profile", "vector-config");
            
            var vectorSearch = new VectorSearch
            {
                Profiles = { vectorSearchProfile },
                Algorithms = 
                {
                    new HnswAlgorithmConfiguration("vector-config")
                    {
                        Parameters = new HnswParameters
                        {
                            Metric = VectorSearchAlgorithmMetric.Cosine,
                            M = 4,
                            EfConstruction = 400,
                            EfSearch = 500
                        }
                    }
                }
            };

            // Erstelle Index
            var index = new SearchIndex(IndexName)
            {
                Fields = new FieldBuilder().Build(typeof(DocumentChunk)),
                VectorSearch = vectorSearch
            };

            await _indexClient.CreateIndexAsync(index);
            _logger.LogInformation("Index {IndexName} created successfully", IndexName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing index {IndexName}", IndexName);
            throw;
        }
    }

    public async Task<bool> IndexDocumentChunksAsync(List<DocumentChunk> chunks)
    {
        try
        {
            if (!chunks.Any())
                return true;

            var batch = IndexDocumentsBatch.Upload(chunks);
            var result = await _searchClient.IndexDocumentsAsync(batch);
            
            var successful = result.Value.Results.Count(r => r.Succeeded);
            var failed = result.Value.Results.Count(r => !r.Succeeded);
            
            _logger.LogInformation("Indexing completed: {Successful} successful, {Failed} failed", 
                successful, failed);
            
            return failed == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error indexing {ChunkCount} document chunks", chunks.Count);
            return false;
        }
    }

    public async Task<SearchResults<DocumentChunk>> SearchAsync(string query, int top = 10)
    {
        try
        {
            var searchOptions = new SearchOptions
            {
                Size = top,
                IncludeTotalCount = true
            };

            return await _searchClient.SearchAsync<DocumentChunk>(query, searchOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in text search for query: {Query}", query);
            throw;
        }
    }

    public async Task<SearchResults<DocumentChunk>> VectorSearchAsync(IReadOnlyList<float> queryEmbedding, int top = 10)
    {
        try
        {
            var embeddingArray = queryEmbedding.ToArray();
            var searchOptions = new SearchOptions
            {
                Size = top,
                VectorSearch = new()
                {
                    Queries = { new VectorizedQuery(embeddingArray) { KNearestNeighborsCount = top, Fields = { "Embedding" } } }
                }
            };

            return await _searchClient.SearchAsync<DocumentChunk>(searchOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in vector search");
            throw;
        }
    }

    public async Task<SearchResults<DocumentChunk>> HybridSearchAsync(string query, IReadOnlyList<float> queryEmbedding, int top = 10, string? documentId = null)
    {
        try
        {
            var embeddingArray = queryEmbedding.ToArray();
            var searchOptions = new SearchOptions
            {
                Size = top,
                IncludeTotalCount = true,
                VectorSearch = new()
                {
                    Queries = { new VectorizedQuery(embeddingArray) { KNearestNeighborsCount = top, Fields = { "Embedding" } } }
                }
            };

            // Add filter for DocumentId if specified
            if (!string.IsNullOrEmpty(documentId))
            {
                searchOptions.Filter = $"DocumentId eq '{documentId}'";
            }

            // Hybrid Search: Combine text search and vector search
            return await _searchClient.SearchAsync<DocumentChunk>(query, searchOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in hybrid search for query: {Query}", query);
            throw;
        }
    }
}
