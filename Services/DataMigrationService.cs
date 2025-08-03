using DriftMind.Models;

namespace DriftMind.Services;

public interface IDataMigrationService
{
    Task<bool> MigrateToOptimizedMetadataStorageAsync();
    Task<bool> FixContentTypesAsync();
}

public class DataMigrationService : IDataMigrationService
{
    private readonly ISearchService _searchService;
    private readonly ILogger<DataMigrationService> _logger;

    public DataMigrationService(
        ISearchService searchService,
        ILogger<DataMigrationService> logger)
    {
        _searchService = searchService;
        _logger = logger;
    }

    /// <summary>
    /// Migrates existing documents to store metadata only in chunk 0 (ChunkIndex = 0).
    /// This reduces redundant storage by ~98% for document metadata.
    /// </summary>
    public async Task<bool> MigrateToOptimizedMetadataStorageAsync()
    {
        try
        {
            _logger.LogInformation("Starting migration to optimized metadata storage (Option 2: metadata only in chunk 0)");

            // Get all documents
            var allDocuments = await _searchService.GetAllDocumentsAsync(1000, 0);
            int totalDocuments = allDocuments.Count;
            int processedDocuments = 0;
            int totalChunksProcessed = 0;
            int totalChunksOptimized = 0;

            _logger.LogInformation("Found {TotalDocuments} documents to migrate", totalDocuments);

            foreach (var (documentId, chunks) in allDocuments)
            {
                if (!chunks.Any()) continue;

                var sortedChunks = chunks.OrderBy(c => c.ChunkIndex).ToList();
                var firstChunk = sortedChunks.First();
                
                _logger.LogDebug("Processing document {DocumentId} with {ChunkCount} chunks", 
                    documentId, sortedChunks.Count);

                // Ensure first chunk has all metadata
                bool firstChunkUpdated = false;
                if (string.IsNullOrEmpty(firstChunk.OriginalFileName) && sortedChunks.Count > 1)
                {
                    // Find metadata from any chunk that has it
                    var chunkWithMetadata = sortedChunks.FirstOrDefault(c => 
                        !string.IsNullOrEmpty(c.OriginalFileName) ||
                        !string.IsNullOrEmpty(c.ContentType) ||
                        c.FileSizeBytes.HasValue);

                    if (chunkWithMetadata != null)
                    {
                        firstChunk.OriginalFileName = chunkWithMetadata.OriginalFileName;
                        firstChunk.ContentType = chunkWithMetadata.ContentType;
                        firstChunk.FileSizeBytes = chunkWithMetadata.FileSizeBytes;
                        firstChunk.BlobPath = chunkWithMetadata.BlobPath;
                        firstChunk.BlobContainer = chunkWithMetadata.BlobContainer;
                        firstChunk.TextContentBlobPath = chunkWithMetadata.TextContentBlobPath;
                        firstChunkUpdated = true;
                    }
                }

                // Clear metadata from all other chunks (index > 0)
                var chunksToUpdate = new List<DocumentChunk>();
                if (firstChunkUpdated)
                {
                    chunksToUpdate.Add(firstChunk);
                }

                foreach (var chunk in sortedChunks.Where(c => c.ChunkIndex > 0))
                {
                    bool chunkNeedsUpdate = false;

                    if (!string.IsNullOrEmpty(chunk.OriginalFileName))
                    {
                        chunk.OriginalFileName = null;
                        chunkNeedsUpdate = true;
                    }
                    if (!string.IsNullOrEmpty(chunk.ContentType))
                    {
                        chunk.ContentType = null;
                        chunkNeedsUpdate = true;
                    }
                    if (chunk.FileSizeBytes.HasValue)
                    {
                        chunk.FileSizeBytes = null;
                        chunkNeedsUpdate = true;
                    }
                    if (!string.IsNullOrEmpty(chunk.BlobPath))
                    {
                        chunk.BlobPath = null;
                        chunkNeedsUpdate = true;
                    }
                    if (!string.IsNullOrEmpty(chunk.BlobContainer))
                    {
                        chunk.BlobContainer = null;
                        chunkNeedsUpdate = true;
                    }
                    if (!string.IsNullOrEmpty(chunk.TextContentBlobPath))
                    {
                        chunk.TextContentBlobPath = null;
                        chunkNeedsUpdate = true;
                    }

                    if (chunkNeedsUpdate)
                    {
                        chunksToUpdate.Add(chunk);
                        totalChunksOptimized++;
                    }
                }

                // Update chunks if necessary
                if (chunksToUpdate.Any())
                {
                    var updateSuccess = await _searchService.IndexDocumentChunksAsync(chunksToUpdate);
                    if (updateSuccess)
                    {
                        _logger.LogDebug("Successfully optimized {ChunkCount} chunks for document {DocumentId}", 
                            chunksToUpdate.Count, documentId);
                    }
                    else
                    {
                        _logger.LogWarning("Failed to update chunks for document {DocumentId}", documentId);
                    }
                }

                totalChunksProcessed += sortedChunks.Count;
                processedDocuments++;

                // Log progress every 10 documents
                if (processedDocuments % 10 == 0)
                {
                    _logger.LogInformation("Migration progress: {ProcessedDocuments}/{TotalDocuments} documents processed",
                        processedDocuments, totalDocuments);
                }
            }

            _logger.LogInformation("Migration completed successfully! " +
                "Processed {ProcessedDocuments} documents, " +
                "{TotalChunksProcessed} total chunks, " +
                "{OptimizedChunks} chunks optimized (metadata removed from non-first chunks)",
                processedDocuments, totalChunksProcessed, totalChunksOptimized);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during migration to optimized metadata storage");
            return false;
        }
    }

    /// <summary>
    /// Fixes incorrect content types for existing documents based on file extensions.
    /// </summary>
    public async Task<bool> FixContentTypesAsync()
    {
        try
        {
            _logger.LogInformation("Starting content type correction for existing documents");

            // Get all documents
            var allDocuments = await _searchService.GetAllDocumentsAsync(1000, 0);
            int totalDocuments = allDocuments.Count;
            int processedDocuments = 0;
            int correctedDocuments = 0;

            _logger.LogInformation("Found {TotalDocuments} documents to check", totalDocuments);

            foreach (var (documentId, chunks) in allDocuments)
            {
                if (!chunks.Any()) continue;

                // Only check the first chunk (where metadata is stored)
                var firstChunk = chunks.OrderBy(c => c.ChunkIndex).First();
                
                if (string.IsNullOrEmpty(firstChunk.OriginalFileName))
                {
                    processedDocuments++;
                    continue; // Skip if no filename
                }

                var correctContentType = GetCorrectContentType(firstChunk.OriginalFileName, firstChunk.ContentType);
                
                // Check if content type needs correction
                if (!string.IsNullOrEmpty(firstChunk.ContentType) && 
                    firstChunk.ContentType.Equals(correctContentType, StringComparison.OrdinalIgnoreCase))
                {
                    processedDocuments++;
                    continue; // Already correct
                }

                // Update content type
                _logger.LogInformation("Correcting content type for document {DocumentId}, file {FileName}: '{OldType}' -> '{NewType}'", 
                    documentId, firstChunk.OriginalFileName, firstChunk.ContentType ?? "null", correctContentType);

                firstChunk.ContentType = correctContentType;
                
                // Update in search index
                await _searchService.IndexDocumentChunksAsync(new List<DocumentChunk> { firstChunk });
                correctedDocuments++;
                processedDocuments++;
            }

            _logger.LogInformation("Content type correction completed: {ProcessedDocuments} documents processed, {CorrectedDocuments} corrected", 
                processedDocuments, correctedDocuments);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during content type correction");
            return false;
        }
    }

    private string GetCorrectContentType(string fileName, string? clientContentType)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        
        // Map file extensions to correct MIME types
        var mimeTypes = new Dictionary<string, string>
        {
            { ".pdf", "application/pdf" },
            { ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document" },
            { ".doc", "application/msword" },
            { ".txt", "text/plain" },
            { ".md", "text/markdown" },
            { ".json", "application/json" },
            { ".xml", "application/xml" },
            { ".csv", "text/csv" },
            { ".log", "text/plain" }
        };

        // If we have a mapping for this extension, use it
        if (mimeTypes.TryGetValue(extension, out var correctType))
        {
            return correctType;
        }

        // Fall back to client content type or generic binary
        return clientContentType ?? "application/octet-stream";
    }
}
