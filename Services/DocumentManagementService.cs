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

            // Determine target document IDs
            List<string> targetDocumentIds;
            if (!string.IsNullOrEmpty(request.DocumentIdFilter))
            {
                targetDocumentIds = new List<string> { request.DocumentIdFilter };
            }
            else
            {
                var allIds = await _searchService.GetAllDocumentIdsAsync();
                targetDocumentIds = allIds.Skip(Math.Max(0, request.Skip)).Take(Math.Min(request.MaxResults, 100)).ToList();
            }

            if (!targetDocumentIds.Any())
            {
                return new DocumentListResponse
                {
                    Documents = new List<DocumentSummary>(),
                    TotalDocuments = 0,
                    ReturnedDocuments = 0,
                    Success = true,
                    Message = "Retrieved 0 documents successfully."
                };
            }

            // 1) Bulk load metadata from chunk 0
            var chunk0s = await _searchService.GetChunk0sForDocumentsAsync(targetDocumentIds);
            var metaByDoc = chunk0s.ToDictionary(c => c.DocumentId, c => c);

            // 2) For each document, minimal queries for count/lastUpdated/sample
            var summaries = new List<DocumentSummary>(targetDocumentIds.Count);

            // Potential simple parallelization: run tasks per doc
            var tasks = targetDocumentIds.Select(async docId =>
            {
                metaByDoc.TryGetValue(docId, out var meta);

                var countTask = _searchService.GetChunkCountAsync(docId);
                var lastUpdatedTask = _searchService.GetLastUpdatedAsync(docId);
                var topChunksTask = _searchService.GetTopChunksAsync(docId, 3);

                await Task.WhenAll(countTask, lastUpdatedTask, topChunksTask);

                var sample = topChunksTask.Result
                    .OrderBy(c => c.ChunkIndex)
                    .Select(c => TruncateContent(c.Content, 150))
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                var createdAt = meta?.CreatedAt ?? DateTimeOffset.MinValue;
                var summary = new DocumentSummary
                {
                    DocumentId = docId,
                    ChunkCount = countTask.Result,
                    FileName = meta?.OriginalFileName,
                    FileType = meta?.ContentType,
                    FileSizeBytes = meta?.FileSizeBytes,
                    Metadata = meta?.Metadata,
                    CreatedAt = createdAt,
                    LastUpdated = lastUpdatedTask.Result ?? createdAt,
                    SampleContent = sample
                };

                return summary;
            }).ToList();

            var documentSummaries = (await Task.WhenAll(tasks))
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
