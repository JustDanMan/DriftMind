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
    private readonly ILogger<SearchOrchestrationService> _logger;
    private readonly IConfiguration _configuration;

    public SearchOrchestrationService(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        IChatService chatService,
        ILogger<SearchOrchestrationService> logger,
        IConfiguration configuration)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _chatService = chatService;
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

            // 1. Generate embedding for the search query
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(request.Query);
            
            _logger.LogInformation("Embedding generated for search query");

            // 2. Perform hybrid search with more results for filtering
            var multiplier = request.Query.Length < 20 ? 4 : 3; // More results for short queries
            var searchResults = request.UseSemanticSearch 
                ? await _searchService.HybridSearchAsync(request.Query, queryEmbedding, request.MaxResults * multiplier, request.DocumentId)
                : await _searchService.SearchAsync(request.Query, Math.Min(request.MaxResults * 2, 50));

            var resultsList = searchResults.GetResults().ToList();
            _logger.LogInformation("Search results received: {ResultCount} for query: '{Query}'", resultsList.Count, request.Query);

            // 3. Convert to DTOs with improved relevance scoring
            var results = new List<SearchResult>();
            foreach (var result in resultsList)
            {
                var vectorScore = result.Score;
                var combinedScore = RelevanceAnalyzer.CalculateRelevanceScore(
                    result.Document.Content, 
                    request.Query, 
                    vectorScore);
                    
                var isRelevant = RelevanceAnalyzer.IsContentRelevant(
                    result.Document.Content, 
                    request.Query, 
                    vectorScore);

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
                    IsRelevant = isRelevant,
                    RelevanceScore = combinedScore,
                    // Add blob storage information
                    BlobPath = result.Document.BlobPath,
                    BlobContainer = result.Document.BlobContainer,
                    OriginalFileName = result.Document.OriginalFileName,
                    ContentType = result.Document.ContentType,
                    TextContentBlobPath = result.Document.TextContentBlobPath,
                    // Add download information if file is available
                    Download = !string.IsNullOrEmpty(result.Document.BlobPath) ? new DownloadInfo
                    {
                        DocumentId = result.Document.DocumentId,
                        TokenEndpoint = $"/download/token",
                        FileName = result.Document.OriginalFileName ?? "unknown",
                        FileType = Path.GetExtension(result.Document.OriginalFileName ?? ""),
                        TokenExpirationMinutes = 15
                    } : null
                });
            }

            // 4. Apply adaptive filtering based on query characteristics
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
        var queryLength = request.Query.Length;
        var queryTermCount = request.Query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        
        // Get configurable minimum score
        var minScore = _configuration.GetValue<double>("ChatService:MinScoreForAnswer", 0.3);

        // For very short queries, be more inclusive
        if (queryLength < 15 || queryTermCount <= 2)
        {
            return results
                .Where(r => r.Score > (minScore * 0.67) || r.VectorScore > 0.5) // Even more lenient (2/3 of minScore)
                .OrderByDescending(r => r.Score)
                .Take(request.MaxResults)
                .ToList();
        }

        // For medium queries, use standard filtering
        if (queryLength < 50 || queryTermCount <= 5)
        {
            return results
                .Where(r => r.IsRelevant || r.Score > minScore) // Use configurable minScore
                .OrderByDescending(r => r.Score)
                .Take(request.MaxResults)
                .ToList();
        }

        // For long/complex queries, use slightly higher threshold
        return results
            .Where(r => r.IsRelevant || r.Score > (minScore * 1.33)) // 1/3 higher than minScore
            .OrderByDescending(r => r.Score)
            .Take(request.MaxResults)
            .ToList();
    }
}
