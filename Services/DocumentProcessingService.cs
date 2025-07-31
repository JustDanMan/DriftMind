using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

public interface IDocumentProcessingService
{
    Task<UploadTextResponse> ProcessTextAsync(UploadTextRequest request);
}

public class DocumentProcessingService : IDocumentProcessingService
{
    private readonly ITextChunkingService _chunkingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ISearchService _searchService;
    private readonly ILogger<DocumentProcessingService> _logger;

    public DocumentProcessingService(
        ITextChunkingService chunkingService,
        IEmbeddingService embeddingService,
        ISearchService searchService,
        ILogger<DocumentProcessingService> logger)
    {
        _chunkingService = chunkingService;
        _embeddingService = embeddingService;
        _searchService = searchService;
        _logger = logger;
    }

    public async Task<UploadTextResponse> ProcessTextAsync(UploadTextRequest request)
    {
        try
        {
            var documentId = request.DocumentId ?? Guid.NewGuid().ToString();
            
            _logger.LogInformation("Verarbeite Text für Dokument {DocumentId}", documentId);

            // 1. Text in Chunks aufteilen
            var chunks = _chunkingService.ChunkText(request.Text, request.ChunkSize, request.ChunkOverlap);
            
            if (!chunks.Any())
            {
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = "Keine Chunks konnten aus dem Text erstellt werden."
                };
            }

            _logger.LogInformation("Text wurde in {ChunkCount} Chunks aufgeteilt", chunks.Count);

            // 2. Embeddings für alle Chunks generieren
            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(chunks);
            
            _logger.LogInformation("Embeddings für {ChunkCount} Chunks generiert", embeddings.Count);

            // 3. DocumentChunk-Objekte erstellen
            var documentChunks = new List<DocumentChunk>();
            for (int i = 0; i < chunks.Count; i++)
            {
                documentChunks.Add(new DocumentChunk
                {
                    Id = $"{documentId}_{i}",
                    DocumentId = documentId,
                    Content = chunks[i],
                    ChunkIndex = i,
                    Embedding = embeddings[i],
                    Metadata = request.Metadata,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }

            // 4. Chunks in Azure AI Search indexieren
            var indexingSuccess = await _searchService.IndexDocumentChunksAsync(documentChunks);

            if (indexingSuccess)
            {
                _logger.LogInformation("Dokument {DocumentId} erfolgreich verarbeitet und indexiert", documentId);
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    ChunksCreated = chunks.Count,
                    Success = true,
                    Message = $"Text erfolgreich in {chunks.Count} Chunks verarbeitet und indexiert."
                };
            }
            else
            {
                _logger.LogWarning("Indexierung für Dokument {DocumentId} war nicht vollständig erfolgreich", documentId);
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    ChunksCreated = chunks.Count,
                    Success = false,
                    Message = "Text wurde verarbeitet, aber die Indexierung war nicht vollständig erfolgreich."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Verarbeiten des Texts für Dokument {DocumentId}", request.DocumentId);
            return new UploadTextResponse
            {
                DocumentId = request.DocumentId ?? "unknown",
                Success = false,
                Message = $"Fehler bei der Verarbeitung: {ex.Message}"
            };
        }
    }
}
