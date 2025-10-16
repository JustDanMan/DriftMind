using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

/// <summary>
/// Service for handling search history context and follow-up questions
/// </summary>
public interface ISearchHistoryService
{
    Task<List<SearchResult>?> ExtractPreviousSearchResultsFromHistory(List<ChatMessage> chatHistory);
    Task<List<SearchResult>> RunEnhancedSearchAsync(
        SearchRequest request, 
        string searchQuery, 
        IReadOnlyList<float> queryEmbedding, 
        List<SearchResult>? currentResults = null);
    Task<bool> IsRelatedTopicQuestionAsync(string query, List<ChatMessage>? chatHistory);
}

public class SearchHistoryService : ISearchHistoryService
{
    private readonly ISearchService _searchService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger<SearchHistoryService> _logger;

    public SearchHistoryService(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        ILogger<SearchHistoryService> logger)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _logger = logger;
    }

    /// <summary>
    /// Extracts previous search results from chat history for follow-up context
    /// </summary>
    public Task<List<SearchResult>?> ExtractPreviousSearchResultsFromHistory(List<ChatMessage> chatHistory)
    {
        if (!chatHistory.Any()) return Task.FromResult<List<SearchResult>?>(null);

        _logger.LogInformation("Extracting previous search results from chat history with {MessageCount} messages", chatHistory.Count);

        // Look for the most recent assistant message that contains search results
        var recentAssistantMessages = chatHistory
            .Where(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .TakeLast(2) // Look at last 2 assistant messages
            .ToList();

        foreach (var message in recentAssistantMessages.AsEnumerable().Reverse())
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            var content = message.Content;
            _logger.LogDebug("Analyzing assistant message for search results: {ContentPreview}", 
                content.Length > 100 ? content.Substring(0, 100) + "..." : content);

            // Look for patterns that indicate search results were provided
            if (content.Contains("**Quellen:**") || content.Contains("Basierend auf") || 
                content.Contains("Laut") || content.Contains("**Quelle"))
            {
                _logger.LogInformation("Found assistant message with potential search results");
                
                // Try to extract document references and create mock search results
                var documentReferences = ChatHistoryAnalyzer.ExtractDocumentReferences(chatHistory, _logger);
                if (documentReferences.Any())
                {
                    _logger.LogInformation("Creating search results from {Count} document references: {References}", 
                        documentReferences.Count, string.Join(", ", documentReferences));

                    var searchResults = new List<SearchResult>();
                    foreach (var docRef in documentReferences.Take(5))
                    {
                        searchResults.Add(new SearchResult
                        {
                            Id = docRef,
                            Content = $"Referenced document: {docRef}",
                            DocumentId = docRef,
                            ChunkIndex = 0,
                            Score = 0.9,
                            VectorScore = 0.9,
                            Metadata = docRef,
                            CreatedAt = DateTimeOffset.UtcNow,
                            OriginalFileName = docRef
                        });
                    }
                    
                    return Task.FromResult<List<SearchResult>?>(searchResults);
                }
            }
        }

        _logger.LogInformation("No previous search results found in chat history");
        return Task.FromResult<List<SearchResult>?>(null);
    }

    /// <summary>
    /// Enhanced search that incorporates chat history context for better results
    /// </summary>
    public async Task<List<SearchResult>> RunEnhancedSearchAsync(
        SearchRequest request, 
        string searchQuery, 
        IReadOnlyList<float> queryEmbedding, 
        List<SearchResult>? currentResults = null)
    {
        _logger.LogInformation("🔄 ENHANCED SEARCH: Running enhanced search pipeline for query: '{Query}'", request.Query);

        try
        {
            // Extract context from chat history
            var historyKeywords = ChatHistoryAnalyzer.ExtractKeywords(request.ChatHistory);
            var documentReferences = ChatHistoryAnalyzer.ExtractDocumentReferences(request.ChatHistory, _logger);

            _logger.LogInformation("Enhanced search context - Keywords: [{Keywords}], Documents: [{Documents}]", 
                string.Join(", ", historyKeywords), string.Join(", ", documentReferences));

            // Perform hybrid search with the original query
            var hybridResults = await _searchService.HybridSearchAsync(
                searchQuery, 
                queryEmbedding.ToArray(), 
                top: 20);

            var resultsList = hybridResults.GetResults().ToList();
            if (!resultsList.Any())
            {
                _logger.LogWarning("Enhanced search returned no results");
                return currentResults ?? new List<SearchResult>();
            }

            // Load metadata for unique documents (same logic as SearchOrchestrationService)
            var uniqueDocumentIds = resultsList
                .Select(r => r.Document.DocumentId)
                .Distinct()
                .ToList();
            
            // Load metadata from Chunk 0 for all unique documents
            var chunk0s = await _searchService.GetChunk0sForDocumentsAsync(uniqueDocumentIds);
            var metadataDict = chunk0s.ToDictionary(
                chunk => chunk.DocumentId,
                chunk => chunk);

            var enhancedResults = new List<SearchResult>();

            foreach (var result in resultsList)
            {
                if (result?.Document == null) continue;

                try
                {
                    // Calculate relevance score
                    var vectorScore = result.Score ?? 0.0;
                    
                    double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                        result.Document.Content, 
                        request.Query, 
                        vectorScore);

                    // Strong boost for documents that match our document references
                    if (ChatHistoryAnalyzer.DocumentIsReferencedInHistory(result.Document.DocumentId, result.Document.Metadata, documentReferences))
                    {
                        combinedScore *= 1.8; // 80% boost for history-referenced documents
                        _logger.LogDebug("Applied strong history boost to document: {DocumentId} (score: {Score:F3})", 
                            result.Document.DocumentId, combinedScore);
                    }
                    // Moderate boost for content containing history keywords
                    else if (TextProcessingHelper.ContainsAnyKeyword(result.Document.Content, historyKeywords))
                    {
                        combinedScore *= 1.3; // 30% boost for history context
                        _logger.LogDebug("Applied history context boost to document: {DocumentId} (score: {Score:F3})", 
                            result.Document.DocumentId, combinedScore);
                    }

                    enhancedResults.Add(new SearchResult
                    {
                        Id = result.Document.Id,
                        Content = result.Document.Content,
                        DocumentId = result.Document.DocumentId,
                        ChunkIndex = result.Document.ChunkIndex,
                        Score = combinedScore,
                        VectorScore = vectorScore,
                        Metadata = result.Document.Metadata,
                        CreatedAt = result.Document.CreatedAt,
                        // Use metadata from chunk 0 or fallback to current chunk metadata
                        BlobPath = metadataDict.TryGetValue(result.Document.DocumentId, out var metadata) ? metadata.BlobPath : result.Document.BlobPath,
                        BlobContainer = metadata?.BlobContainer ?? result.Document.BlobContainer,
                        OriginalFileName = metadata?.OriginalFileName ?? result.Document.OriginalFileName,
                        ContentType = metadata?.ContentType ?? result.Document.ContentType,
                        TextContentBlobPath = metadata?.TextContentBlobPath ?? result.Document.TextContentBlobPath,
                        FileSizeBytes = metadata?.FileSizeBytes ?? result.Document.FileSizeBytes
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing search result for document {DocumentId}", 
                        result.Document?.DocumentId ?? "unknown");
                }
            }

            // Sort by combined score and return top results
            var finalResults = enhancedResults
                .OrderByDescending(r => r.Score)
                .Take(15)
                .ToList();

            _logger.LogInformation("✅ ENHANCED SEARCH: Returning {Count} enhanced results with history context", finalResults.Count);
            return finalResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in enhanced search with history. Falling back to current results.");
            return currentResults ?? new List<SearchResult>();
        }
    }

    /// <summary>
    /// Determines if the current query is about a related topic to recent conversation using semantic similarity
    /// </summary>
    public async Task<bool> IsRelatedTopicQuestionAsync(string query, List<ChatMessage>? chatHistory)
    {
        if (string.IsNullOrWhiteSpace(query) || chatHistory?.Any() != true) return false;

        _logger.LogInformation("Checking semantic similarity for query: '{Query}'", query);
        
        // Get recent user questions (not assistant responses)
        var recentUserQuestions = chatHistory
            .Where(m => m.Role.ToLower() == "user" && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(3)
            .Select(m => m.Content)
            .ToList();
        
        if (!recentUserQuestions.Any())
        {
            _logger.LogDebug("No recent user questions found in chat history");
            return false;
        }

        try
        {
            // Generate embedding for current query
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(query);
            
            // Calculate semantic similarity with recent questions
            double maxSimilarity = 0;
            string? mostSimilarQuestion = null;
            
            foreach (var previousQuestion in recentUserQuestions)
            {
                var previousEmbedding = await _embeddingService.GenerateEmbeddingAsync(previousQuestion);
                var similarity = FollowUpQuestionAnalyzer.CalculateCosineSimilarity(queryEmbedding, previousEmbedding);
                
                if (similarity > maxSimilarity)
                {
                    maxSimilarity = similarity;
                    mostSimilarQuestion = previousQuestion;
                }
            }
            
            // Consider topics related if similarity is above threshold (0.7)
            bool isRelated = maxSimilarity > 0.7;
            
            _logger.LogInformation("Semantic similarity check: Query '{Query}' vs '{MostSimilar}' = {Similarity:F3} (threshold: 0.7) -> {IsRelated}", 
                query, mostSimilarQuestion, maxSimilarity, isRelated ? "RELATED" : "NOT RELATED");
            
            return isRelated;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating semantic similarity for query: '{Query}'", query);
            return false;
        }
    }
}
