using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

public interface ISearchOrchestrationService
{
    Task<SearchResponse> SearchAsync(SearchRequest request);
}

public class SearchOrchestrationService : ISearchOrchestrationService
{
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChatService _chatService;
    private readonly IQueryExpansionService _queryExpansionService;
    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly IConfiguration _configuration;

    public SearchOrchestrationService(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        IChatService chatService,
        IQueryExpansionService queryExpansionService,
        ILogger<SearchOrchestrationService> logger,
        IConfiguration configuration)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _chatService = chatService;
        _queryExpansionService = queryExpansionService;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request)
    {
        try
        {
            _logger.LogInformation("Starting search for query: {Query}", request.Query);

            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return new SearchResponse
                {
                    Query = request.Query,
                    Success = false,
                    Message = "Search query cannot be empty."
                };
            }

            // 1. Query expansion if enabled
            var searchQuery = request.Query;
            string? expandedQuery = null;
            
            if (request.EnableQueryExpansion)
            {
                expandedQuery = await _queryExpansionService.ExpandQueryAsync(request.Query, request.ChatHistory);
                if (!string.Equals(expandedQuery, request.Query, StringComparison.OrdinalIgnoreCase))
                {
                    searchQuery = expandedQuery;
                    _logger.LogInformation("Query expanded from '{OriginalQuery}' to '{ExpandedQuery}'", 
                        request.Query, expandedQuery);
                }
            }

            // 2. Generate embedding for the search query (using expanded query if available)
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(searchQuery);
            
            _logger.LogInformation("Embedding generated for search query");

            // 3. Perform hybrid search with more results for filtering
            var multiplier = searchQuery.Length < 20 ? 4 : 3; // More results for short queries
            var searchResults = request.UseSemanticSearch 
                ? await _searchService.HybridSearchAsync(searchQuery, queryEmbedding, request.MaxResults * multiplier, request.DocumentId)
                : await _searchService.SearchAsync(searchQuery, Math.Min(request.MaxResults * 2, 50));

            var resultsList = searchResults.GetResults().ToList();
            _logger.LogInformation("Search results received: {ResultCount} for query: '{Query}'", resultsList.Count, request.Query);

            // 3. Bulk load metadata for all unique documents (PERFORMANCE OPTIMIZATION)
            var uniqueDocumentIds = resultsList
                .Select(r => r.Document.DocumentId)
                .Distinct()
                .ToList();

            _logger.LogDebug("Bulk loading metadata for {DocumentCount} unique documents", uniqueDocumentIds.Count);
            var metadataBulk = await LoadMetadataBulkAsync(uniqueDocumentIds);

            // 4. Create SearchResult objects from Azure Search results
            var results = new List<SearchResult>();
            
            foreach (var result in resultsList)
            {
                double vectorScore = result.Score ?? 0.0;
                double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                    result.Document.Content, 
                    request.Query, 
                    vectorScore);

                // Get metadata from bulk-loaded cache (O(1) lookup!)
                metadataBulk.TryGetValue(result.Document.DocumentId, out var metadata);

                results.Add(new SearchResult
                {
                    Id = result.Document.Id,
                    Content = result.Document.Content,
                    DocumentId = result.Document.DocumentId,
                    ChunkIndex = result.Document.ChunkIndex,
                    Score = combinedScore,
                    VectorScore = vectorScore,
                    Metadata = result.Document.Metadata,
                    CreatedAt = result.Document.CreatedAt,
                    // Use bulk-loaded metadata or fallback to current chunk metadata
                    BlobPath = metadata?.BlobPath ?? result.Document.BlobPath,
                    BlobContainer = metadata?.BlobContainer ?? result.Document.BlobContainer,
                    OriginalFileName = metadata?.OriginalFileName ?? result.Document.OriginalFileName,
                    ContentType = metadata?.ContentType ?? result.Document.ContentType,
                    TextContentBlobPath = metadata?.TextContentBlobPath ?? result.Document.TextContentBlobPath,
                    FileSizeBytes = metadata?.FileSizeBytes ?? result.Document.FileSizeBytes
                    // Download: Use POST /download/token with documentId if OriginalFileName != null
                });
            }
            
            // 5. Apply simplified score-based filtering
            var filteredResults = FilterResults(results, request);

            _logger.LogInformation("Filtered {Original} results to {Relevant} relevant results for query: '{Query}'", 
                results.Count, filteredResults.Count, request.Query);

            // 5. Apply source diversification (max 1 chunk per document) and limit to MaxSourcesForAnswer
            var maxSources = _configuration.GetValue<int>("ChatService:MaxSourcesForAnswer", 5);
            var diversifiedResults = filteredResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                .OrderByDescending(r => r.Score)
                .Take(Math.Min(request.MaxResults, maxSources)) // Limit by both MaxResults and MaxSourcesForAnswer
                .ToList();

            _logger.LogInformation("Diversified {Original} chunks to {Diversified} chunks from {Documents} different documents (limited to {MaxSources} sources)", 
                filteredResults.Count, diversifiedResults.Count, diversifiedResults.Select(r => r.DocumentId).Distinct().Count(), maxSources);

            var response = new SearchResponse
            {
                Query = request.Query,
                ExpandedQuery = expandedQuery != request.Query ? expandedQuery : null,
                Results = diversifiedResults,
                Success = true,
                TotalResults = diversifiedResults.Count
            };

            // 6. Generate answer with diversified results
            if (request.IncludeAnswer && diversifiedResults.Any())
            {
                // Use history-aware method if chat history is provided
                if (request.ChatHistory?.Any() == true)
                {
                    response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                        request.Query, diversifiedResults, request.ChatHistory);
                }
                else
                {
                    response.GeneratedAnswer = await _chatService.GenerateAnswerAsync(request.Query, diversifiedResults);
                }
            }
            else if (request.IncludeAnswer && !diversifiedResults.Any())
            {
                // If no search results but we have chat history, try to answer from history
                if (request.ChatHistory?.Any() == true)
                {
                    response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                        request.Query, new List<SearchResult>(), request.ChatHistory);
                }
                else
                {
                    response.GeneratedAnswer = "Es konnten keine relevanten Informationen zu Ihrer Frage gefunden werden. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
                }
            }

            _logger.LogInformation("Search completed successfully");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during search for query: {Query}", request.Query);
            return new SearchResponse
            {
                Query = request.Query,
                Success = false,
                Message = $"Error during search: {ex.Message}"
            };
        }
    }

    private List<SearchResult> FilterResults(List<SearchResult> results, SearchRequest request)
    {
        var minScore = _configuration.GetValue<double>("ChatService:MinScoreForAnswer", 0.25);
        
        _logger.LogDebug("Filtering {ResultCount} results for query: '{Query}' using MinScore: {MinScore}", 
            results.Count, request.Query, minScore);

        var filteredResults = new List<SearchResult>();

        foreach (var result in results)
        {
            // SIMPLE: Only one criterion - score threshold
            var shouldInclude = result.Score >= minScore;

            if (shouldInclude)
            {
                filteredResults.Add(result);
                _logger.LogDebug("Included result: Score={Score:F3}, Content preview: {ContentPreview}", 
                    result.Score, result.Content.Length > 50 ? result.Content.Substring(0, 50) + "..." : result.Content);
            }
            else
            {
                _logger.LogDebug("Rejected result: Score={Score:F3} (required: ≥{MinScore})", 
                    result.Score, minScore);
            }
        }

        var finalResults = filteredResults
            .OrderByDescending(r => r.Score)
            .Take(request.MaxResults)
            .ToList();

        _logger.LogInformation("Filtered {OriginalCount} results to {FilteredCount} relevant results for query: '{Query}'", 
            results.Count, finalResults.Count, request.Query);

        return finalResults;
    }

    /// <summary>
    /// Ensures document metadata is available by fetching from chunk 0 if current chunk doesn't have it
    /// </summary>
    private async Task<DocumentMetadata> EnsureDocumentMetadataAsync(DocumentChunk chunk)
    {
        // If current chunk has metadata, use it
        if (!string.IsNullOrEmpty(chunk.OriginalFileName))
        {
            return new DocumentMetadata
            {
                BlobPath = chunk.BlobPath,
                BlobContainer = chunk.BlobContainer,
                OriginalFileName = chunk.OriginalFileName,
                ContentType = chunk.ContentType,
                TextContentBlobPath = chunk.TextContentBlobPath,
                FileSizeBytes = chunk.FileSizeBytes
            };
        }

        // Otherwise, get metadata from chunk 0 of the same document
        try
        {
            var chunks = await _searchService.GetDocumentChunksAsync(chunk.DocumentId);
            var firstChunk = chunks.FirstOrDefault(c => c.ChunkIndex == 0);
            
            return new DocumentMetadata
            {
                BlobPath = firstChunk?.BlobPath,
                BlobContainer = firstChunk?.BlobContainer,
                OriginalFileName = firstChunk?.OriginalFileName,
                ContentType = firstChunk?.ContentType,
                TextContentBlobPath = firstChunk?.TextContentBlobPath,
                FileSizeBytes = firstChunk?.FileSizeBytes
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve metadata from chunk 0 for document {DocumentId}", chunk.DocumentId);
            
            // Return empty metadata if we can't get it
            return new DocumentMetadata();
        }
    }

    /// <summary>
    /// Bulk loads metadata for multiple documents in a single API call (PERFORMANCE OPTIMIZATION)
    /// This replaces N individual calls with 1 bulk call, reducing latency by 80-90%
    /// </summary>
    private async Task<Dictionary<string, DocumentMetadata>> LoadMetadataBulkAsync(List<string> documentIds)
    {
        if (!documentIds.Any())
        {
            return new Dictionary<string, DocumentMetadata>();
        }

        try
        {
            _logger.LogDebug("Bulk loading metadata for {DocumentCount} documents", documentIds.Count);

            // Single API call to get all chunk-0s at once
            var chunk0s = await _searchService.GetChunk0sForDocumentsAsync(documentIds);

            // Convert to dictionary for O(1) lookups
            var metadataDict = chunk0s.ToDictionary(
                chunk => chunk.DocumentId,
                chunk => new DocumentMetadata
                {
                    BlobPath = chunk.BlobPath,
                    BlobContainer = chunk.BlobContainer,
                    OriginalFileName = chunk.OriginalFileName,
                    ContentType = chunk.ContentType,
                    TextContentBlobPath = chunk.TextContentBlobPath,
                    FileSizeBytes = chunk.FileSizeBytes
                });

            _logger.LogDebug("Bulk loaded metadata for {Found}/{Requested} documents", 
                metadataDict.Count, documentIds.Count);

            return metadataDict;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error bulk loading metadata for {DocumentCount} documents", documentIds.Count);
            return new Dictionary<string, DocumentMetadata>();
        }
    }

    /// <summary>
    /// Helper class to hold document metadata
    /// </summary>
    private class DocumentMetadata
    {
        public string? BlobPath { get; set; }
        public string? BlobContainer { get; set; }
        public string? OriginalFileName { get; set; }
        public string? ContentType { get; set; }
        public string? TextContentBlobPath { get; set; }
        public long? FileSizeBytes { get; set; }
    }
}
