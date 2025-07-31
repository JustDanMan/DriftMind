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

            // 2. Perform hybrid search
            var searchResults = request.UseSemanticSearch 
                ? await _searchService.HybridSearchAsync(request.Query, queryEmbedding, request.MaxResults, request.DocumentId)
                : await _searchService.SearchAsync(request.Query, request.MaxResults);

            var resultsList = searchResults.GetResults().ToList();
            _logger.LogInformation("Search results received: {ResultCount}", resultsList.Count);

            // 3. Convert to DTOs
            var results = new List<SearchResult>();
            foreach (var result in resultsList)
            {
                results.Add(new SearchResult
                {
                    Id = result.Document.Id,
                    Content = result.Document.Content,
                    DocumentId = result.Document.DocumentId,
                    ChunkIndex = result.Document.ChunkIndex,
                    Score = result.Score ?? 0.0,
                    Metadata = result.Document.Metadata,
                    CreatedAt = result.Document.CreatedAt
                });
            }

            var response = new SearchResponse
            {
                Query = request.Query,
                Results = results,
                Success = true,
                TotalResults = resultsList.Count
            };

            // 4. Generate answer with GPT-4o if requested
            if (request.IncludeAnswer && results.Any())
            {
                _logger.LogInformation("Generating answer with GPT-4o");
                response.GeneratedAnswer = await _chatService.GenerateAnswerAsync(request.Query, results);
            }
            else if (request.IncludeAnswer && !results.Any())
            {
                response.GeneratedAnswer = "No relevant information found for your query.";
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
}
