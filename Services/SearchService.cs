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
    Task<List<string>> GetAllDocumentIdsAsync();
    Task<List<DocumentChunk>> GetDocumentChunksAsync(string documentId);
    Task<List<DocumentChunk>> GetAdjacentChunksAsync(string documentId, int chunkIndex, int adjacentCount);
    Task<Dictionary<string, List<DocumentChunk>>> GetAllDocumentsAsync(int maxResults = 50, int skip = 0);
    Task<bool> DeleteDocumentAsync(string documentId);
    Task<List<DocumentChunk>> GetChunk0sForDocumentsAsync(List<string> documentIds);
    Task<bool> DocumentExistsAsync(string documentId);
    // Efficient, minimal queries for overview
    Task<int> GetChunkCountAsync(string documentId);
    Task<DateTimeOffset?> GetLastUpdatedAsync(string documentId);
    Task<List<DocumentChunk>> GetTopChunksAsync(string documentId, int topN = 3);
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
                var existingIndex = await _indexClient.GetIndexAsync(IndexName);
                
                // Check if the index has the new blob storage fields
                var hasBlobPath = existingIndex.Value.Fields.Any(f => f.Name == "BlobPath");
                var hasTextContentBlobPath = existingIndex.Value.Fields.Any(f => f.Name == "TextContentBlobPath");
                var hasFileSizeBytes = existingIndex.Value.Fields.Any(f => f.Name == "FileSizeBytes");
                
                if (!hasBlobPath || !hasTextContentBlobPath || !hasFileSizeBytes)
                {
                    _logger.LogInformation("Index {IndexName} exists but missing some required fields. Updating...", IndexName);
                    await UpdateIndexWithBlobFieldsAsync();
                }
                else
                {
                    _logger.LogInformation("Index {IndexName} already exists with all required fields", IndexName);
                }
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Index does not exist, create it
                _logger.LogInformation("Index {IndexName} does not exist. Creating...", IndexName);
            }

            // Create Vector Search Profile
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

            // Create Index
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

    private async Task UpdateIndexWithBlobFieldsAsync()
    {
        try
        {
            var existingIndex = await _indexClient.GetIndexAsync(IndexName);
            var index = existingIndex.Value;

            // Add new blob storage fields if they don't exist
            var fieldsToAdd = new List<SearchField>();

            if (!index.Fields.Any(f => f.Name == "BlobPath"))
            {
                fieldsToAdd.Add(new SearchField("BlobPath", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = false,
                    IsFacetable = false
                });
            }

            if (!index.Fields.Any(f => f.Name == "BlobContainer"))
            {
                fieldsToAdd.Add(new SearchField("BlobContainer", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = false,
                    IsFacetable = false
                });
            }

            if (!index.Fields.Any(f => f.Name == "OriginalFileName"))
            {
                fieldsToAdd.Add(new SearchField("OriginalFileName", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = false,
                    IsFacetable = false
                });
            }

            if (!index.Fields.Any(f => f.Name == "ContentType"))
            {
                fieldsToAdd.Add(new SearchField("ContentType", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = false,
                    IsFacetable = false
                });
            }

            if (!index.Fields.Any(f => f.Name == "TextContentBlobPath"))
            {
                fieldsToAdd.Add(new SearchField("TextContentBlobPath", SearchFieldDataType.String)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = false,
                    IsFacetable = false
                });
            }

            if (!index.Fields.Any(f => f.Name == "FileSizeBytes"))
            {
                fieldsToAdd.Add(new SearchField("FileSizeBytes", SearchFieldDataType.Int64)
                {
                    IsFilterable = true,
                    IsSearchable = false,
                    IsSortable = true,
                    IsFacetable = false
                });
            }

            if (fieldsToAdd.Any())
            {
                // Add the new fields to the existing index
                foreach (var field in fieldsToAdd)
                {
                    index.Fields.Add(field);
                }

                await _indexClient.CreateOrUpdateIndexAsync(index);
                _logger.LogInformation("Index {IndexName} updated with {FieldCount} new blob storage fields", 
                    IndexName, fieldsToAdd.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating index {IndexName} with blob storage fields", IndexName);
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
                Size = Math.Min(top * 3, 100), // Get more results for better filtering
                IncludeTotalCount = true,
                VectorSearch = new()
                {
                    Queries = { new VectorizedQuery(embeddingArray) { KNearestNeighborsCount = Math.Min(top * 3, 100), Fields = { "Embedding" } } }
                }
            };

            // Add filter for DocumentId if specified
            if (!string.IsNullOrEmpty(documentId))
            {
                searchOptions.Filter = $"DocumentId eq '{documentId}'";
            }

            _logger.LogDebug("Performing hybrid search for query: {Query} with top: {Top}", query, top);

            // Hybrid Search: Combine text search and vector search
            return await _searchClient.SearchAsync<DocumentChunk>(query, searchOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in hybrid search for query: {Query}", query);
            throw;
        }
    }

    public async Task<List<string>> GetAllDocumentIdsAsync()
    {
        try
        {
            _logger.LogInformation("Retrieving all document IDs");

            var searchOptions = new SearchOptions
            {
                Size = 1000, // Large number to get all documents
                Select = { "DocumentId" },
                IncludeTotalCount = false
            };

            var searchResults = await _searchClient.SearchAsync<DocumentChunk>("*", searchOptions);
            var documentIds = new HashSet<string>();

            await foreach (var result in searchResults.Value.GetResultsAsync())
            {
                if (!string.IsNullOrEmpty(result.Document.DocumentId))
                {
                    documentIds.Add(result.Document.DocumentId);
                }
            }

            _logger.LogInformation("Found {Count} unique document IDs", documentIds.Count);
            return documentIds.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving document IDs");
            throw;
        }
    }

    public async Task<List<DocumentChunk>> GetDocumentChunksAsync(string documentId)
    {
        try
        {
            _logger.LogInformation("Retrieving chunks for document: {DocumentId}", documentId);

            var searchOptions = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}'",
                OrderBy = { "ChunkIndex asc" },
                Size = 1000 // Large number to get all chunks
            };

            var searchResults = await _searchClient.SearchAsync<DocumentChunk>("*", searchOptions);
            var chunks = new List<DocumentChunk>();

            await foreach (var result in searchResults.Value.GetResultsAsync())
            {
                chunks.Add(result.Document);
            }

            _logger.LogInformation("Retrieved {Count} chunks for document {DocumentId}", chunks.Count, documentId);
            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving chunks for document: {DocumentId}", documentId);
            throw;
        }
    }

    public async Task<List<DocumentChunk>> GetAdjacentChunksAsync(string documentId, int chunkIndex, int adjacentCount)
    {
        try
        {
            _logger.LogDebug("Retrieving adjacent chunks for document: {DocumentId}, chunk: {ChunkIndex}, adjacentCount: {AdjacentCount}", 
                documentId, chunkIndex, adjacentCount);

            var minChunkIndex = Math.Max(0, chunkIndex - adjacentCount);
            var maxChunkIndex = chunkIndex + adjacentCount;

            var searchOptions = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}' and ChunkIndex ge {minChunkIndex} and ChunkIndex le {maxChunkIndex}",
                OrderBy = { "ChunkIndex asc" },
                Size = (adjacentCount * 2) + 1 // Target chunk + before + after
            };

            var searchResults = await _searchClient.SearchAsync<DocumentChunk>("*", searchOptions);
            var chunks = new List<DocumentChunk>();

            await foreach (var result in searchResults.Value.GetResultsAsync())
            {
                chunks.Add(result.Document);
            }

            _logger.LogDebug("Retrieved {Count} adjacent chunks for document {DocumentId}, chunk {ChunkIndex}", 
                chunks.Count, documentId, chunkIndex);
            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving adjacent chunks for document: {DocumentId}, chunk: {ChunkIndex}", 
                documentId, chunkIndex);
            throw;
        }
    }

    public async Task<Dictionary<string, List<DocumentChunk>>> GetAllDocumentsAsync(int maxResults = 50, int skip = 0)
    {
        try
        {
            _logger.LogInformation("Retrieving all documents with pagination (max: {MaxResults}, skip: {Skip})", 
                maxResults, skip);

            // First, get unique document IDs with pagination
            var documentIds = await GetAllDocumentIdsAsync();
            var paginatedDocumentIds = documentIds.Skip(skip).Take(maxResults).ToList();

            var allDocuments = new Dictionary<string, List<DocumentChunk>>();

            // Get chunks for each document
            foreach (var documentId in paginatedDocumentIds)
            {
                var chunks = await GetDocumentChunksAsync(documentId);
                allDocuments[documentId] = chunks;
            }

            _logger.LogInformation("Retrieved {Count} documents with their chunks", allDocuments.Count);
            return allDocuments;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving all documents");
            throw;
        }
    }

    public async Task<bool> DeleteDocumentAsync(string documentId)
    {
        try
        {
            _logger.LogInformation("Deleting all chunks for document: {DocumentId}", documentId);

            // First, get all chunks for this document to count them
            var chunksToDelete = await GetDocumentChunksAsync(documentId);
            
            if (!chunksToDelete.Any())
            {
                _logger.LogWarning("No chunks found for document {DocumentId} to delete", documentId);
                return true; // Not an error if document doesn't exist
            }

            // Create a batch of delete actions
            var deleteActions = chunksToDelete.Select(chunk => 
                IndexDocumentsAction.Delete(chunk)).ToArray();

            // Execute the batch delete
            var response = await _searchClient.IndexDocumentsAsync(
                IndexDocumentsBatch.Create(deleteActions));

            var successfulDeletes = response.Value.Results.Count(r => r.Succeeded);
            var totalChunks = chunksToDelete.Count;

            if (successfulDeletes == totalChunks)
            {
                _logger.LogInformation("Successfully deleted {Count} chunks for document {DocumentId}", 
                    successfulDeletes, documentId);
                return true;
            }
            else
            {
                _logger.LogWarning("Partially deleted document {DocumentId}: {Success}/{Total} chunks", 
                    documentId, successfulDeletes, totalChunks);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document: {DocumentId}", documentId);
            return false;
        }
    }

    public async Task<List<DocumentChunk>> GetChunk0sForDocumentsAsync(List<string> documentIds)
    {
        if (!documentIds.Any())
        {
            return new List<DocumentChunk>();
        }

        try
        {
            _logger.LogDebug("Bulk loading metadata for {DocumentCount} documents", documentIds.Count);

            // Create filter for all DocumentIDs with ChunkIndex = 0
            var filterConditions = documentIds.Select(id => 
                $"(DocumentId eq '{id}' and ChunkIndex eq 0)");
            var filter = string.Join(" or ", filterConditions);

            // Create search options for bulk metadata retrieval
            var searchOptions = new SearchOptions
            {
                Filter = filter,
                Size = documentIds.Count,
                Select = { 
                    "Id", "DocumentId", "OriginalFileName", "ContentType", "FileSizeBytes", 
                    "BlobPath", "BlobContainer", "TextContentBlobPath", "CreatedAt", "Metadata"
                }
            };

            _logger.LogDebug("Executing bulk metadata query with filter: {Filter}", filter);

            var response = await _searchClient.SearchAsync<DocumentChunk>("*", searchOptions);
            var results = response.Value.GetResults()
                .Select(r => r.Document)
                .ToList();

            _logger.LogInformation("Bulk loaded metadata for {Found}/{Requested} documents", 
                results.Count, documentIds.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk loading metadata for {DocumentCount} documents", documentIds.Count);
            
            // Fallback: Return empty list (SearchOrchestrationService will handle missing metadata)
            return new List<DocumentChunk>();
        }
    }

    public async Task<bool> DocumentExistsAsync(string documentId)
    {
        try
        {
            // Fast existence check: look for any chunk with ChunkIndex = 0 for the document
            var searchOptions = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}' and ChunkIndex eq 0",
                Size = 1,
                Select = { "Id" }
            };

            var response = await _searchClient.SearchAsync<DocumentChunk>("*", searchOptions);
            await foreach (var _ in response.Value.GetResultsAsync())
            {
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking existence for document {DocumentId}", documentId);
            throw;
        }
    }

    public async Task<int> GetChunkCountAsync(string documentId)
    {
        try
        {
            var options = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}'",
                IncludeTotalCount = true,
                Size = 1,
                Select = { "Id" }
            };

            var response = await _searchClient.SearchAsync<DocumentChunk>("*", options);
            var total = response.Value.TotalCount ?? 0;
            return (int)total;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting chunk count for document {DocumentId}", documentId);
            throw;
        }
    }

    public async Task<DateTimeOffset?> GetLastUpdatedAsync(string documentId)
    {
        try
        {
            var options = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}'",
                OrderBy = { "CreatedAt desc" },
                Size = 1,
                Select = { "CreatedAt" }
            };

            var response = await _searchClient.SearchAsync<DocumentChunk>("*", options);
            await foreach (var r in response.Value.GetResultsAsync())
            {
                return r.Document.CreatedAt;
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting last updated for document {DocumentId}", documentId);
            throw;
        }
    }

    public async Task<List<DocumentChunk>> GetTopChunksAsync(string documentId, int topN = 3)
    {
        try
        {
            var maxIndex = Math.Max(0, topN - 1);
            var options = new SearchOptions
            {
                Filter = $"DocumentId eq '{documentId}' and ChunkIndex ge 0 and ChunkIndex le {maxIndex}",
                OrderBy = { "ChunkIndex asc" },
                Size = topN,
                Select = { "Content", "ChunkIndex" }
            };

            var response = await _searchClient.SearchAsync<DocumentChunk>("*", options);
            var chunks = new List<DocumentChunk>();
            await foreach (var r in response.Value.GetResultsAsync())
            {
                chunks.Add(r.Document);
            }
            return chunks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting top chunks for document {DocumentId}", documentId);
            throw;
        }
    }
}
