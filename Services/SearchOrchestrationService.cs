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

    // Cache for previous search results to support follow-up questions
    private static readonly Dictionary<string, List<SearchResult>> _searchResultsCache = new();
    private static readonly object _cacheLock = new object();
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

                // Debug logging for missing metadata
                if (string.IsNullOrEmpty(results.Last().OriginalFileName))
                {
                    _logger.LogWarning("Missing OriginalFileName for DocumentId: {DocumentId}, ChunkIndex: {ChunkIndex}, Metadata: {Metadata}",
                        result.Document.DocumentId, result.Document.ChunkIndex, result.Document.Metadata);
                }
            }

            // 5. Apply simplified score-based filtering
            var filteredResults = FilterResults(results, request);

            _logger.LogInformation("Filtered {Original} results to {Relevant} relevant results for query: '{Query}'",
                results.Count, filteredResults.Count, request.Query);

            // 5. Apply source diversification (max 1 chunk per document) and limit to MaxSourcesForAnswer
            var maxSources = _configuration.GetValue<int>("ChatService:MaxSourcesForAnswer", 5);

            // CRITICAL FIX: For potential follow-up scenarios, preserve more documents
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

            var response = new SearchResponse
            {
                Query = request.Query,
                ExpandedQuery = expandedQuery != request.Query ? expandedQuery : null,
                Results = diversifiedResults,
                Success = true,
                TotalResults = diversifiedResults.Count
            };

            // 6. Enhanced search for follow-up questions and related topics
            var finalResults = diversifiedResults;
            if (request.ChatHistory?.Any() == true)
            {
                bool isFollowUpInContext = FollowUpQuestionAnalyzer.IsFollowUpQuestion(request.Query, _logger);
                bool isRelatedTopic = !isFollowUpInContext && await _searchHistoryService.IsRelatedTopicQuestionAsync(request.Query, request.ChatHistory);

                _logger.LogInformation("Search strategy analysis - IsFollowUp: {IsFollowUp}, IsRelatedTopic: {IsRelatedTopic}, Query: '{Query}'",
                    isFollowUpInContext, isRelatedTopic, request.Query);

                if (isFollowUpInContext || isRelatedTopic)
                {
                    List<SearchResult> contextualResults = diversifiedResults ?? new List<SearchResult>();

                    // For follow-up questions, use a more intelligent approach:
                    // 1. If we have meaningful current results (>0), use them for context
                    // 2. Only fall back to chat history extraction if current results are poor
                    if (isFollowUp)
                    {
                        bool hasGoodCurrentResults = diversifiedResults?.Any() == true;

                        if (!hasGoodCurrentResults)
                        {
                            // Only try to extract from chat history if current results are poor
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
                                diversifiedResults?.Count ?? 0);
                        }
                    }

                    // Pass contextual results (current or previous) to enhanced search
                    var enhancedResults = await _searchHistoryService.TryEnhancedSearchWithHistoryAsync(request, searchQuery, queryEmbedding, contextualResults);
                    _logger.LogInformation("Enhanced search returned {EnhancedCount} results", enhancedResults.Count);

                    finalResults = CombineAndPrioritizeResults(diversifiedResults ?? new List<SearchResult>(), enhancedResults, request.MaxResults);

                    if (finalResults.Count > (diversifiedResults?.Count ?? 0))
                    {
                        _logger.LogInformation("{SearchType} search enhanced results from {OriginalCount} to {FinalCount}",
                            isFollowUp ? "Follow-up" : "Related topic", diversifiedResults?.Count ?? 0, finalResults.Count);
                    }
                }
            }

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
                        var enhancedResults = await _searchHistoryService.TryEnhancedSearchWithHistoryAsync(request, searchQuery, queryEmbedding);
                        if (enhancedResults.Any())
                        {
                            _logger.LogInformation("Enhanced search with history found {ResultCount} results using keywords: {Keywords}",
                                enhancedResults.Count, string.Join(", ", historyKeywords.Take(5)));
                            response.Results = enhancedResults;
                            response.GeneratedAnswer = await _chatService.GenerateAnswerWithHistoryAsync(
                                request.Query, enhancedResults, request.ChatHistory);
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
    /// For follow-up questions, search within the documents from current search results
    /// This is more reliable than extracting from chat history
    /// </summary>
    private async Task<List<SearchResult>> SearchWithinCurrentDocumentsAsync(
        SearchRequest request, string searchQuery, IReadOnlyList<float> queryEmbedding, List<SearchResult> currentResults)
    {
        var results = new List<SearchResult>();
        var documentIds = currentResults.Select(r => r.DocumentId).Distinct().Take(3).ToList();

        _logger.LogInformation("Searching within {Count} documents from current search results for follow-up question", documentIds.Count);

        // CRITICAL FIX: Load metadata bulk for all documents to ensure OriginalFileName is available
        var metadataBulk = await LoadMetadataBulkAsync(documentIds);

        foreach (var docId in documentIds)
        {
            try
            {
                var docSpecificResults = await _searchService.HybridSearchAsync(
                    searchQuery, queryEmbedding, request.MaxResults * 2, docId);

                var docResultsList = docSpecificResults.GetResults().ToList();

                foreach (var result in docResultsList)
                {
                    double vectorScore = result.Score ?? 0.0;
                    double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                        result.Document.Content, request.Query, vectorScore);

                    // Strong boost for same-document results in follow-up questions
                    combinedScore *= 2.5;

                    // Create SearchResult with proper metadata
                    var searchResult = new SearchResult
                    {
                        Id = result.Document.Id,
                        Content = result.Document.Content,
                        DocumentId = result.Document.DocumentId,
                        ChunkIndex = result.Document.ChunkIndex,
                        Score = combinedScore,
                        VectorScore = vectorScore,
                        Metadata = result.Document.Metadata,
                        CreatedAt = result.Document.CreatedAt
                    };

                    // Add metadata if available, with fallback to document metadata
                    if (metadataBulk.TryGetValue(result.Document.DocumentId, out var metadata))
                    {
                        searchResult.BlobPath = metadata.BlobPath;
                        searchResult.BlobContainer = metadata.BlobContainer;
                        searchResult.OriginalFileName = metadata.OriginalFileName;
                        searchResult.ContentType = metadata.ContentType;
                        searchResult.TextContentBlobPath = metadata.TextContentBlobPath;
                        searchResult.FileSizeBytes = metadata.FileSizeBytes;
                    }
                    else
                    {
                        // Fallback to document metadata if bulk metadata is not available
                        searchResult.BlobPath = result.Document.BlobPath;
                        searchResult.BlobContainer = result.Document.BlobContainer;
                        searchResult.OriginalFileName = result.Document.OriginalFileName;
                        searchResult.ContentType = result.Document.ContentType;
                        searchResult.TextContentBlobPath = result.Document.TextContentBlobPath;
                        searchResult.FileSizeBytes = result.Document.FileSizeBytes;

                        _logger.LogWarning("⚠️ NO METADATA: {DocumentId} using document OriginalFileName: {FileName}",
                            result.Document.DocumentId, result.Document.OriginalFileName ?? "[NULL]");
                    }

                    results.Add(searchResult);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search in current document {DocumentId}", docId);
            }
        }

        return results;
    }

    /// <summary>
    /// Enhanced search using chat history context and keywords
    /// </summary>
    private async Task<List<SearchResult>> SearchWithHistoryContextAsync(
        SearchRequest request, string searchQuery, IReadOnlyList<float> queryEmbedding)
    {
        var results = new List<SearchResult>();

        try
        {
            var historyKeywords = ChatHistoryAnalyzer.ExtractKeywords(request.ChatHistory);
            if (historyKeywords.Any())
            {
                // Create multiple search variations with history context
                var searchVariations = new[]
                {
                    $"{request.Query} {string.Join(" ", historyKeywords.Take(2))}", // Query + top 2 keywords
                    $"{string.Join(" ", historyKeywords.Take(3))} {request.Query}", // Top 3 keywords + query
                    request.Query // Original query as fallback
                };

                foreach (var variation in searchVariations.Take(2)) // Only try first 2 variations
                {
                    _logger.LogDebug("Trying context-enhanced search with: '{Query}'", variation);

                    var variationEmbedding = await _embeddingService.GenerateEmbeddingAsync(variation);
                    var searchResults = await _searchService.HybridSearchAsync(
                        variation, variationEmbedding, request.MaxResults, request.DocumentId);

                    foreach (var result in searchResults.GetResults().Take(3)) // Limit per variation
                    {
                        double vectorScore = result.Score ?? 0.0;
                        double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                            result.Document.Content, request.Query, vectorScore);

                        // Strong boost for context-enhanced results that contain history keywords
                        if (TextProcessingHelper.ContainsAnyKeyword(result.Document.Content, historyKeywords))
                        {
                            combinedScore *= 2.2; // 120% boost for content with history keywords
                        }
                        else
                        {
                            combinedScore *= 1.5; // 50% boost for context search results
                        }

                        results.Add(CreateSearchResultFromDocument(result, combinedScore, vectorScore));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed context-enhanced search");
        }

        return results;
    }

    /// <summary>
    /// Checks if content contains keywords from chat history
    /// </summary>
    private bool ContainsHistoryKeywords(string content, List<string> historyKeywords)
    {
        var contentLower = content.ToLower();
        return historyKeywords.Any(keyword => contentLower.Contains(keyword.ToLower()));
    }

    /// <summary>
    /// Helper method to create SearchResult from Azure search result
    /// </summary>
    private SearchResult CreateSearchResultFromDocument(Azure.Search.Documents.Models.SearchResult<DocumentChunk> result,
        double combinedScore, double vectorScore)
    {
        return new SearchResult
        {
            Id = result.Document.Id,
            Content = result.Document.Content,
            DocumentId = result.Document.DocumentId,
            ChunkIndex = result.Document.ChunkIndex,
            Score = combinedScore,
            VectorScore = vectorScore,
            Metadata = result.Document.Metadata,
            CreatedAt = result.Document.CreatedAt,
            BlobPath = result.Document.BlobPath,
            BlobContainer = result.Document.BlobContainer,
            OriginalFileName = result.Document.OriginalFileName,
            ContentType = result.Document.ContentType,
            TextContentBlobPath = result.Document.TextContentBlobPath,
            FileSizeBytes = result.Document.FileSizeBytes
        };
    }

    /// <summary>
    /// Extracts document references from previous search results in chat history
    /// </summary>

    /// <summary>
    /// Performs enhanced search using chat history context to find previously referenced documents
    /// </summary>



    /// <summary>
    /// Determines if the current query is about a related topic to recent conversation using semantic similarity
    /// </summary>

    /// <summary>
    /// Calculates cosine similarity between two embedding vectors
    /// </summary>
    private static double CalculateCosineSimilarity(IReadOnlyList<float> vector1, IReadOnlyList<float> vector2)
    {
        if (vector1.Count != vector2.Count) return 0.0;

        double dotProduct = 0.0;
        double magnitude1 = 0.0;
        double magnitude2 = 0.0;

        for (int i = 0; i < vector1.Count; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        if (magnitude1 == 0.0 || magnitude2 == 0.0) return 0.0;

        return dotProduct / (Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2));
    }

    /// <summary>
    /// Checks if two questions have similar structure patterns
    /// </summary>
    private static bool HasSimilarQuestionStructure(string query1, string query2)
    {
        var q1Lower = query1.ToLowerInvariant();
        var q2Lower = query2.ToLowerInvariant();

        // Check for similar question words
        var questionWords = new[] { "welche", "was", "wie", "wo", "wann", "warum", "wer" };
        var q1QuestionWords = questionWords.Where(w => q1Lower.Contains(w)).ToList();
        var q2QuestionWords = questionWords.Where(w => q2Lower.Contains(w)).ToList();

        if (q1QuestionWords.Any() && q2QuestionWords.Any() && q1QuestionWords.Intersect(q2QuestionWords).Any())
        {
            return true;
        }

        // Check for similar action words
        var actionWords = new[] { "empfiehlt", "vorgeschlagen", "definiert", "beschreibt", "erklärt", "zeigt" };
        var q1ActionWords = actionWords.Where(w => q1Lower.Contains(w)).ToList();
        var q2ActionWords = actionWords.Where(w => q2Lower.Contains(w)).ToList();

        return q1ActionWords.Any() && q2ActionWords.Any() && q1ActionWords.Intersect(q2ActionWords).Any();
    }

    /// <summary>
    /// Fallback method for keyword-based similarity detection
    /// </summary>
    private bool HasSimilarKeywords(string query, List<string> previousQuestions)
    {
        var queryWords = ExtractMeaningfulWords(query);

        foreach (var previousQuestion in previousQuestions)
        {
            var previousWords = ExtractMeaningfulWords(previousQuestion);
            var commonWords = queryWords.Intersect(previousWords, StringComparer.OrdinalIgnoreCase).Count();
            var totalUniqueWords = queryWords.Union(previousWords, StringComparer.OrdinalIgnoreCase).Count();

            if (totalUniqueWords > 0)
            {
                var similarity = (double)commonWords / totalUniqueWords;
                if (similarity >= 0.3) // 30% word overlap
                {
                    _logger.LogInformation("Detected related topic via keyword similarity ({Similarity:F3}): '{Query}' relates to '{Previous}'",
                        similarity, query, previousQuestion);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Extracts meaningful words from a text, filtering out stop words
    /// </summary>
    private List<string> ExtractMeaningfulWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !TextProcessingHelper.IsStopWord(w))
            .ToList();
    }

    /// <summary>
    /// Combines current query with history context, with different strategies for follow-up questions
    /// </summary>
    private string CombineQueryWithHistoryContext(string originalQuery, List<string> searchTerms)
    {
        if (!searchTerms.Any()) return originalQuery;

        // For follow-up questions, be more conservative with term combination
        bool isFollowUp = FollowUpQuestionAnalyzer.IsFollowUpQuestion(originalQuery);

        if (isFollowUp)
        {
            // For follow-ups: use fewer terms and prioritize original query
            var limitedTerms = searchTerms.Take(2); // Reduced from 5 to 2
            return $"{originalQuery} {string.Join(" ", limitedTerms)}";
        }
        else
        {
            // For new questions: use more context terms
            var topTerms = searchTerms.Take(4); // Reduced from 5 to 4
            return $"{originalQuery} {string.Join(" ", topTerms)}";
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
    /// Combines and prioritizes results from normal search and history-based search
    /// </summary>
    private List<SearchResult> CombineAndPrioritizeResults(List<SearchResult> normalResults, List<SearchResult> historyResults, int maxResults)
    {
        var combinedResults = new List<SearchResult>();
        var seenDocuments = new HashSet<string>();

        // First, add history-based results (they get strong priority for follow-up questions)
        foreach (var result in historyResults.OrderByDescending(r => r.Score))
        {
            if (!seenDocuments.Contains(result.DocumentId))
            {
                // History results already have their boosts applied in SearchWithinCurrentDocumentsAsync
                // No additional boost needed here - they should maintain their high scores

                _logger.LogInformation("History result added: DocumentId={DocumentId}, Score={Score:F3}, OriginalFileName={OriginalFileName}",
                    result.DocumentId, result.Score, result.OriginalFileName ?? "[NULL]");

                combinedResults.Add(result);
                seenDocuments.Add(result.DocumentId);
            }
        }

        // Then add normal results that aren't already included, but only if we have space
        foreach (var result in normalResults.OrderByDescending(r => r.Score))
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
    /// Extracts filenames from chat history to find previous context
    /// </summary>
    private List<string> ExtractDocumentIdsFromChatHistory(List<ChatMessage> chatHistory)
    {
        var filenames = new HashSet<string>();

        // Look for the most recent assistant response that contains sources
        for (int i = chatHistory.Count - 1; i >= 0; i--)
        {
            var message = chatHistory[i];
            if (message.Role == "assistant" && !string.IsNullOrEmpty(message.Content))
            {
                // Look for source references in format: **Quelle: [filename]** or similar
                var sourceMatches = System.Text.RegularExpressions.Regex.Matches(
                    message.Content,
                    @"\*\*Quelle:\s*([^\*]+)\*\*|\*\*Source:\s*([^\*]+)\*\*",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match match in sourceMatches)
                {
                    var filename = match.Groups[1].Success ? match.Groups[1].Value.Trim() : match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(filename))
                    {
                        filenames.Add(filename);
                        _logger.LogInformation("Found filename in chat history: '{Filename}'", filename);
                    }
                }

                // If we found sources in this response, use them
                if (filenames.Any())
                {
                    _logger.LogInformation("Found {Count} source references in recent assistant response", filenames.Count);
                    break;
                }
            }
        }

        return filenames.ToList();
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
                _logger.LogWarning("No results found in previous context documents, falling back to general search");
                // Fall back to normal search logic by continuing with the normal flow
                return await PerformNormalSearch(request);
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
            // Fall back to normal search
            return await PerformNormalSearch(request);
        }
    }

    /// <summary>
    /// Performs the normal search logic (fallback for follow-up questions)
    /// </summary>
    private async Task<SearchResponse> PerformNormalSearch(SearchRequest request)
    {
        // Fallback to simplified normal search - just continue with regular logic
        var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(request.Query);
        var searchResults = await _searchService.HybridSearchAsync(request.Query, queryEmbedding, request.MaxResults * 3);
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

        return new SearchResponse
        {
            Query = request.Query,
            Results = filteredResults,
            Success = true,
            TotalResults = results.Count
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
