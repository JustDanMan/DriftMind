using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

public interface IDocumentManagementService
{
    Task<DocumentListResponse> GetAllDocumentsAsync(DocumentListRequest request);
    Task<DeleteDocumentResponse> DeleteDocumentAsync(DeleteDocumentRequest request);
}

public class DocumentManagementService : IDocumentManagementService
{
    private readonly ISearchService _searchService;
    private readonly ILogger<DocumentManagementService> _logger;

    public DocumentManagementService(ISearchService searchService, ILogger<DocumentManagementService> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    public async Task<DocumentListResponse> GetAllDocumentsAsync(DocumentListRequest request)
    {
        try
        {
            _logger.LogInformation("Retrieving documents list with parameters: MaxResults={MaxResults}, Skip={Skip}, Filter={Filter}", 
                request.MaxResults, request.Skip, request.DocumentIdFilter);

            Dictionary<string, List<DocumentChunk>> allDocuments;

            if (!string.IsNullOrEmpty(request.DocumentIdFilter))
            {
                // Filter for specific document
                var chunks = await _searchService.GetDocumentChunksAsync(request.DocumentIdFilter);
                allDocuments = new Dictionary<string, List<DocumentChunk>>
                {
                    { request.DocumentIdFilter, chunks }
                };
            }
            else
            {
                // Get all documents with pagination
                allDocuments = await _searchService.GetAllDocumentsAsync(request.MaxResults, request.Skip);
            }

            var documentSummaries = new List<DocumentSummary>();

            foreach (var (documentId, chunks) in allDocuments)
            {
                if (!chunks.Any()) continue;

                // Sort chunks by index to get consistent ordering
                var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
                var firstChunk = sortedChunks.First();
                var lastChunk = sortedChunks.Last();

                // Extract file info from metadata if available
                var fileName = ExtractFileNameFromMetadata(firstChunk.Metadata);
                var fileType = ExtractFileTypeFromMetadata(firstChunk.Metadata, fileName);
                var fileSizeInBytes = ExtractFileSizeFromMetadata(firstChunk.Metadata);

                // Get sample content from first few chunks
                var sampleContent = sortedChunks
                    .Take(3)
                    .Select(c => TruncateContent(c.Content, 150))
                    .Where(content => !string.IsNullOrWhiteSpace(content))
                    .ToList();

                var documentSummary = new DocumentSummary
                {
                    DocumentId = documentId,
                    ChunkCount = chunks.Count,
                    FileName = fileName,
                    FileType = fileType,
                    FileSizeInBytes = fileSizeInBytes,
                    Metadata = firstChunk.Metadata,
                    CreatedAt = firstChunk.CreatedAt,
                    LastUpdated = lastChunk.CreatedAt,
                    SampleContent = sampleContent
                };

                documentSummaries.Add(documentSummary);
            }

            // Sort by creation date (newest first)
            documentSummaries = documentSummaries
                .OrderByDescending(d => d.CreatedAt)
                .ToList();

            var response = new DocumentListResponse
            {
                Documents = documentSummaries,
                TotalDocuments = documentSummaries.Count,
                ReturnedDocuments = documentSummaries.Count,
                Success = true,
                Message = $"Retrieved {documentSummaries.Count} documents successfully."
            };

            _logger.LogInformation("Successfully retrieved {Count} document summaries", documentSummaries.Count);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving documents list");
            return new DocumentListResponse
            {
                Success = false,
                Message = $"Error retrieving documents: {ex.Message}"
            };
        }
    }

    private string? ExtractFileNameFromMetadata(string? metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;

        // Look for "File: filename" pattern
        var filePrefix = "File: ";
        var fileIndex = metadata.IndexOf(filePrefix, StringComparison.OrdinalIgnoreCase);
        if (fileIndex >= 0)
        {
            var startIndex = fileIndex + filePrefix.Length;
            var endIndex = metadata.IndexOf(',', startIndex);
            if (endIndex > startIndex)
            {
                return metadata.Substring(startIndex, endIndex - startIndex).Trim();
            }
            else
            {
                return metadata.Substring(startIndex).Trim();
            }
        }

        return null;
    }

    private string? ExtractFileTypeFromMetadata(string? metadata, string? fileName)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            return Path.GetExtension(fileName);
        }

        return null;
    }

    private long? ExtractFileSizeFromMetadata(string? metadata)
    {
        // This would need to be implemented if file size is stored in metadata
        // For now, return null as we don't store file size in metadata consistently
        return null;
    }

    private string TruncateContent(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
            return content ?? string.Empty;

        var truncated = content.Substring(0, maxLength);
        var lastSpace = truncated.LastIndexOf(' ');
        
        if (lastSpace > maxLength * 0.8) // If we can find a space in the last 20%
        {
            truncated = truncated.Substring(0, lastSpace);
        }

        return truncated + "...";
    }

    public async Task<DeleteDocumentResponse> DeleteDocumentAsync(DeleteDocumentRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.DocumentId))
            {
                return new DeleteDocumentResponse
                {
                    DocumentId = request.DocumentId,
                    Success = false,
                    Message = "Document ID cannot be empty."
                };
            }

            _logger.LogInformation("Attempting to delete document: {DocumentId}", request.DocumentId);

            // First, check if the document exists and get chunk count
            var chunks = await _searchService.GetDocumentChunksAsync(request.DocumentId);
            
            if (!chunks.Any())
            {
                return new DeleteDocumentResponse
                {
                    DocumentId = request.DocumentId,
                    Success = false,
                    Message = $"Document '{request.DocumentId}' not found."
                };
            }

            var chunkCount = chunks.Count;
            _logger.LogInformation("Found {ChunkCount} chunks for document {DocumentId}", chunkCount, request.DocumentId);

            // Delete the document
            var deleteSuccess = await _searchService.DeleteDocumentAsync(request.DocumentId);

            if (deleteSuccess)
            {
                _logger.LogInformation("Successfully deleted document {DocumentId} with {ChunkCount} chunks", 
                    request.DocumentId, chunkCount);
                
                return new DeleteDocumentResponse
                {
                    DocumentId = request.DocumentId,
                    ChunksDeleted = chunkCount,
                    Success = true,
                    Message = $"Document '{request.DocumentId}' successfully deleted. {chunkCount} chunks removed."
                };
            }
            else
            {
                _logger.LogWarning("Failed to delete document {DocumentId}", request.DocumentId);
                
                return new DeleteDocumentResponse
                {
                    DocumentId = request.DocumentId,
                    ChunksDeleted = 0,
                    Success = false,
                    Message = $"Failed to delete document '{request.DocumentId}'. Some chunks may not have been removed."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting document: {DocumentId}", request.DocumentId);
            
            return new DeleteDocumentResponse
            {
                DocumentId = request.DocumentId,
                ChunksDeleted = 0,
                Success = false,
                Message = $"Error deleting document: {ex.Message}"
            };
        }
    }
}
