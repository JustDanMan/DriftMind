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
            _logger.LogInformation("Starte Suche für Query: {Query}", request.Query);

            if (string.IsNullOrWhiteSpace(request.Query))
            {
                return new SearchResponse
                {
                    Query = request.Query,
                    Success = false,
                    Message = "Suchanfrage darf nicht leer sein."
                };
            }

            // 1. Generiere Embedding für die Suchanfrage
            var queryEmbedding = await _embeddingService.GenerateEmbeddingAsync(request.Query);
            
            _logger.LogInformation("Embedding für Suchanfrage generiert");

            // 2. Führe Hybrid-Suche durch
            var searchResults = request.UseSemanticSearch 
                ? await _searchService.HybridSearchAsync(request.Query, queryEmbedding, request.MaxResults, request.DocumentId)
                : await _searchService.SearchAsync(request.Query, request.MaxResults);

            var resultsList = searchResults.GetResults().ToList();
            _logger.LogInformation("Suchergebnisse erhalten: {ResultCount}", resultsList.Count);

            // 3. Konvertiere zu DTOs
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

            // 4. Generiere Antwort mit GPT-4o falls gewünscht
            if (request.IncludeAnswer && results.Any())
            {
                _logger.LogInformation("Generiere Antwort mit GPT-4o");
                response.GeneratedAnswer = await _chatService.GenerateAnswerAsync(request.Query, results);
            }
            else if (request.IncludeAnswer && !results.Any())
            {
                response.GeneratedAnswer = "Keine relevanten Informationen zu Ihrer Anfrage gefunden.";
            }

            _logger.LogInformation("Suche erfolgreich abgeschlossen");
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler bei der Suche für Query: {Query}", request.Query);
            return new SearchResponse
            {
                Query = request.Query,
                Success = false,
                Message = $"Fehler bei der Suche: {ex.Message}"
            };
        }
    }
}
