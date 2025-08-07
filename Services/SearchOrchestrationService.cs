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
    /// Performs enhanced search using chat history context to find previously referenced documents
    /// </summary>
    private async Task<List<SearchResult>> TryEnhancedSearchWithHistoryAsync(SearchRequest request, string searchQuery, IReadOnlyList<float> queryEmbedding)
    {
        try
        {
            _logger.LogInformation("Attempting enhanced search with chat history context");

            // Determine if this is a follow-up question
            bool isFollowUpQuestion = IsFollowUpQuestion(request.Query);
            
            // Extract keywords and document references from chat history with reduced scope for follow-ups
            var historyKeywords = ExtractKeywordsFromChatHistory(request.ChatHistory);
            var documentReferences = ExtractDocumentReferencesFromChatHistory(request.ChatHistory);
            
            // For follow-up questions, prioritize current query terms over history accumulation
            List<string> allSearchTerms;
            if (isFollowUpQuestion)
            {
                var followUpTerms = ExtractFollowUpTerms(request.Query);
                // For follow-ups: prioritize current query + relevant document references, limit history keywords
                allSearchTerms = followUpTerms
                    .Concat(documentReferences.Take(2)) // Limit document references
                    .Concat(historyKeywords.Take(3))    // Significantly limit history keywords
                    .Distinct()
                    .ToList();
            }
            else
            {
                // For new questions: use full context
                var followUpTerms = ExtractFollowUpTerms(request.Query);
                allSearchTerms = historyKeywords.Concat(followUpTerms).Concat(documentReferences).Distinct().ToList();
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
                // Try with original query terms from history
                var originalTerms = ExtractOriginalQueryTermsFromHistory(request.ChatHistory);
                if (originalTerms.Any())
                {
                    var historyBasedQuery = string.Join(" ", originalTerms);
                    _logger.LogDebug("Trying search with original history terms: '{HistoryQuery}'", historyBasedQuery);
                    
                    var historyEmbedding = await _embeddingService.GenerateEmbeddingAsync(historyBasedQuery);
                    searchResults = request.UseSemanticSearch 
                        ? await _searchService.HybridSearchAsync(historyBasedQuery, historyEmbedding, request.MaxResults * 4, request.DocumentId)
                        : await _searchService.SearchAsync(historyBasedQuery, Math.Min(request.MaxResults * 3, 75));
                    
                    resultsList = searchResults.GetResults().ToList();
                }
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

                // Add metadata if available
                if (metadataBulk.TryGetValue(result.Document.DocumentId, out var metadata))
                {
                    searchResult.BlobPath = metadata.BlobPath;
                    searchResult.BlobContainer = metadata.BlobContainer;
                    searchResult.OriginalFileName = metadata.OriginalFileName;
                    searchResult.ContentType = metadata.ContentType;
                    searchResult.TextContentBlobPath = metadata.TextContentBlobPath;
                    searchResult.FileSizeBytes = metadata.FileSizeBytes;
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
    /// Extracts document IDs and filenames from chat history sources - limited for follow-up questions
    /// </summary>
    private List<string> ExtractDocumentReferencesFromChatHistory(List<ChatMessage>? chatHistory)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var documentReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Get recent assistant messages (last 2 instead of 3) that might contain source references
        var recentAssistantMessages = chatHistory
            .Where(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .TakeLast(2); // Reduced from 3 to 2
        
        foreach (var message in recentAssistantMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            // Look for source references in German format: "Quelle 1:", "Laut Quelle", etc.
            var lines = message.Content.Split('\n');
            bool inSourcesSection = false;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Check if we're in the sources section
                if (trimmedLine.StartsWith("**Quellen:**", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("Quellen:", StringComparison.OrdinalIgnoreCase))
                {
                    inSourcesSection = true;
                    continue;
                }
                
                // If we're in sources section, extract document names
                if (inSourcesSection && trimmedLine.StartsWith("- **Quelle", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract filename from source line
                    // Format: "- **Quelle 1:** [DocumentName.pdf] - [Topic]"
                    var parts = trimmedLine.Split(new[] { '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var filename = parts[1].Trim();
                        if (!string.IsNullOrEmpty(filename))
                        {
                            documentReferences.Add(filename);
                            
                            // Also add filename without extension for broader matching
                            var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
                            if (!string.IsNullOrEmpty(nameWithoutExt))
                            {
                                documentReferences.Add(nameWithoutExt);
                            }
                        }
                    }
                }
                
                // Also look for inline source references like "Laut Quelle 1"
                if (trimmedLine.Contains("Quelle ", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract any capitalized words that might be document names
                    var words = trimmedLine.Split(' ');
                    foreach (var word in words)
                    {
                        if (word.Length > 4 && char.IsUpper(word[0]) && 
                            !word.StartsWith("Quelle", StringComparison.OrdinalIgnoreCase) &&
                            !word.StartsWith("Laut", StringComparison.OrdinalIgnoreCase))
                        {
                            documentReferences.Add(word.Trim(':', '.', ',', ';'));
                        }
                    }
                }
            }
        }

        _logger.LogDebug("Extracted {Count} document references from chat history: {References}", 
            documentReferences.Count, string.Join(", ", documentReferences.Take(3))); // Reduced logging from 5 to 3

        return documentReferences.Take(5).ToList(); // Limit to top 5 document references
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

        // Check against metadata (filename) - handle different metadata formats safely
        if (!string.IsNullOrEmpty(metadata))
        {
            try
            {
                // Try to parse as JSON first
                if (metadata.StartsWith("{") && metadata.EndsWith("}"))
                {
                    var metadataObj = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
                    if (metadataObj?.TryGetValue("originalFileName", out var filenameObj) == true)
                    {
                        var filename = filenameObj.ToString();
                        if (!string.IsNullOrEmpty(filename) && CheckFilenameMatch(filename, documentReferences))
                            return true;
                    }
                }
                else
                {
                    // If not JSON, treat metadata as plain text filename
                    if (CheckFilenameMatch(metadata, documentReferences))
                        return true;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // If JSON parsing fails, treat metadata as plain text
                if (CheckFilenameMatch(metadata, documentReferences))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a filename matches any document reference
    /// </summary>
    private bool CheckFilenameMatch(string filename, List<string> documentReferences)
    {
        if (string.IsNullOrEmpty(filename)) return false;

        // Direct filename check
        if (documentReferences.Any(dr => filename.Contains(dr, StringComparison.OrdinalIgnoreCase) ||
                                         dr.Contains(filename, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Check filename without extension
        var nameWithoutExt = Path.GetFileNameWithoutExtension(filename);
        if (!string.IsNullOrEmpty(nameWithoutExt) && 
            documentReferences.Any(dr => dr.Equals(nameWithoutExt, StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    /// <summary>
    /// Extracts original query terms that were used in previous searches
    /// </summary>
    private List<string> ExtractOriginalQueryTermsFromHistory(List<ChatMessage>? chatHistory)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var queryTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Look for user questions in recent history
        var userMessages = chatHistory
            .Where(m => m.Role.ToLower() == "user")
            .TakeLast(3)
            .Select(m => m.Content);

        foreach (var message in userMessages)
        {
            if (string.IsNullOrWhiteSpace(message)) continue;

            // Extract meaningful terms from user questions
            var terms = message
                .Split(new[] { ' ', '?', '!', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(t => t.Length > 2 && !IsStopWord(t))
                .Take(8);

            foreach (var term in terms)
            {
                queryTerms.Add(term.Trim());
            }
        }

        return queryTerms.ToList();
    }

    /// <summary>
    /// Extracts terms from follow-up questions that might help find related documents
    /// </summary>
    private List<string> ExtractFollowUpTerms(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<string>();

        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        // Extract meaningful terms from the current query
        var words = query
            .Split(new[] { ' ', ',', '.', '?', '!', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !IsStopWord(w) && !IsFollowUpWord(w))
            .Take(5); // Limit to prevent too many terms

        foreach (var word in words)
        {
            terms.Add(word.Trim());
        }

        return terms.ToList();
    }

    /// <summary>
    /// Checks if a word is a typical follow-up question word that shouldn't be used for search
    /// </summary>
    private bool IsFollowUpWord(string word)
    {
        var followUpWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // German follow-up words (only connector/question words, not content terms)
            "mehr", "weitere", "andere", "zusätzliche", "gehe", "bitte", "kannst", "könntest", "würdest", "sagen", "erzählen", "erklären", "erläutern", "beschreiben", "Details", "Einzelheiten", "Aspekte", "Seiten", "Punkte",
            // English follow-up words  
            "more", "additional", "further", "other", "please", "could", "would", "tell", "explain", "describe", "elaborate", "details", "aspects"
        };

        return followUpWords.Contains(word);
    }

    /// <summary>
    /// Determines if the current query is a follow-up question based on typical patterns
    /// </summary>
    private bool IsFollowUpQuestion(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var queryLower = query.ToLowerInvariant();
        
        // German follow-up patterns
        var followUpPatterns = new[]
        {
            "gehe mehr", "mehr auf", "weitere", "zusätzliche", "andere aspekte", "andere seiten", 
            "nachteile", "vorteile", "probleme", "schwierigkeiten", "details", "einzelheiten",
            "kannst du", "könntest du", "würdest du", "bitte", "erklär", "beschreib", "erläuter",
            "was sind", "welche sind", "wie sieht", "wie ist"
        };

        // English follow-up patterns
        var englishPatterns = new[]
        {
            "tell me more", "more about", "additional", "further", "other aspects", "downsides", 
            "advantages", "disadvantages", "problems", "issues", "details", "could you", "would you",
            "please", "explain", "describe", "elaborate", "what are", "which are", "how is"
        };

        var allPatterns = followUpPatterns.Concat(englishPatterns);
        
        return allPatterns.Any(pattern => queryLower.Contains(pattern));
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
