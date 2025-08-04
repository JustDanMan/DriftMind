using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

public interface IDocumentProcessingService
{
    Task<UploadTextResponse> ProcessFileAsync(UploadFileRequest request);
}

public class DocumentProcessingService : IDocumentProcessingService
{
    private readonly ITextChunkingService _chunkingService;
    private readonly IEmbeddingService _embeddingService;
    private readonly ISearchService _searchService;
    private readonly IFileProcessingService _fileProcessingService;
    private readonly IBlobStorageService _blobStorageService;
    private readonly ILogger<DocumentProcessingService> _logger;
    private readonly IConfiguration _configuration;

    public DocumentProcessingService(
        ITextChunkingService chunkingService,
        IEmbeddingService embeddingService,
        ISearchService searchService,
        IFileProcessingService fileProcessingService,
        IBlobStorageService blobStorageService,
        IConfiguration configuration,
        ILogger<DocumentProcessingService> logger)
    {
        _chunkingService = chunkingService;
        _embeddingService = embeddingService;
        _searchService = searchService;
        _fileProcessingService = fileProcessingService;
        _blobStorageService = blobStorageService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<UploadTextResponse> ProcessFileAsync(UploadFileRequest request)
    {
        try
        {
            var documentId = request.DocumentId ?? Guid.NewGuid().ToString();
            
            _logger.LogInformation("Processing file {FileName} for document {DocumentId}", 
                request.File.FileName, documentId);

            // 1. Validate file
            if (!_fileProcessingService.IsFileTypeSupported(request.File.FileName))
            {
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = $"File type not supported. Supported types: .txt, .md, .pdf, .docx",
                    FileName = request.File.FileName,
                    FileType = Path.GetExtension(request.File.FileName),
                    FileSizeBytes = request.File.Length
                };
            }

            if (!_fileProcessingService.IsFileSizeValid(request.File.Length))
            {
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = "File size exceeds the maximum allowed size.",
                    FileName = request.File.FileName,
                    FileType = Path.GetExtension(request.File.FileName),
                    FileSizeBytes = request.File.Length
                };
            }

            // 2. Save original file to blob storage
            string? blobPath = null;
            string? contentType = null;
            try
            {
                // Determine correct content type based on file extension
                contentType = GetCorrectContentType(request.File.FileName, request.File.ContentType);
                var fileName = $"{documentId}_{request.File.FileName}";
                
                // Upload file to blob storage
                using var fileStream = request.File.OpenReadStream();
                var uploadResult = await _blobStorageService.UploadFileAsync(
                    fileName,
                    fileStream,
                    contentType,
                    new Dictionary<string, string>
                    {
                        ["DocumentId"] = documentId,
                        ["OriginalFileName"] = request.File.FileName,
                        ["UploadedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                        ["FileSize"] = request.File.Length.ToString()
                    });

                if (uploadResult.Success)
                {
                    blobPath = uploadResult.BlobPath;
                    _logger.LogInformation("File {FileName} uploaded to blob storage at {BlobPath}", 
                        request.File.FileName, blobPath);
                }
                else
                {
                    _logger.LogError("Failed to upload file {FileName} to blob storage: {Error}", 
                        request.File.FileName, uploadResult.ErrorMessage);
                    return new UploadTextResponse
                    {
                        DocumentId = documentId,
                        Success = false,
                        Message = $"Failed to upload file '{request.File.FileName}' to blob storage: {uploadResult.ErrorMessage}",
                        FileName = request.File.FileName,
                        FileType = Path.GetExtension(request.File.FileName),
                        FileSizeBytes = request.File.Length
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file {FileName} to blob storage", request.File.FileName);
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = $"Error uploading file '{request.File.FileName}' to blob storage: {ex.Message}",
                    FileName = request.File.FileName,
                    FileType = Path.GetExtension(request.File.FileName),
                    FileSizeBytes = request.File.Length
                };
            }

            // 3. Extract text from file
            var extractResult = await _fileProcessingService.ExtractTextFromFileAsync(request.File);
            if (!extractResult.Success)
            {
                return new UploadTextResponse
                {
                    DocumentId = documentId,
                    Success = false,
                    Message = extractResult.ErrorMessage,
                    FileName = request.File.FileName,
                    FileType = Path.GetExtension(request.File.FileName),
                    FileSizeBytes = request.File.Length
                };
            }

            // 3.1. Save extracted text content to blob storage for PDF/Word files
            string? textContentBlobPath = null;
            if (!IsDirectTextFile(request.File.FileName))
            {
                try
                {
                    var textUploadResult = await _blobStorageService.UploadTextContentAsync(
                        $"{documentId}_{request.File.FileName}",
                        extractResult.Text,
                        request.File.FileName,
                        new Dictionary<string, string>
                        {
                            ["DocumentId"] = documentId,
                            ["OriginalFileName"] = request.File.FileName,
                            ["ExtractedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                            ["OriginalFileSize"] = request.File.Length.ToString(),
                            ["TextLength"] = extractResult.Text.Length.ToString()
                        });

                    if (textUploadResult.Success)
                    {
                        textContentBlobPath = textUploadResult.BlobPath;
                        _logger.LogInformation("Extracted text from {FileName} saved to blob storage at {TextBlobPath}", 
                            request.File.FileName, textContentBlobPath);
                    }
                    else
                    {
                        _logger.LogError("Failed to save extracted text from {FileName} to blob storage: {Error}", 
                            request.File.FileName, textUploadResult.ErrorMessage);
                        
                        // Cleanup: Delete the original file that was already uploaded
                        try
                        {
                            if (!string.IsNullOrEmpty(blobPath))
                            {
                                await _blobStorageService.DeleteFileAsync(blobPath);
                                _logger.LogInformation("Cleaned up original file {BlobPath} due to text upload failure", blobPath);
                            }
                        }
                        catch (Exception cleanupEx)
                        {
                            _logger.LogWarning(cleanupEx, "Failed to cleanup original file {BlobPath} after text upload failure", blobPath);
                        }
                        
                        return new UploadTextResponse
                        {
                            DocumentId = documentId,
                            Success = false,
                            Message = $"Failed to save extracted text from '{request.File.FileName}' to blob storage: {textUploadResult.ErrorMessage}",
                            FileName = request.File.FileName,
                            FileType = Path.GetExtension(request.File.FileName),
                            FileSizeBytes = request.File.Length
                        };
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving extracted text from {FileName} to blob storage", request.File.FileName);
                    
                    // Cleanup: Delete the original file that was already uploaded
                    try
                    {
                        if (!string.IsNullOrEmpty(blobPath))
                        {
                            await _blobStorageService.DeleteFileAsync(blobPath);
                            _logger.LogInformation("Cleaned up original file {BlobPath} due to text upload error", blobPath);
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        _logger.LogWarning(cleanupEx, "Failed to cleanup original file {BlobPath} after text upload error", blobPath);
                    }
                    
                    return new UploadTextResponse
                    {
                        DocumentId = documentId,
                        Success = false,
                        Message = $"Error saving extracted text from '{request.File.FileName}' to blob storage: {ex.Message}",
                        FileName = request.File.FileName,
                        FileType = Path.GetExtension(request.File.FileName),
                        FileSizeBytes = request.File.Length
                    };
                }
            }

            // 4. Process extracted text using existing text processing logic with blob storage info
            var metadata = string.IsNullOrEmpty(request.Metadata) 
                ? $"File: {request.File.FileName}" 
                : $"File: {request.File.FileName}, {request.Metadata}";

            // Include blob storage information in chunks
            var result = await ProcessTextWithBlobInfoAsync(
                extractResult.Text, 
                documentId, 
                metadata, 
                request.ChunkSize, 
                request.ChunkOverlap, 
                blobPath, 
                textContentBlobPath, 
                request.File.FileName, 
                contentType, 
                request.File.Length);
            
            // 5. Add file-specific information to response
            result.FileName = request.File.FileName;
            result.FileType = Path.GetExtension(request.File.FileName);
            result.FileSizeBytes = request.File.Length;

            if (result.Success)
            {
                _logger.LogInformation("File {FileName} successfully processed and indexed as document {DocumentId}", 
                    request.File.FileName, documentId);
                result.Message = $"File '{request.File.FileName}' successfully processed into {result.ChunksCreated} chunks and indexed.";
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {FileName} for document {DocumentId}", 
                request.File.FileName, request.DocumentId);
            return new UploadTextResponse
            {
                DocumentId = request.DocumentId ?? "unknown",
                Success = false,
                Message = $"Error during file processing: {ex.Message}",
                FileName = request.File.FileName,
                FileType = Path.GetExtension(request.File.FileName),
                FileSizeBytes = request.File.Length
            };
        }
    }

    private async Task<UploadTextResponse> ProcessTextWithBlobInfoAsync(
        string text,
        string? documentId,
        string? metadata,
        int chunkSize,
        int chunkOverlap,
        string? blobPath, 
        string? textContentBlobPath,
        string? originalFileName, 
        string? contentType,
        long? fileSizeBytes = null)
    {
        try
        {
            var docId = documentId ?? Guid.NewGuid().ToString();
            
            _logger.LogInformation("Processing text for document {DocumentId}", docId);

            // 1. Split text into chunks
            var chunks = _chunkingService.ChunkText(text, chunkSize, chunkOverlap);
            
            if (!chunks.Any())
            {
                return new UploadTextResponse
                {
                    DocumentId = docId,
                    Success = false,
                    Message = "No text chunks were created from the provided text."
                };
            }

            // 2. Create embeddings for chunks
            var chunksWithEmbeddings = new List<(string chunk, IReadOnlyList<float> embedding)>();
            
            foreach (var chunk in chunks)
            {
                var embedding = await _embeddingService.GenerateEmbeddingAsync(chunk);
                chunksWithEmbeddings.Add((chunk, embedding));
            }

            // 3. Create DocumentChunk objects with blob storage information
            var containerName = _configuration["AzureStorage:ContainerName"] ?? "documents";
            var documentChunks = chunksWithEmbeddings.Select((chunkData, index) => new DocumentChunk
            {
                Id = $"{docId}_{index}",
                Content = chunkData.chunk,
                DocumentId = docId,
                ChunkIndex = index,
                Embedding = chunkData.embedding,
                Metadata = metadata,
                BlobPath = index == 0 ? blobPath : null, // Only first chunk stores blob info
                BlobContainer = index == 0 ? containerName : null, // Only first chunk stores container
                OriginalFileName = index == 0 ? originalFileName : null, // Only first chunk stores filename
                ContentType = index == 0 ? contentType : null, // Only first chunk stores content type
                TextContentBlobPath = index == 0 ? textContentBlobPath : null, // Only first chunk stores text blob path
                FileSizeBytes = index == 0 ? fileSizeBytes : null // Only first chunk stores file size
            }).ToList();

            _logger.LogInformation("Created {ChunkCount} chunks with embeddings for document {DocumentId}", 
                chunks.Count, docId);

            // 4. Index chunks in Azure AI Search
            var indexingSuccess = await _searchService.IndexDocumentChunksAsync(documentChunks);

            if (indexingSuccess)
            {
                _logger.LogInformation("Document {DocumentId} successfully processed and indexed", docId);
                return new UploadTextResponse
                {
                    DocumentId = docId,
                    ChunksCreated = chunks.Count,
                    Success = true,
                    Message = $"Text successfully processed into {chunks.Count} chunks and indexed."
                };
            }
            else
            {
                _logger.LogError("Indexing for document {DocumentId} failed", docId);
                
                // Cleanup: Delete blob files if indexing fails
                await CleanupBlobFilesAsync(blobPath, textContentBlobPath, docId);
                
                return new UploadTextResponse
                {
                    DocumentId = docId,
                    ChunksCreated = 0,
                    Success = false,
                    Message = "Document processing failed during indexing. All uploaded files have been cleaned up."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing text for document {DocumentId}", documentId ?? "unknown");
            
            // Cleanup: Delete blob files if processing fails
            var docIdForCleanup = documentId ?? Guid.NewGuid().ToString();
            await CleanupBlobFilesAsync(blobPath, textContentBlobPath, docIdForCleanup);
            
            return new UploadTextResponse
            {
                DocumentId = documentId ?? "unknown",
                Success = false,
                Message = $"Error processing text: {ex.Message}. All uploaded files have been cleaned up."
            };
        }
    }

    private async Task CleanupBlobFilesAsync(string? blobPath, string? textContentBlobPath, string documentId)
    {
        var cleanupTasks = new List<Task>();
        
        if (!string.IsNullOrEmpty(blobPath))
        {
            cleanupTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _blobStorageService.DeleteFileAsync(blobPath);
                    _logger.LogInformation("Cleaned up original file {BlobPath} for document {DocumentId}", blobPath, documentId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup original file {BlobPath} for document {DocumentId}", blobPath, documentId);
                }
            }));
        }
        
        if (!string.IsNullOrEmpty(textContentBlobPath))
        {
            cleanupTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _blobStorageService.DeleteFileAsync(textContentBlobPath);
                    _logger.LogInformation("Cleaned up text content file {TextBlobPath} for document {DocumentId}", textContentBlobPath, documentId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to cleanup text content file {TextBlobPath} for document {DocumentId}", textContentBlobPath, documentId);
                }
            }));
        }
        
        if (cleanupTasks.Any())
        {
            await Task.WhenAll(cleanupTasks);
        }
    }

    private bool IsDirectTextFile(string fileName)
    {
        var textExtensions = new[] { ".txt", ".md", ".json", ".xml", ".csv", ".log" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return textExtensions.Contains(extension);
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
            // Log if the client sent a different (incorrect) content type
            if (!string.IsNullOrEmpty(clientContentType) && 
                !clientContentType.Equals(correctType, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Correcting content type for {FileName}: client sent '{ClientType}', using '{CorrectType}'", 
                    fileName, clientContentType, correctType);
            }
            return correctType;
        }

        // Fall back to client content type or generic binary
        return clientContentType ?? "application/octet-stream";
    }
}
