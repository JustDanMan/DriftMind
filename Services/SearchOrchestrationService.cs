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
    private readonly ISearchHistoryService _searchHistoryService;
    private readonly IEmbeddingService _embeddingService;
    private readonly IChatService _chatService;
    private readonly IQueryExpansionService _queryExpansionService;
    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly IConfiguration _configuration;

    public SearchOrchestrationService(
        ISearchService searchService,
        ISearchHistoryService searchHistoryService,
        IEmbeddingService embeddingService,
        IChatService chatService,
        IQueryExpansionService queryExpansionService,
        ILogger<SearchOrchestrationService> logger,
        IConfiguration configuration)
    {
        _searchService = searchService;
        _searchHistoryService = searchHistoryService;
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

            // Check for follow-up questions first
            bool isFollowUp = FollowUpQuestionAnalyzer.IsFollowUpQuestion(request.Query, _logger);
            if (isFollowUp && request.ChatHistory?.Any() == true)
            {
                _logger.LogInformation("Detected follow-up question: '{Query}' - searching within previous context", request.Query);

                // Extract document IDs from the last assistant response with sources
                var previousDocumentIds = ChatHistoryAnalyzer.ExtractDocumentIds(request.ChatHistory, _logger);

                if (previousDocumentIds.Any())
                {
                    _logger.LogInformation("Found {Count} document IDs from previous responses: {DocumentIds}",
                        previousDocumentIds.Count, string.Join(", ", previousDocumentIds));

                    // Search within these specific documents only
                    return await SearchWithinSpecificDocuments(request, previousDocumentIds);
                }
                else
                {
                    _logger.LogWarning("No document IDs found in chat history for follow-up question");
                }
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

            // Bulk load metadata for all unique documents (PERFORMANCE OPTIMIZATION)
            var uniqueDocumentIds = resultsList
                .Select(r => r.Document.DocumentId)
                .Distinct()
                .ToList();

            var metadataBulk = await LoadMetadataBulkAsync(uniqueDocumentIds);            // 4. Create SearchResult objects from Azure Search results
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

            // For potential follow-up scenarios, preserve more documents
            // Check if this looks like a first question that might have follow-ups
            bool mightHaveFollowUps = request.ChatHistory?.Any() != true || // First question
                                     filteredResults.Select(r => r.DocumentId).Distinct().Count() > 1; // Multiple relevant docs

            if (mightHaveFollowUps)
            {
                // Use higher limit to preserve more documents for potential follow-ups
                maxSources = Math.Max(maxSources, Math.Min(10, filteredResults.Select(r => r.DocumentId).Distinct().Count()));
                _logger.LogInformation("🔄 PRESERVING SOURCES: Increased maxSources to {MaxSources} for potential follow-ups", maxSources);
            }

            var diversifiedResults = filteredResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                .OrderByDescending(r => r.Score)
                .Take(Math.Min(request.MaxResults, maxSources)) // Limit by both MaxResults and MaxSourcesForAnswer
                .ToList();

            _logger.LogInformation("Diversified {Original} chunks to {Diversified} chunks from {Documents} different documents (limited to {MaxSources} sources)",
                filteredResults.Count, diversifiedResults.Count, diversifiedResults.Select(r => r.DocumentId).Distinct().Count(), maxSources);

            var contextualResults = diversifiedResults ?? new List<SearchResult>();

            if (request.ChatHistory?.Any() == true)
            {
                bool isFollowUpInContext = FollowUpQuestionAnalyzer.IsFollowUpQuestion(request.Query, _logger);
                bool isRelatedTopic = !isFollowUpInContext && await _searchHistoryService.IsRelatedTopicQuestionAsync(request.Query, request.ChatHistory);

                _logger.LogInformation("Search strategy analysis - IsFollowUp: {IsFollowUp}, IsRelatedTopic: {IsRelatedTopic}, Query: '{Query}'",
                    isFollowUpInContext, isRelatedTopic, request.Query);

                if (isFollowUp)
                {
                    bool hasGoodCurrentResults = contextualResults.Any();

                    if (!hasGoodCurrentResults)
                    {
                        var previousResults = await _searchHistoryService.ExtractPreviousSearchResultsFromHistory(request.ChatHistory);
                        if (previousResults?.Any() == true)
                        {
                            _logger.LogInformation("🔄 FOLLOW-UP PRIORITY: Using {Count} previous search results from chat history",
                                previousResults.Count);
                            contextualResults = previousResults;
                        }
                        else
                        {
                            _logger.LogInformation("🔄 FOLLOW-UP FALLBACK: No good current or previous results found");
                        }
                    }
                    else
                    {
                        _logger.LogInformation("🔄 FOLLOW-UP CONTEXT: Using {Count} current search results as context",
                            contextualResults.Count);
                    }
                }
                else if (isRelatedTopic)
                {
                    _logger.LogInformation("Related topic detected, keeping diversified results as baseline context");
                }
            }

            var enhancedResults = await _searchHistoryService.RunEnhancedSearchAsync(request, searchQuery, queryEmbedding, contextualResults);
            _logger.LogInformation("Enhanced search returned {EnhancedCount} results", enhancedResults.Count);

            var finalResults = MergeSearchResults(contextualResults, enhancedResults, request.MaxResults);

            var response = new SearchResponse
            {
                Query = request.Query,
                ExpandedQuery = expandedQuery != request.Query ? expandedQuery : null,
                Results = finalResults,
                Success = true,
                TotalResults = finalResults.Count
            };

            // 7. Generate answer with final results
            if (request.IncludeAnswer && finalResults.Any())
            {
                // Use history-aware method if chat history is provided
                if (request.ChatHistory?.Any() == true)
                {
                    response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                        request.Query, finalResults, request.ChatHistory);
                }
                else
                {
                    response.GeneratedAnswer = await _chatService.GenerateAnswerAsync(request.Query, finalResults);
                }
                response.Results = finalResults;
            }
            else if (request.IncludeAnswer && !finalResults.Any())
            {
                // If no search results but we have chat history, try enhanced search with history context
                if (request.ChatHistory?.Any() == true)
                {
                    // Only try enhanced search if the chat history contains substantial document-related keywords
                    var historyKeywords = ChatHistoryAnalyzer.ExtractKeywords(request.ChatHistory);
                    if (historyKeywords.Count >= 2) // Require at least 2 meaningful keywords from document discussions
                    {
                        var supplementalResults = await _searchHistoryService.RunEnhancedSearchAsync(request, searchQuery, queryEmbedding);
                        if (supplementalResults.Any())
                        {
                            _logger.LogInformation("Enhanced search with history found {ResultCount} results using keywords: {Keywords}",
                                supplementalResults.Count, string.Join(", ", historyKeywords.Take(5)));
                            response.Results = supplementalResults;
                            response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                                request.Query, supplementalResults, request.ChatHistory);
                        }
                        else
                        {
                            // Fallback to history-only answer
                            response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                                request.Query, new List<SearchResult>(), request.ChatHistory);
                        }
                    }
                    else
                    {
                        // Not enough document-related context in history - use history-only answer
                        _logger.LogInformation("Insufficient document-related keywords in chat history ({Count}), using history-only approach", historyKeywords.Count);
                        response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                            request.Query, new List<SearchResult>(), request.ChatHistory);
                    }
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
        var minScore = _configuration.GetValue<double>("ChatService:MinScoreForAnswer", 0.15);

        // For follow-up questions, use an even more lenient threshold
        bool isFollowUp = FollowUpQuestionAnalyzer.IsFollowUpQuestion(request.Query);
        if (isFollowUp)
        {
            minScore = 0.05; // Very low threshold for follow-up questions to capture relevant context
        }

        var filteredResults = new List<SearchResult>();

        foreach (var result in results)
        {
            // SIMPLE: Only one criterion - score threshold
            var shouldInclude = result.Score >= minScore;

            if (shouldInclude)
            {
                filteredResults.Add(result);
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
    /// Merges base results with enhanced search output, prioritizing higher scores
    /// </summary>
    private List<SearchResult> MergeSearchResults(List<SearchResult> baseResults, List<SearchResult> enhancedResults, int maxResults)
    {
        var combinedResults = new List<SearchResult>();
        var seenDocuments = new HashSet<string>();

        // First, add history-based results (they get strong priority for follow-up questions)
        foreach (var result in enhancedResults.OrderByDescending(r => r.Score))
        {
            if (!seenDocuments.Contains(result.DocumentId))
            {
                _logger.LogInformation("History result added: DocumentId={DocumentId}, Score={Score:F3}, OriginalFileName={OriginalFileName}",
                    result.DocumentId, result.Score, result.OriginalFileName ?? "[NULL]");

                combinedResults.Add(result);
                seenDocuments.Add(result.DocumentId);
            }
        }

        // Then add normal results that aren't already included, but only if we have space
        foreach (var result in baseResults.OrderByDescending(r => r.Score))
        {
            if (!seenDocuments.Contains(result.DocumentId) && combinedResults.Count < maxResults)
            {
                combinedResults.Add(result);
                seenDocuments.Add(result.DocumentId);
            }
        }

        // Return top results sorted by score
        return combinedResults
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Searches within specific documents identified from previous responses
    /// </summary>
    private async Task<SearchResponse> SearchWithinSpecificDocuments(SearchRequest request, List<string> previousFilenames)
    {
        try
        {
            _logger.LogInformation("Attempting to search within previous context documents: {Filenames} for query: '{Query}'",
                string.Join(", ", previousFilenames), request.Query);

            // Generate embedding for the search query
            var searchQuery = request.Query;
            if (request.EnableQueryExpansion)
            {
                var expandedQuery = await _queryExpansionService.ExpandQueryAsync(request.Query, request.ChatHistory);
                if (!string.Equals(expandedQuery, request.Query, StringComparison.OrdinalIgnoreCase))
                {
                    searchQuery = expandedQuery;
                    _logger.LogInformation("Query expanded for context search: '{ExpandedQuery}'", expandedQuery);
                }
            }

            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(searchQuery);

            // Search with expanded results to catch documents
            var searchResults = await _searchService.HybridSearchAsync(searchQuery, queryEmbedding, request.MaxResults * 5);
            var resultsList = searchResults.GetResults().ToList();

            var results = new List<SearchResult>();
            var metadataDict = await LoadMetadataBulkAsync(resultsList.Select(r => r.Document.DocumentId).Distinct().ToList());

            foreach (var result in resultsList)
            {
                double vectorScore = result.Score ?? 0.0;
                double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                    result.Document.Content,
                    request.Query,
                    vectorScore);

                // Get metadata 
                metadataDict.TryGetValue(result.Document.DocumentId, out var metadata);
                var originalFileName = metadata?.OriginalFileName ?? result.Document.OriginalFileName;

                // Check if this result is from one of the previous filenames
                bool isFromPreviousContext = false;
                if (!string.IsNullOrEmpty(originalFileName))
                {
                    isFromPreviousContext = previousFilenames.Any(fn =>
                        originalFileName.Contains(fn, StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains(originalFileName, StringComparison.OrdinalIgnoreCase));
                }

                if (isFromPreviousContext)
                {
                    // Boost score for documents from previous context
                    combinedScore = Math.Max(combinedScore, 0.15);

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
                        BlobPath = metadata?.BlobPath ?? result.Document.BlobPath,
                        BlobContainer = metadata?.BlobContainer ?? result.Document.BlobContainer,
                        OriginalFileName = originalFileName,
                        ContentType = metadata?.ContentType ?? result.Document.ContentType,
                        TextContentBlobPath = metadata?.TextContentBlobPath ?? result.Document.TextContentBlobPath,
                        FileSizeBytes = metadata?.FileSizeBytes ?? result.Document.FileSizeBytes
                    });

                    _logger.LogInformation("Found result from previous context: '{FileName}', Score: {Score:F3}",
                        originalFileName, combinedScore);
                }
            }

            if (!results.Any())
            {
                _logger.LogWarning("No results found in previous context documents, falling back to enhanced search pipeline");
                return await ExecuteEnhancedFallbackAsync(request);
            }

            // Apply filtering with lenient threshold for follow-up
            var filteredResults = FilterResults(results, request);

            _logger.LogInformation("Found {Count} results in previous context documents after filtering", filteredResults.Count);

            return new SearchResponse
            {
                Query = request.Query,
                Results = filteredResults,
                Success = true,
                TotalResults = results.Count,
                Message = $"Found {filteredResults.Count} results in previous context documents"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching within specific documents for follow-up question");
            return await ExecuteEnhancedFallbackAsync(request);
        }
    }

    /// <summary>
    /// Executes the enhanced search pipeline as a fallback within follow-up flows
    /// </summary>
    private async Task<SearchResponse> ExecuteEnhancedFallbackAsync(SearchRequest request)
    {
        var fallbackQuery = request.Query;
        if (request.EnableQueryExpansion)
        {
            var expandedQuery = await _queryExpansionService.ExpandQueryAsync(request.Query, request.ChatHistory);
            if (!string.Equals(expandedQuery, request.Query, StringComparison.OrdinalIgnoreCase))
            {
                fallbackQuery = expandedQuery;
                _logger.LogInformation("Fallback query expanded from '{Original}' to '{Expanded}'", request.Query, expandedQuery);
            }
        }

        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(fallbackQuery);
        var searchResults = await _searchService.HybridSearchAsync(fallbackQuery, queryEmbedding, request.MaxResults * 3);
        var resultsList = searchResults.GetResults().ToList();

        var results = new List<SearchResult>();
        var metadataDict = await LoadMetadataBulkAsync(resultsList.Select(r => r.Document.DocumentId).Distinct().ToList());

        foreach (var result in resultsList)
        {
            double vectorScore = result.Score ?? 0.0;
            double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                result.Document.Content,
                request.Query,
                vectorScore);

            metadataDict.TryGetValue(result.Document.DocumentId, out var metadata);

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
                BlobPath = metadata?.BlobPath ?? result.Document.BlobPath,
                BlobContainer = metadata?.BlobContainer ?? result.Document.BlobContainer,
                OriginalFileName = metadata?.OriginalFileName ?? result.Document.OriginalFileName,
                ContentType = metadata?.ContentType ?? result.Document.ContentType,
                TextContentBlobPath = metadata?.TextContentBlobPath ?? result.Document.TextContentBlobPath,
                FileSizeBytes = metadata?.FileSizeBytes ?? result.Document.FileSizeBytes
            });
        }

        var filteredResults = FilterResults(results, request);
        var enhancedResults = await _searchHistoryService.RunEnhancedSearchAsync(request, fallbackQuery, queryEmbedding, filteredResults);
        var finalResults = MergeSearchResults(filteredResults, enhancedResults, request.MaxResults);

        return new SearchResponse
        {
            Query = request.Query,
            Results = finalResults,
            Success = true,
            TotalResults = finalResults.Count
        };
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
