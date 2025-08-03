using DriftMind.DTOs;
using DriftMind.Models;
using System.Linq;

namespace DriftMind.Services;

public interface IDocumentManagementService
{
    Task<DocumentListResponse> GetAllDocumentsAsync(DocumentListRequest request);
    Task<DeleteDocumentResponse> DeleteDocumentAsync(DeleteDocumentRequest request);
}

public class DocumentManagementService : IDocumentManagementService
{
    private readonly ISearchService _searchService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly ILogger<DocumentManagementService> _logger;

    public DocumentManagementService(
        ISearchService searchService, 
        IBlobStorageService blobStorageService,
        ILogger<DocumentManagementService> logger)
    {
        _searchService = searchService;
        _blobStorageService = blobStorageService;
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

                // Get document metadata from first chunk (ChunkIndex = 0)
                // All metadata is stored only in the first chunk to avoid redundancy
                var fileName = firstChunk.OriginalFileName;
                var fileType = firstChunk.ContentType;
                var fileSizeBytes = firstChunk.FileSizeBytes;

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
                    FileSizeBytes = fileSizeBytes,
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

            // Collect blob paths for deletion
            var blobPaths = new HashSet<string>();
            var textContentBlobPaths = new HashSet<string>();
            
            foreach (var chunk in chunks)
            {
                if (!string.IsNullOrEmpty(chunk.BlobPath))
                {
                    blobPaths.Add(chunk.BlobPath);
                }
                if (!string.IsNullOrEmpty(chunk.TextContentBlobPath))
                {
                    textContentBlobPaths.Add(chunk.TextContentBlobPath);
                }
            }

            _logger.LogInformation("Found {BlobCount} original blob files and {TextBlobCount} text content blobs to delete for document {DocumentId}", 
                blobPaths.Count, textContentBlobPaths.Count, request.DocumentId);

            // Delete the document from Azure AI Search first
            var deleteSuccess = await _searchService.DeleteDocumentAsync(request.DocumentId);

            if (!deleteSuccess)
            {
                _logger.LogWarning("Failed to delete document {DocumentId} from search index", request.DocumentId);
                
                return new DeleteDocumentResponse
                {
                    DocumentId = request.DocumentId,
                    ChunksDeleted = 0,
                    Success = false,
                    Message = $"Failed to delete document '{request.DocumentId}' from search index."
                };
            }

            // Delete blob files (original files)
            var blobDeletionResults = new List<string>();
            foreach (var blobPath in blobPaths)
            {
                try
                {
                    var blobDeleteSuccess = await _blobStorageService.DeleteFileAsync(blobPath);
                    if (blobDeleteSuccess)
                    {
                        blobDeletionResults.Add($"✓ Deleted blob: {blobPath}");
                        _logger.LogInformation("Successfully deleted blob file: {BlobPath}", blobPath);
                    }
                    else
                    {
                        blobDeletionResults.Add($"✗ Failed to delete blob: {blobPath}");
                        _logger.LogWarning("Failed to delete blob file: {BlobPath}", blobPath);
                    }
                }
                catch (Exception ex)
                {
                    blobDeletionResults.Add($"✗ Error deleting blob: {blobPath} - {ex.Message}");
                    _logger.LogError(ex, "Error deleting blob file: {BlobPath}", blobPath);
                }
            }

            // Delete text content blob files (extracted text from PDF/Word)
            foreach (var textBlobPath in textContentBlobPaths)
            {
                try
                {
                    var textBlobDeleteSuccess = await _blobStorageService.DeleteFileAsync(textBlobPath);
                    if (textBlobDeleteSuccess)
                    {
                        blobDeletionResults.Add($"✓ Deleted text content blob: {textBlobPath}");
                        _logger.LogInformation("Successfully deleted text content blob: {TextBlobPath}", textBlobPath);
                    }
                    else
                    {
                        blobDeletionResults.Add($"✗ Failed to delete text content blob: {textBlobPath}");
                        _logger.LogWarning("Failed to delete text content blob: {TextBlobPath}", textBlobPath);
                    }
                }
                catch (Exception ex)
                {
                    blobDeletionResults.Add($"✗ Error deleting text content blob: {textBlobPath} - {ex.Message}");
                    _logger.LogError(ex, "Error deleting text content blob: {TextBlobPath}", textBlobPath);
                }
            }

            var totalBlobs = blobPaths.Count + textContentBlobPaths.Count;
            var successfulBlobDeletions = blobDeletionResults.Count(r => r.StartsWith("✓"));

            _logger.LogInformation("Document {DocumentId} deletion summary: Search index deleted, {SuccessfulBlobs}/{TotalBlobs} blob files deleted", 
                request.DocumentId, successfulBlobDeletions, totalBlobs);

            var message = $"Document '{request.DocumentId}' deleted from search index. {chunkCount} chunks removed.";
            if (totalBlobs > 0)
            {
                message += $" Blob storage: {successfulBlobDeletions}/{totalBlobs} files deleted.";
                if (blobDeletionResults.Any())
                {
                    message += $"\nDetails:\n{string.Join("\n", blobDeletionResults)}";
                }
            }

            return new DeleteDocumentResponse
            {
                DocumentId = request.DocumentId,
                ChunksDeleted = chunkCount,
                Success = true,
                Message = message
            };
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
