using DriftMind.DTOs;
using DriftMind.Models;
using System.Text;

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
        // Determine desired documentId and ensure it's unique according to the rules
        string documentId;
        if (!string.IsNullOrWhiteSpace(request.DocumentId))
        {
            // Client-specified ID: check for existence and reject if already present
            var exists = await _searchService.DocumentExistsAsync(request.DocumentId);
            if (exists)
            {
                return new UploadTextResponse
                {
                    DocumentId = request.DocumentId!,
                    Success = false,
                    ErrorCode = "Conflict",
                    Message = $"DocumentId '{request.DocumentId}' already exists. Please choose a different id."
                };
            }
            documentId = request.DocumentId!;
        }
        else
        {
            // Auto-generate GUID and ensure uniqueness with a short retry loop (extremely low collision probability)
            const int maxAttempts = 5;
            int attempt = 0;
            do
            {
                documentId = Guid.NewGuid().ToString();
                attempt++;
                var exists = await _searchService.DocumentExistsAsync(documentId);
                if (!exists)
                {
                    break;
                }
            } while (attempt < maxAttempts);

            if (attempt >= maxAttempts)
            {
                return new UploadTextResponse
                {
                    DocumentId = string.Empty,
                    Success = false,
                    ErrorCode = "GenerationFailed",
                    Message = "Failed to generate a unique documentId after multiple attempts. Please try again."
                };
            }
        }
        string? blobPath = null;
        string? textContentBlobPath = null;
        
        try
        {
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
            string? contentType = null;
            try
            {
                // Determine correct content type based on file extension
                contentType = GetCorrectContentType(request.File.FileName, request.File.ContentType);
                
                // Sanitize filename for blob storage - remove non-ASCII characters
                var sanitizedFileName = SanitizeFileNameForBlobStorage(request.File.FileName);
                var fileName = $"{documentId}_{sanitizedFileName}";
                
                // Upload file to blob storage
                using var fileStream = request.File.OpenReadStream();
                var uploadResult = await _blobStorageService.UploadFileAsync(
                    fileName,
                    fileStream,
                    contentType,
                    new Dictionary<string, string>
                    {
                        ["DocumentId"] = documentId,
                        ["OriginalFileName"] = SanitizeMetadataValue(request.File.FileName), // Sanitized for compatibility
                        ["OriginalFileNameBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.File.FileName)), // Original with umlauts
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
                // Cleanup: Delete the original file that was already uploaded
                try
                {
                    if (!string.IsNullOrEmpty(blobPath))
                    {
                        await _blobStorageService.DeleteFileAsync(blobPath);
                        _logger.LogInformation("Cleaned up original file {BlobPath} due to text extraction failure", blobPath);
                    }
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to cleanup original file {BlobPath} after text extraction failure", blobPath);
                }
                
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
            if (!IsDirectTextFile(request.File.FileName))
            {
                try
                {
                    var sanitizedTextFileName = SanitizeFileNameForBlobStorage(request.File.FileName);
                    var textUploadResult = await _blobStorageService.UploadTextContentAsync(
                        $"{documentId}_{sanitizedTextFileName}",
                        extractResult.Text,
                        request.File.FileName,
                        new Dictionary<string, string>
                        {
                            ["DocumentId"] = documentId,
                            ["OriginalFileName"] = SanitizeMetadataValue(request.File.FileName),
                            ["OriginalFileNameBase64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(request.File.FileName)), // Original with umlauts
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
            
            // Critical: Cleanup any uploaded blobs if processing fails at any point
            await CleanupBlobFilesAsync(blobPath, textContentBlobPath, documentId);
            
            return new UploadTextResponse
            {
                DocumentId = documentId,
                Success = false,
                Message = $"Error during file processing: {ex.Message}. All uploaded files have been cleaned up.",
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
                // 4.1. Verify blob files still exist after indexing (safety check)
                var blobValidationFailed = false;
                var blobValidationErrors = new List<string>();
                
                if (!string.IsNullOrEmpty(blobPath))
                {
                    try
                    {
                        var originalFileExists = await _blobStorageService.FileExistsAsync(blobPath);
                        if (!originalFileExists)
                        {
                            blobValidationFailed = true;
                            blobValidationErrors.Add($"Original file {blobPath} no longer exists in blob storage");
                        }
                    }
                    catch (Exception ex)
                    {
                        blobValidationFailed = true;
                        blobValidationErrors.Add($"Failed to verify original file {blobPath}: {ex.Message}");
                    }
                }
                
                if (!string.IsNullOrEmpty(textContentBlobPath))
                {
                    try
                    {
                        var textFileExists = await _blobStorageService.FileExistsAsync(textContentBlobPath);
                        if (!textFileExists)
                        {
                            blobValidationFailed = true;
                            blobValidationErrors.Add($"Text content file {textContentBlobPath} no longer exists in blob storage");
                        }
                    }
                    catch (Exception ex)
                    {
                        blobValidationFailed = true;
                        blobValidationErrors.Add($"Failed to verify text content file {textContentBlobPath}: {ex.Message}");
                    }
                }
                
                if (blobValidationFailed)
                {
                    _logger.LogError("Blob validation failed after successful indexing for document {DocumentId}: {Errors}", 
                        docId, string.Join(", ", blobValidationErrors));
                    
                    // Critical: Remove the index entries since blob files are missing
                    try
                    {
                        await _searchService.DeleteDocumentAsync(docId);
                        _logger.LogInformation("Removed index entries for document {DocumentId} due to missing blob files", docId);
                    }
                    catch (Exception deleteEx)
                    {
                        _logger.LogError(deleteEx, "Failed to cleanup index entries for document {DocumentId} after blob validation failure", docId);
                    }
                    
                    return new UploadTextResponse
                    {
                        DocumentId = docId,
                        ChunksCreated = 0,
                        Success = false,
                        Message = $"Document processing failed: {string.Join(", ", blobValidationErrors)}. Index entries have been cleaned up."
                    };
                }
                
                _logger.LogInformation("Document {DocumentId} successfully processed and indexed with valid blob references", docId);
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

    private string SanitizeFileNameForBlobStorage(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return "unknown_file";

        var result = new StringBuilder();
        
        // Process each character and replace umlauts properly
        foreach (char c in fileName)
        {
            if (c <= 127) // ASCII range
            {
                // Replace problematic characters for file systems
                switch (c)
                {
                    case '<':
                    case '>':
                    case ':':
                    case '"':
                    case '|':
                    case '?':
                    case '*':
                    case '/':
                    case '\\':
                    case ' ': // Replace spaces with underscores
                        result.Append('_');
                        break;
                    default:
                        result.Append(c);
                        break;
                }
            }
            else
            {
                // Replace common German umlauts and special characters
                switch (c)
                {
                    case 'ä': result.Append("ae"); break;
                    case 'ö': result.Append("oe"); break;
                    case 'ü': result.Append("ue"); break;
                    case 'Ä': result.Append("Ae"); break;
                    case 'Ö': result.Append("Oe"); break;
                    case 'Ü': result.Append("Ue"); break;
                    case 'ß': result.Append("ss"); break;
                    default:
                        // For other non-ASCII characters, try ASCII conversion or use underscore
                        var ascii = System.Text.Encoding.ASCII.GetString(
                            System.Text.Encoding.ASCII.GetBytes(c.ToString()));
                        if (ascii != "?")
                        {
                            result.Append(ascii);
                        }
                        else
                        {
                            result.Append('_'); // Fallback for truly unknown characters
                        }
                        break;
                }
            }
        }

        var sanitized = result.ToString();

        // Remove multiple consecutive underscores
        while (sanitized.Contains("__"))
        {
            sanitized = sanitized.Replace("__", "_");
        }

        // Trim underscores from start and end
        sanitized = sanitized.Trim('_');

        // Ensure we have a valid filename
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "sanitized_file";
        }

        return sanitized;
    }

    private string SanitizeMetadataValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        // HTTP headers must contain only ASCII characters
        // Replace non-ASCII characters with their closest ASCII equivalent or remove them
        var result = new StringBuilder();
        foreach (char c in value)
        {
            if (c <= 127) // ASCII range
            {
                result.Append(c);
            }
            else
            {
                // Replace common German umlauts and special characters
                switch (c)
                {
                    case 'ä': result.Append("ae"); break;
                    case 'ö': result.Append("oe"); break;
                    case 'ü': result.Append("ue"); break;
                    case 'Ä': result.Append("Ae"); break;
                    case 'Ö': result.Append("Oe"); break;
                    case 'Ü': result.Append("Ue"); break;
                    case 'ß': result.Append("ss"); break;
                    default:
                        // For other non-ASCII characters, try ASCII conversion
                        var ascii = System.Text.Encoding.ASCII.GetString(
                            System.Text.Encoding.ASCII.GetBytes(c.ToString()));
                        if (ascii != "?")
                        {
                            result.Append(ascii);
                        }
                        // If ASCII conversion fails, skip the character
                        break;
                }
            }
        }

        return result.ToString();
    }
}
