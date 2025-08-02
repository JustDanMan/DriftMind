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

    public SearchOrchestrationService(
        ISearchService searchService,
        IEmbeddingService embeddingService,
        IChatService chatService,
        ILogger<SearchOrchestrationService> logger)
    {
        _searchService = searchService;
        _embeddingService = embeddingService;
        _chatService = chatService;
        _logger = logger;
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

            // 5. Apply source diversification (max 1 chunk per document)
            var diversifiedResults = filteredResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                .OrderByDescending(r => r.Score)
                .ToList();

            _logger.LogInformation("Diversified {Original} chunks to {Diversified} chunks from {Documents} different documents", 
                filteredResults.Count, diversifiedResults.Count, diversifiedResults.Select(r => r.DocumentId).Distinct().Count());

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
                response.GeneratedAnswer = await _chatService.GenerateAnswerAsync(request.Query, diversifiedResults);
            }
            else if (request.IncludeAnswer && !diversifiedResults.Any())
            {
                response.GeneratedAnswer = "Es konnten keine relevanten Informationen zu Ihrer Frage gefunden werden. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
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

        // For very short queries, be more inclusive
        if (queryLength < 15 || queryTermCount <= 2)
        {
            return results
                .Where(r => r.Score > 0.2 || r.VectorScore > 0.5) // Even more lenient
                .OrderByDescending(r => r.Score)
                .Take(request.MaxResults)
                .ToList();
        }

        // For medium queries, use lenient filtering
        if (queryLength < 50 || queryTermCount <= 5)
        {
            return results
                .Where(r => r.IsRelevant || r.Score > 0.3) // More lenient: IsRelevant OR Score
                .OrderByDescending(r => r.Score)
                .Take(request.MaxResults)
                .ToList();
        }

        // For long/complex queries, still be reasonably lenient
        return results
            .Where(r => r.IsRelevant || r.Score > 0.4) // Changed from AND to OR, reduced threshold
            .OrderByDescending(r => r.Score)
            .Take(request.MaxResults)
            .ToList();
    }
}
