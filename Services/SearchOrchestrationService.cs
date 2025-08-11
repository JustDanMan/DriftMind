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
                bool isFollowUp = IsFollowUpQuestion(request.Query);
                bool isRelatedTopic = !isFollowUp && await IsRelatedTopicQuestionAsync(request.Query, request.ChatHistory);
                
                _logger.LogInformation("Search strategy analysis - IsFollowUp: {IsFollowUp}, IsRelatedTopic: {IsRelatedTopic}, Query: '{Query}'", 
                    isFollowUp, isRelatedTopic, request.Query);
                
                if (isFollowUp || isRelatedTopic)
                {
                    var enhancedResults = await TryEnhancedSearchWithHistoryAsync(request, searchQuery, queryEmbedding);
                    _logger.LogInformation("Enhanced search returned {EnhancedCount} results", enhancedResults.Count);
                    
                    finalResults = CombineAndPrioritizeResults(diversifiedResults, enhancedResults, request.MaxResults);
                    
                    if (finalResults.Count > diversifiedResults.Count)
                    {
                        _logger.LogInformation("{SearchType} search enhanced results from {OriginalCount} to {FinalCount}", 
                            isFollowUp ? "Follow-up" : "Related topic", diversifiedResults.Count, finalResults.Count);
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
                    var historyKeywords = ExtractKeywordsFromChatHistory(request.ChatHistory);
                    if (historyKeywords.Count >= 2) // Require at least 2 meaningful keywords from document discussions
                    {
                        var enhancedResults = await TryEnhancedSearchWithHistoryAsync(request, searchQuery, queryEmbedding);
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
        var minScore = _configuration.GetValue<double>("ChatService:MinScoreForAnswer", 0.25);
        
        // For follow-up questions, use a more lenient threshold
        bool isFollowUp = IsFollowUpQuestion(request.Query);
        if (isFollowUp)
        {
            minScore = Math.Min(minScore, 0.15); // Lower threshold for follow-up questions
            _logger.LogDebug("Using lenient filtering for follow-up question: MinScore lowered to {MinScore}", minScore);
        }
        
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
    /// For follow-up questions, search primarily within recently referenced documents
    /// </summary>
    private async Task<List<SearchResult>> SearchWithinRecentlyReferencedDocumentsAsync(
        SearchRequest request, string searchQuery, IReadOnlyList<float> queryEmbedding)
    {
        var results = new List<SearchResult>();
        
        // Strategy 1: Extract document references and search within them
        var recentDocuments = await ExtractRecentlyReferencedDocumentIdsAsync(request.ChatHistory);
        
        if (recentDocuments.Any())
        {
            _logger.LogInformation("Searching within {Count} recently referenced documents for follow-up question", recentDocuments.Count);
            
            foreach (var docId in recentDocuments.Take(2))
            {
                _logger.LogDebug("Searching within document: {DocumentId}", docId);
                
                try
                {
                    var docSpecificResults = await _searchService.HybridSearchAsync(
                        searchQuery, queryEmbedding, request.MaxResults * 2, docId);
                    
                    var docResultsList = docSpecificResults.GetResults().ToList();
                    _logger.LogDebug("Found {Count} results in document {DocumentId}", docResultsList.Count, docId);
                    
                    foreach (var result in docResultsList)
                    {
                        double vectorScore = result.Score ?? 0.0;
                        double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                            result.Document.Content, request.Query, vectorScore);
                        
                        // MASSIVE boost for same-document results in follow-up questions
                        combinedScore *= 3.0;
                        
                        _logger.LogDebug("Applied follow-up same-document boost to {DocumentId} (score: {Score:F3})", 
                            result.Document.DocumentId, combinedScore);
                        
                        results.Add(CreateSearchResultFromDocument(result, combinedScore, vectorScore));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to search in document {DocumentId}, trying as filename", docId);
                    
                    // Fallback: Try searching for content that mentions this "document ID" as text
                    await TrySearchByDocumentKeyword(docId, searchQuery, queryEmbedding, request, results);
                }
            }
        }
        
        // Strategy 2: If no specific documents found or limited results, use context-based search
        if (results.Count < 3) // If we have less than 3 good results, try context approach
        {
            _logger.LogInformation("Limited document-specific results ({Count}), trying context-enhanced search", results.Count);
            
            var contextResults = await SearchWithHistoryContextAsync(request, searchQuery, queryEmbedding);
            results.AddRange(contextResults);
        }
        
        var finalResults = results
            .GroupBy(r => r.Id) // Remove duplicates by ID
            .Select(g => g.OrderByDescending(r => r.Score).First()) // Keep highest scored version
            .OrderByDescending(r => r.Score)
            .Take(request.MaxResults)
            .ToList();
        
        _logger.LogInformation("Follow-up search returned {Count} results", finalResults.Count);
        return finalResults;
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
            var historyKeywords = ExtractKeywordsFromChatHistory(request.ChatHistory);
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
                        if (ContainsHistoryKeywords(result.Document.Content, historyKeywords))
                        {
                            combinedScore *= 2.2; // 120% boost for content with history keywords
                            _logger.LogDebug("Applied history keyword boost to document {DocumentId} (score: {Score:F3})", 
                                result.Document.DocumentId, combinedScore);
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
    /// Helper method to search by document keyword when direct document ID fails
    /// </summary>
    private async Task TrySearchByDocumentKeyword(string docKeyword, string searchQuery, 
        IReadOnlyList<float> queryEmbedding, SearchRequest request, List<SearchResult> results)
    {
        try
        {
            var keywordQuery = $"{searchQuery} {docKeyword}";
            var keywordResults = await _searchService.HybridSearchAsync(
                keywordQuery, queryEmbedding, request.MaxResults, request.DocumentId);
            
            foreach (var result in keywordResults.GetResults())
            {
                // Check if the result content actually relates to the document keyword
                if (result.Document.Content.ToLower().Contains(docKeyword.ToLower()) ||
                    result.Document.OriginalFileName?.ToLower().Contains(docKeyword.ToLower()) == true)
                {
                    double vectorScore = result.Score ?? 0.0;
                    double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                        result.Document.Content, request.Query, vectorScore);
                    
                    combinedScore *= 2.5; // Strong boost for keyword-matched results
                    
                    results.Add(CreateSearchResultFromDocument(result, combinedScore, vectorScore));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed keyword search for document: {DocKeyword}", docKeyword);
        }
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
    /// Extracts document IDs from recent chat history based on source references
    /// </summary>
    private async Task<List<string>> ExtractRecentlyReferencedDocumentIdsAsync(List<ChatMessage>? chatHistory)
    {
        if (chatHistory?.Any() != true) return new List<string>();
        
        var documentIds = new List<string>();
        
        // Look in the last 2 assistant messages for document references
        var recentAssistantMessages = chatHistory
            .Where(m => m.Role.ToLower() == "assistant")
            .TakeLast(2)
            .ToList();
        
        foreach (var message in recentAssistantMessages)
        {
            _logger.LogDebug("🔍 ANALYZING CHAT MESSAGE: {MessagePreview}", 
                message.Content.Length > 100 ? message.Content.Substring(0, 100) + "..." : message.Content);
            
            // Method 1: Try to extract DocumentId patterns directly from context text
            // Look for patterns like DocumentId mentions in the response
            var docIdMatches = System.Text.RegularExpressions.Regex.Matches(
                message.Content, @"\b([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\b", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            foreach (System.Text.RegularExpressions.Match match in docIdMatches)
            {
                documentIds.Add(match.Groups[1].Value);
                _logger.LogInformation("📄 FOUND GUID DocumentId: {DocumentId}", match.Groups[1].Value);
            }
            
            // Method 2: Extract document filenames and find corresponding documents
            var sourceMatches = System.Text.RegularExpressions.Regex.Matches(
                message.Content, @"📄 DOCUMENT: ([^\n📍🎯]+?)(?:\n|📍|🎯|$)");
            
            _logger.LogDebug("📄 DOCUMENT PATTERN MATCHES: {Count}", sourceMatches.Count);
            
            foreach (System.Text.RegularExpressions.Match match in sourceMatches)
            {
                var docName = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(docName))
                {
                    _logger.LogInformation("📄 FOUND DOCUMENT REFERENCE: '{FileName}' - Attempting resolution...", docName);
                    
                    // Try multiple search strategies to find the document
                    var resolvedIds = await TryResolveDocumentName(docName);
                    documentIds.AddRange(resolvedIds);
                    
                    _logger.LogInformation("📄 RESOLVED '{FileName}' → {Count} DocumentIds: {DocumentIds}", 
                        docName, resolvedIds.Count, string.Join(", ", resolvedIds));
                }
            }
        }
        
        var uniqueDocumentIds = documentIds.Distinct().ToList();
        _logger.LogInformation("Extracted {Count} unique document IDs from chat history: {Documents}", 
            uniqueDocumentIds.Count, string.Join(", ", uniqueDocumentIds.Take(3)));
        
        return uniqueDocumentIds;
    }

    /// <summary>
    /// Tries multiple strategies to resolve a document filename to DocumentIds
    /// </summary>
    private async Task<List<string>> TryResolveDocumentName(string docName)
    {
        var resolvedIds = new List<string>();
        
        try
        {
            // Strategy 1: Direct filename search
            var exactSearch = await _searchService.SearchAsync($"\"{docName}\"", 5);
            var exactResults = exactSearch.GetResults()
                .Where(r => !string.IsNullOrEmpty(r.Document.OriginalFileName) && 
                           r.Document.OriginalFileName.Equals(docName, StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Document.DocumentId)
                .Distinct()
                .ToList();
            
            if (exactResults.Any())
            {
                resolvedIds.AddRange(exactResults);
                _logger.LogDebug("Exact filename match found {Count} documents for '{FileName}'", exactResults.Count, docName);
            }
            
            // Strategy 2: Partial filename search if exact didn't work
            if (!resolvedIds.Any())
            {
                var partialSearch = await _searchService.SearchAsync(docName, 10);
                var partialResults = partialSearch.GetResults()
                    .Where(r => !string.IsNullOrEmpty(r.Document.OriginalFileName) && 
                               r.Document.OriginalFileName.ToLower().Contains(docName.ToLower()))
                    .Select(r => r.Document.DocumentId)
                    .Distinct()
                    .Take(3)
                    .ToList();
                
                if (partialResults.Any())
                {
                    resolvedIds.AddRange(partialResults);
                    _logger.LogDebug("Partial filename match found {Count} documents for '{FileName}'", partialResults.Count, docName);
                }
            }
            
            // Strategy 3: Key terms from filename
            if (!resolvedIds.Any())
            {
                var keyTerms = docName.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(term => term.Length > 3)
                    .Take(3);
                
                if (keyTerms.Any())
                {
                    var termQuery = string.Join(" ", keyTerms);
                    var termSearch = await _searchService.SearchAsync(termQuery, 8);
                    var termResults = termSearch.GetResults()
                        .Select(r => r.Document.DocumentId)
                        .Distinct()
                        .Take(2)
                        .ToList();
                    
                    if (termResults.Any())
                    {
                        resolvedIds.AddRange(termResults);
                        _logger.LogDebug("Key terms search found {Count} documents for '{FileName}' using terms: {Terms}", 
                            termResults.Count, docName, termQuery);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve document name: {FileName}", docName);
        }
        
        return resolvedIds;
    }

    /// <summary>
    /// Performs enhanced search using chat history context to find previously referenced documents
    /// </summary>
    private async Task<List<SearchResult>> TryEnhancedSearchWithHistoryAsync(SearchRequest request, string searchQuery, IReadOnlyList<float> queryEmbedding)
    {
        try
        {
            _logger.LogInformation("Attempting enhanced search with chat history context");

            // Determine search strategy
            bool isFollowUpQuestion = IsFollowUpQuestion(request.Query);
            bool isRelatedTopic = !isFollowUpQuestion && await IsRelatedTopicQuestionAsync(request.Query, request.ChatHistory);
            
            List<SearchResult> enhancedResults = new();
            
            // NEW STRATEGY: For follow-up questions, search primarily within recently referenced documents
            if (isFollowUpQuestion)
            {
                _logger.LogInformation("🔍 FOLLOW-UP DETECTED: '{Query}' - Starting prioritized search in referenced documents", request.Query);
                
                var sameDocResults = await SearchWithinRecentlyReferencedDocumentsAsync(
                    request, searchQuery, queryEmbedding);
                
                if (sameDocResults.Any())
                {
                    _logger.LogInformation("✅ FOLLOW-UP SUCCESS: Found {Count} results within recently referenced documents for follow-up question", 
                        sameDocResults.Count);
                    
                    // Log the source documents for debugging
                    var sourceDocuments = sameDocResults.Select(r => r.OriginalFileName ?? r.DocumentId).Distinct();
                    _logger.LogInformation("📄 FOLLOW-UP SOURCES: {Sources}", string.Join(", ", sourceDocuments));
                    
                    return sameDocResults;
                }
                
                _logger.LogWarning("❌ FOLLOW-UP FALLBACK: No results in referenced documents, falling back to global search for follow-up question");
                // Continue with normal search below if no results found in referenced documents
            }
            
            // Extract keywords and document references from chat history
            var historyKeywords = ExtractKeywordsFromChatHistory(request.ChatHistory);
            var documentReferences = ExtractDocumentReferencesFromChatHistory(request.ChatHistory);
            
            // Strategy 1: For related topics, try document-specific searches first
            if (isRelatedTopic && documentReferences.Any())
            {
                _logger.LogInformation("Applying related topic search strategy with {DocumentCount} document references", 
                    documentReferences.Count);
                
                // Try searching within documents that were previously referenced
                foreach (var docRef in documentReferences.Take(3))
                {
                    // Try different search combinations with the document reference
                    var searchVariations = new[]
                    {
                        $"{request.Query} {docRef}",  // Query + document name
                        $"{docRef} {request.Query}",  // Document name + query
                        request.Query // Just the query, but we'll filter by document later
                    };
                    
                    foreach (var searchVariation in searchVariations)
                    {
                        _logger.LogDebug("Searching with document-specific query: '{Query}'", searchVariation);
                        
                        var docEmbedding = await _embeddingService.GenerateEmbeddingAsync(searchVariation);
                        var docResults = await _searchService.HybridSearchAsync(
                            searchVariation, docEmbedding, request.MaxResults * 4, request.DocumentId);
                        
                        var docResultsList = docResults.GetResults().ToList();
                        _logger.LogDebug("Document-specific search for '{DocRef}' with query '{Query}' found {Count} results", 
                            docRef, searchVariation, docResultsList.Count);
                        
                        foreach (var result in docResultsList)
                        {
                            double vectorScore = result.Score ?? 0.0;
                            double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                                result.Document.Content, 
                                request.Query, 
                                vectorScore);

                            // Strong boost for documents that match our document references
                            if (DocumentIsReferencedInHistory(result.Document.DocumentId, result.Document.Metadata, documentReferences))
                            {
                                combinedScore *= 1.8; // 80% boost for history-referenced documents
                                _logger.LogDebug("Applied strong history boost to document: {DocumentId} (score: {Score:F3})", 
                                    result.Document.DocumentId, combinedScore);
                            }

                            var searchResult = new SearchResult
                            {
                                Id = result.Document.Id,
                                Content = result.Document.Content,
                                DocumentId = result.Document.DocumentId,
                                ChunkIndex = result.Document.ChunkIndex,
                                Score = combinedScore,
                                VectorScore = vectorScore,
                                Metadata = result.Document.Metadata,
                                CreatedAt = result.Document.CreatedAt,
                                // Set metadata directly from document (no bulk loading in this path)
                                BlobPath = result.Document.BlobPath,
                                BlobContainer = result.Document.BlobContainer,
                                OriginalFileName = result.Document.OriginalFileName,
                                ContentType = result.Document.ContentType,
                                TextContentBlobPath = result.Document.TextContentBlobPath,
                                FileSizeBytes = result.Document.FileSizeBytes
                            };

                            // Ensure OriginalFileName is never null for document-specific search results
                            if (string.IsNullOrEmpty(searchResult.OriginalFileName))
                            {
                                // Try to extract from Metadata field as last resort
                                if (!string.IsNullOrEmpty(result.Document.Metadata) && result.Document.Metadata.StartsWith("File: "))
                                {
                                    searchResult.OriginalFileName = result.Document.Metadata.Substring(6); // Remove "File: " prefix
                                    _logger.LogDebug("Extracted OriginalFileName from Metadata for DocumentId {DocumentId}: {FileName}", 
                                        result.Document.DocumentId, searchResult.OriginalFileName);
                                }
                                else
                                {
                                    searchResult.OriginalFileName = $"Document-{result.Document.DocumentId}";
                                    _logger.LogWarning("No OriginalFileName found for DocumentId {DocumentId}, using fallback: {FileName}", 
                                        result.Document.DocumentId, searchResult.OriginalFileName);
                                }
                            }

                            enhancedResults.Add(searchResult);
                        }
                        
                        // If we found good results, don't try more variations
                        if (enhancedResults.Any(r => r.Score > 0.2))
                        {
                            break;
                        }
                    }
                }
                
                if (enhancedResults.Any())
                {
                    // Remove duplicates and sort by score
                    var uniqueResults = enhancedResults
                        .GroupBy(r => r.Id)
                        .Select(g => g.OrderByDescending(r => r.Score).First())
                        .OrderByDescending(r => r.Score)
                        .Take(request.MaxResults)
                        .ToList();
                    
                    _logger.LogInformation("Related topic search found {Count} enhanced results from document-specific searches", 
                        uniqueResults.Count);
                    return uniqueResults;
                }
            }
            
            // Strategy 2: Traditional enhanced search
            List<string> allSearchTerms;
            if (isFollowUpQuestion)
            {
                // For follow-up questions: focus on history keywords and document references
                allSearchTerms = historyKeywords.Take(3)
                    .Concat(documentReferences.Take(2))
                    .Distinct()
                    .ToList();
            }
            else
            {
                // For new questions: use full context
                allSearchTerms = historyKeywords.Concat(documentReferences).Distinct().ToList();
            }
            
            var enhancedQuery = CombineQueryWithHistoryContext(searchQuery, allSearchTerms);

            _logger.LogDebug("Enhanced query with history context: '{EnhancedQuery}' (IsFollowUp: {IsFollowUp}, Keywords: {HistoryCount}, Doc refs: {DocCount})", 
                enhancedQuery, isFollowUpQuestion, historyKeywords.Count, documentReferences.Count);

            // For follow-up questions, use more focused search parameters
            int searchMultiplier = isFollowUpQuestion ? 3 : 6; // Reduced multiplier for follow-ups
            int maxResults = isFollowUpQuestion ? Math.Min(request.MaxResults * 2, 50) : Math.Min(request.MaxResults * 4, 100);

            // Perform a broader search with relaxed constraints
            var searchResults = request.UseSemanticSearch 
                ? await _searchService.HybridSearchAsync(enhancedQuery, queryEmbedding, request.MaxResults * searchMultiplier, request.DocumentId)
                : await _searchService.SearchAsync(enhancedQuery, maxResults);

            var resultsList = searchResults.GetResults().ToList();
            
            if (!resultsList.Any())
            {
                _logger.LogDebug("No results found with enhanced query, trying simpler fallback");
                // Fallback to simple search if enhanced search fails
                searchResults = request.UseSemanticSearch 
                    ? await _searchService.HybridSearchAsync(request.Query, queryEmbedding, request.MaxResults * 2, request.DocumentId)
                    : await _searchService.SearchAsync(request.Query, request.MaxResults * 2);
                    
                resultsList = searchResults.GetResults().ToList();
            }

            // If still no results and we have document references, try searching with document names + current query
            if (!resultsList.Any() && documentReferences.Any())
            {
                foreach (var docRef in documentReferences.Take(3))
                {
                    var docSpecificQuery = $"{searchQuery} {docRef}";
                    _logger.LogDebug("Trying document-specific search: '{DocQuery}'", docSpecificQuery);
                    
                    var docEmbedding = await _embeddingService.GenerateEmbeddingAsync(docSpecificQuery);
                    var docSearchResults = request.UseSemanticSearch 
                        ? await _searchService.HybridSearchAsync(docSpecificQuery, docEmbedding, request.MaxResults * 2, request.DocumentId)
                        : await _searchService.SearchAsync(docSpecificQuery, request.MaxResults);
                    
                    var docResults = docSearchResults.GetResults().ToList();
                    if (docResults.Any())
                    {
                        resultsList.AddRange(docResults);
                        _logger.LogDebug("Document-specific search for '{DocRef}' found {Count} results", docRef, docResults.Count);
                    }
                }
            }

            if (!resultsList.Any())
            {
                _logger.LogInformation("Enhanced search with history context found no results");
                return new List<SearchResult>();
            }

            // Bulk load metadata for all unique documents
            var uniqueDocumentIds = resultsList
                .Select(r => r.Document.DocumentId)
                .Distinct()
                .ToList();

            var metadataBulk = await LoadMetadataBulkAsync(uniqueDocumentIds);

            // Create SearchResult objects with enhanced relevance scoring
            var results = new List<SearchResult>();
            
            foreach (var result in resultsList)
            {
                double vectorScore = result.Score ?? 0.0;
                
                // Enhanced scoring for history-contextualized searches
                double combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                    result.Document.Content, 
                    request.Query, 
                    vectorScore);

                // Boost score if content relates to terms from chat history
                if (ContainsHistoryContext(result.Document.Content, historyKeywords))
                {
                    combinedScore *= 1.2; // 20% boost for history relevance
                }

                // Additional boost if document was specifically referenced in chat history
                if (DocumentIsReferencedInHistory(result.Document.DocumentId, result.Document.Metadata, documentReferences))
                {
                    combinedScore *= 1.3; // 30% boost for document history relevance
                    _logger.LogDebug("Applied document reference boost to {DocumentId}", result.Document.DocumentId);
                }

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
                }

                // Ensure OriginalFileName is never null for enhanced search results
                if (string.IsNullOrEmpty(searchResult.OriginalFileName))
                {
                    // Try to extract from Metadata field as last resort
                    if (!string.IsNullOrEmpty(result.Document.Metadata) && result.Document.Metadata.StartsWith("File: "))
                    {
                        searchResult.OriginalFileName = result.Document.Metadata.Substring(6); // Remove "File: " prefix
                        _logger.LogDebug("Extracted OriginalFileName from Metadata for DocumentId {DocumentId}: {FileName}", 
                            result.Document.DocumentId, searchResult.OriginalFileName);
                    }
                    else
                    {
                        searchResult.OriginalFileName = $"Document-{result.Document.DocumentId}";
                        _logger.LogWarning("No OriginalFileName found for DocumentId {DocumentId}, using fallback: {FileName}", 
                            result.Document.DocumentId, searchResult.OriginalFileName);
                    }
                }

                results.Add(searchResult);
            }

            // Apply relevance filtering and diversification (using same logic as main search)
            var filteredResults = FilterResults(results, request);

            // Apply source diversification (max 1 chunk per document) and limit to MaxSourcesForAnswer
            var maxSources = _configuration.GetValue<int>("ChatService:MaxSourcesForAnswer", 5);
            var diversifiedResults = filteredResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                .OrderByDescending(r => r.Score)
                .Take(Math.Min(request.MaxResults, maxSources)) // Limit by both MaxResults and MaxSourcesForAnswer
                .ToList();

            _logger.LogInformation("Enhanced search with history returned {Count} diversified results", diversifiedResults.Count);
            return diversifiedResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in enhanced search with chat history");
            return new List<SearchResult>();
        }
    }

    /// <summary>
    /// Extracts relevant keywords from chat history with decay for older messages
    /// </summary>
    private List<string> ExtractKeywordsFromChatHistory(List<ChatMessage>? chatHistory)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var keywordWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        
        // Get recent messages but limit to last 3 for follow-up questions (instead of 5)
        var recentMessages = chatHistory.TakeLast(3).ToList();
        
        for (int i = 0; i < recentMessages.Count; i++)
        {
            var message = recentMessages[i];
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            // Apply decay factor: newer messages get higher weight
            double messageWeight = Math.Pow(0.7, recentMessages.Count - i - 1); // 1.0, 0.7, 0.49

            // Extract potential keywords (words longer than 3 characters)
            var words = message.Content
                .Split(new[] { ' ', '\n', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3 && !IsStopWord(w) && !IsFollowUpWord(w))
                .Take(8); // Reduced from 10

            foreach (var word in words)
            {
                var cleanWord = word.Trim();
                if (keywordWeights.ContainsKey(cleanWord))
                {
                    keywordWeights[cleanWord] += messageWeight;
                }
                else
                {
                    keywordWeights[cleanWord] = messageWeight;
                }
            }
        }

        // Return top keywords by weight, limited to 8 (reduced from 15)
        return keywordWeights
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Extracts document IDs and filenames from chat history sources with improved detection
    /// </summary>
    private List<string> ExtractDocumentReferencesFromChatHistory(List<ChatMessage>? chatHistory)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var documentReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Get recent assistant messages that might contain source references
        var recentAssistantMessages = chatHistory
            .Where(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3);
        
        _logger.LogDebug("Extracting document references from {MessageCount} assistant messages", recentAssistantMessages.Count());
        
        foreach (var message in recentAssistantMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            _logger.LogDebug("Processing assistant message content: {ContentPreview}", 
                message.Content.Length > 200 ? message.Content.Substring(0, 200) + "..." : message.Content);

            // Strategy 1: Look for source references in German format
            var lines = message.Content.Split('\n');
            bool inSourcesSection = false;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Check if we're in the sources section
                if (trimmedLine.StartsWith("**Quellen:**", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("Quellen:", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("**Quelle", StringComparison.OrdinalIgnoreCase))
                {
                    inSourcesSection = true;
                    _logger.LogDebug("Found sources section: {Line}", trimmedLine);
                    continue;
                }
                
                // Stop if we reach another section
                if (inSourcesSection && trimmedLine.StartsWith("**") && !trimmedLine.Contains("Quelle"))
                {
                    inSourcesSection = false;
                }
                
                // Extract from sources section
                if (inSourcesSection && !string.IsNullOrEmpty(trimmedLine))
                {
                    // Simple extraction: Look for document patterns
                    var docMatches = System.Text.RegularExpressions.Regex.Matches(trimmedLine, 
                        @"([a-zA-Z0-9\-_]+\.(?:pdf|docx?|txt|md))", 
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    
                    foreach (System.Text.RegularExpressions.Match match in docMatches)
                    {
                        var docName = match.Groups[1].Value.Trim();
                        if (docName.Length > 3 && docName.Length < 100)
                        {
                            documentReferences.Add(docName);
                        }
                    }
                }
            }
        }

        var results = documentReferences.Take(5).ToList();
        _logger.LogInformation("Extracted {Count} document references from chat history: {References}", 
            results.Count, string.Join(", ", results));
        
        if (results.Count == 0)
        {
            _logger.LogWarning("No document references extracted from chat history. Recent assistant messages content:");
            foreach (var message in recentAssistantMessages)
            {
                var contentLength = message.Content?.Length ?? 0;
                var maxLength = Math.Min(300, contentLength);
                _logger.LogWarning("Assistant message: {Content}", message.Content?.Substring(0, maxLength));
            }
        }

        return results;
    }

    /// <summary>
    /// Extracts document names from a source line
    /// </summary>
    private void ExtractDocumentNameFromSourceLine(string line, HashSet<string> documentReferences)
    {
        // Pattern 1: [DocumentName.pdf]
        var bracketMatches = System.Text.RegularExpressions.Regex.Matches(line, @"\[([^\]]+\.(?:pdf|docx|md))\]", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in bracketMatches)
        {
            var filename = match.Groups[1].Value.Trim();
            documentReferences.Add(filename);
            documentReferences.Add(Path.GetFileNameWithoutExtension(filename));
            _logger.LogDebug("Found bracketed document: {Document}", filename);
        }
        
        // Pattern 2: *DocumentName.md* (markdown formatting)
        var asteriskMatches = System.Text.RegularExpressions.Regex.Matches(line, @"\*([^\*]+\.(?:pdf|docx|md))\*", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in asteriskMatches)
        {
            var filename = match.Groups[1].Value.Trim();
            documentReferences.Add(filename);
            documentReferences.Add(Path.GetFileNameWithoutExtension(filename));
            _logger.LogDebug("Found asterisk document: {Document}", filename);
        }
        
        // Pattern 3: DocumentName.pdf: description
        var colonMatches = System.Text.RegularExpressions.Regex.Matches(line, @"([^:\[\]\*]+\.(?:pdf|docx|md)):", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in colonMatches)
        {
            var filename = match.Groups[1].Value.Trim();
            documentReferences.Add(filename);
            documentReferences.Add(Path.GetFileNameWithoutExtension(filename));
            _logger.LogDebug("Found colon-separated document: {Document}", filename);
        }
    }

    /// <summary>
    /// Extracts document names from regular text
    /// </summary>
    private void ExtractDocumentNamesFromText(string text, HashSet<string> documentReferences)
    {
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"\b([A-Za-zÄÖÜäöüß][A-Za-zÄÖÜäöüß0-9_\-\s]+\.(?:pdf|docx|md))\b", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var filename = match.Groups[1].Value.Trim();
            if (filename.Length > 5 && filename.Length < 100) // Reasonable filename length
            {
                documentReferences.Add(filename);
                documentReferences.Add(Path.GetFileNameWithoutExtension(filename));
                _logger.LogDebug("Found document in text: {Document}", filename);
            }
        }
    }

    /// <summary>
    /// Extracts inline document references like "Laut Azure Resource Abbreviations"
    /// </summary>
    private void ExtractInlineDocumentReferences(string content, HashSet<string> documentReferences)
    {
        // Look for patterns like "Laut [Document]", "In [Document]", "Gemäß [Document]"
        var patterns = new[]
        {
            @"(?:Laut|In|Gemäß|Nach|Basierend auf)\s+([A-Za-zÄÖÜäöüß][A-Za-zÄÖÜäöüß0-9_\-\s]{5,50})",
            @"(?:dem|der|das)\s+(?:Dokument|Quelle)\s+([A-Za-zÄÖÜäöüß][A-Za-zÄÖÜäöüß0-9_\-\s]{5,50})"
        };
        
        foreach (var pattern in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(content, pattern, 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                var reference = match.Groups[1].Value.Trim();
                if (!string.IsNullOrEmpty(reference))
                {
                    documentReferences.Add(reference);
                    _logger.LogDebug("Found inline document reference: {Reference}", reference);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a document is referenced in the chat history
    /// </summary>
    private bool DocumentIsReferencedInHistory(string documentId, string? metadata, List<string> documentReferences)
    {
        if (!documentReferences.Any()) return false;

        // Check against document ID
        if (documentReferences.Any(dr => documentId.Contains(dr, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check against metadata/filename (simplified)
        if (!string.IsNullOrEmpty(metadata))
        {
            if (documentReferences.Any(dr => 
                metadata.Contains(dr, StringComparison.OrdinalIgnoreCase) ||
                dr.Contains(metadata, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if a word is a typical follow-up question word that shouldn't be used for search
    /// </summary>
    private bool IsFollowUpWord(string word)
    {
        var followUpWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // German follow-up words (only connector/question words, not content terms like "beispiel")
            "mehr", "weitere", "andere", "zusätzliche", "gehe", "bitte", "kannst", "könntest", 
            "würdest", "sagen", "erzählen", "erklären", "erläutern", "beschreiben", 
            // English follow-up words  
            "more", "additional", "further", "other", "please", "could", "would", "tell", 
            "explain", "describe", "elaborate"
        };

        return followUpWords.Contains(word);
    }

    /// <summary>
    /// Determines if the current query is a follow-up question based on typical patterns
    /// </summary>
    private bool IsFollowUpQuestion(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var queryLower = query.ToLowerInvariant().Trim();
        
        // Only very short queries are likely follow-ups (reduced threshold)
        if (queryLower.Length < 10 || queryLower.Split(' ').Length <= 2)
        {
            return true;
        }

        // German and English follow-up patterns - specific patterns that indicate continuation
        var followUpPatterns = new[]
        {
            // German patterns
            "beispiel", "beispiele", "mehr über", "mehr dazu", "mehr infos", "mehr details", 
            "weitere informationen", "nachteile davon", "vorteile davon", "probleme dabei", 
            "schwierigkeiten", "andere aspekte", "zusätzlich", "außerdem", "darüber hinaus",
            "kannst du", "könntest du", "erklär mir", "sag mir mehr", "gib mir", "zeig mir",
            "was meinst du", "erkläre das", "genauer", "spezifischer", "details",
            
            // English patterns
            "example", "examples", "can you", "could you", "tell me more", "give me", "show me",
            "what do you mean", "explain that", "more about", "more details", "more info",
            "disadvantages", "advantages", "problems with", "issues with", "other aspects",
            "additionally", "furthermore", "more specific", "more precise", "elaborate"
        };

        // Question words at the beginning (check for legitimate standalone questions)
        var questionWords = new[] { 
            // German
            "welche", "welcher", "welches", "was", "wie", "warum", "weshalb", "wo", "wann", "wer",
            // English
            "what", "how", "why", "where", "when", "who", "which"
        };
        bool startsWithQuestionWord = questionWords.Any(qw => queryLower.StartsWith(qw + " "));
        
        // If it starts with a question word and is reasonably long, it's likely a new question, not a follow-up
        if (startsWithQuestionWord && queryLower.Length > 20)
        {
            return false;
        }
        
        // Check for follow-up patterns
        var hasFollowUpPattern = followUpPatterns.Any(pattern => queryLower.Contains(pattern));
        
        return hasFollowUpPattern;
    }

    /// <summary>
    /// Determines if the current query is about a related topic to recent conversation using semantic similarity
    /// </summary>
    private async Task<bool> IsRelatedTopicQuestionAsync(string query, List<ChatMessage>? chatHistory)
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
            
            // Check semantic similarity with recent questions
            foreach (var previousQuestion in recentUserQuestions)
            {
                var previousEmbedding = await _embeddingService.GenerateEmbeddingAsync(previousQuestion);
                
                // Calculate cosine similarity
                var similarity = CalculateCosineSimilarity(queryEmbedding, previousEmbedding);
                
                _logger.LogDebug("Semantic similarity between '{CurrentQuery}' and '{PreviousQuery}': {Similarity:F3}", 
                    query, previousQuestion, similarity);
                
                // If similarity is above threshold, consider it a related topic
                if (similarity >= 0.75) // 75% similarity threshold
                {
                    _logger.LogInformation("Detected related topic question with high semantic similarity ({Similarity:F3}): '{Query}' relates to '{Previous}'", 
                        similarity, query, previousQuestion);
                    return true;
                }
                
                // Lower threshold for questions with similar structure
                if (similarity >= 0.65 && HasSimilarQuestionStructure(query, previousQuestion))
                {
                    _logger.LogInformation("Detected related topic question with moderate similarity + similar structure ({Similarity:F3}): '{Query}' relates to '{Previous}'", 
                        similarity, query, previousQuestion);
                    return true;
                }
            }
            
            _logger.LogDebug("No semantically related topics detected for query: '{Query}'", query);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking semantic similarity for related topics, falling back to simple detection");
            
            // Fallback to simple keyword-based detection
            return HasSimilarKeywords(query, recentUserQuestions);
        }
    }

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
            .Where(w => w.Length > 3 && !IsStopWord(w))
            .ToList();
    }

    /// <summary>
    /// Combines current query with history context, with different strategies for follow-up questions
    /// </summary>
    private string CombineQueryWithHistoryContext(string originalQuery, List<string> searchTerms)
    {
        if (!searchTerms.Any()) return originalQuery;

        // For follow-up questions, be more conservative with term combination
        bool isFollowUp = IsFollowUpQuestion(originalQuery);
        
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
    /// Checks if content contains terms from chat history context
    /// </summary>
    private bool ContainsHistoryContext(string content, List<string> historyKeywords)
    {
        if (!historyKeywords.Any()) return false;
        
        var contentLower = content.ToLowerInvariant();
        return historyKeywords.Any(keyword => contentLower.Contains(keyword.ToLowerInvariant()));
    }

    /// <summary>
    /// Simple stop word check for German and English
    /// </summary>
    private bool IsStopWord(string word)
    {
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // German stop words
            "der", "die", "und", "in", "den", "von", "zu", "das", "mit", "sich", "des", "auf", "für", "ist", "im", "eine", "als", "auch", "dem", "wird", "an", "dass", "kann", "sind", "nach", "nicht", "werden", "bei", "einer", "ein", "war", "hat", "ich", "es", "sie", "haben", "er", "über", "so", "hier", "oder", "was", "aber", "mehr", "aus", "wenn", "nur", "noch", "wie", "bis", "dann", "diese", "um", "vor", "durch", "man", "sein", "soll", "etwa", "alle", "seine", "wo", "unter", "sehr", "alle", "zum", "einem", "könnte", "ihren", "seiner", "zwei", "zwischen", "wieder", "diesem", "hatte", "ihre", "eines", "gegen", "vom", "können", "weitere", "sollte", "seit", "wurde", "während", "diesem", "dazu", "bereits", "dabei",
            // English stop words
            "the", "is", "at", "which", "on", "and", "a", "to", "as", "are", "was", "will", "an", "be", "or", "of", "with", "by", "from", "up", "about", "into", "through", "during", "before", "after", "above", "below", "between", "among", "throughout", "despite", "towards", "upon", "concerning", "within", "without", "through", "during", "before", "after", "above", "below", "up", "down", "in", "out", "on", "off", "over", "under", "again", "further", "then", "once", "here", "there", "when", "where", "why", "how", "all", "any", "both", "each", "few", "more", "most", "other", "some", "such", "no", "nor", "not", "only", "own", "same", "so", "than", "too", "very", "can", "will", "just", "should", "now"
        };

        return stopWords.Contains(word);
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
                // History results already have their boosts applied in SearchWithinRecentlyReferencedDocumentsAsync
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
