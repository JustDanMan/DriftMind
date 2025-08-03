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
                contentType = request.File.ContentType;
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
                    _logger.LogWarning("Failed to upload file {FileName} to blob storage: {Error}", 
                        request.File.FileName, uploadResult.ErrorMessage);
                    // Continue processing even if blob upload fails
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading file {FileName} to blob storage", request.File.FileName);
                // Continue processing even if blob upload fails
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
                        _logger.LogWarning("Failed to save extracted text from {FileName} to blob storage: {Error}", 
                            request.File.FileName, textUploadResult.ErrorMessage);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving extracted text from {FileName} to blob storage", request.File.FileName);
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
                _logger.LogWarning("Indexing for document {DocumentId} was not completely successful", docId);
                return new UploadTextResponse
                {
                    DocumentId = docId,
                    ChunksCreated = chunks.Count,
                    Success = false,
                    Message = "Text was processed, but indexing was not completely successful."
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing text for document {DocumentId}", documentId ?? "unknown");
            return new UploadTextResponse
            {
                DocumentId = documentId ?? "unknown",
                Success = false,
                Message = $"Error during processing: {ex.Message}"
            };
        }
    }

    private bool IsDirectTextFile(string fileName)
    {
        var textExtensions = new[] { ".txt", ".md", ".json", ".xml", ".csv", ".log" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return textExtensions.Contains(extension);
    }
}
