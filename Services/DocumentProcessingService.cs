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
            
            _logger.LogInformation("Processing text for document {DocumentId}", documentId);

            // 1. Split text into chunks
            var chunks = _chunkingService.ChunkText(request.Text, request.ChunkSize, request.ChunkOverlap);
            
            if (!chunks.Any())
            {
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = "No chunks could be created from the text."
                };
            }

            _logger.LogInformation("Text was split into {ChunkCount} chunks", chunks.Count);

            // 2. Generate embeddings for all chunks
            var embeddings = await _embeddingService.GenerateEmbeddingsAsync(chunks);
            
            _logger.LogInformation("Embeddings generated for {ChunkCount} chunks", embeddings.Count);

            // 3. Create DocumentChunk objects
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

            // 4. Index chunks in Azure AI Search
            var indexingSuccess = await _searchService.IndexDocumentChunksAsync(documentChunks);

            if (indexingSuccess)
            {
                _logger.LogInformation("Document {DocumentId} successfully processed and indexed", documentId);
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    ChunksCreated = chunks.Count,
                    Success = true,
                    Message = $"Text successfully processed into {chunks.Count} chunks and indexed."
                };
            }
            else
            {
                _logger.LogWarning("Indexing for document {DocumentId} was not completely successful", documentId);
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    ChunksCreated = chunks.Count,
                    Success = false,
                    Message = "Text was processed, but indexing was not completely successful."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing text for document {DocumentId}", request.DocumentId);
            return new UploadTextResponse
            {
                DocumentId = request.DocumentId ?? "unknown",
                Success = false,
                Message = $"Error during processing: {ex.Message}"
            };
        }
    }
}
