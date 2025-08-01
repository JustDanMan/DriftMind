using DriftMind.DTOs;
using DriftMind.Models;

namespace DriftMind.Services;

public interface IDocumentProcessingService
{
    Task<UploadTextResponse> ProcessTextAsync(UploadTextRequest request);
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
                    FileSizeInBytes = request.File.Length
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
                    FileSizeInBytes = request.File.Length
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
                    FileSizeInBytes = request.File.Length
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
            var textRequest = new UploadTextRequest
            {
                Text = extractResult.Text,
                DocumentId = documentId,
                Metadata = string.IsNullOrEmpty(request.Metadata) 
                    ? $"File: {request.File.FileName}" 
                    : $"File: {request.File.FileName}, {request.Metadata}",
                ChunkSize = request.ChunkSize,
                ChunkOverlap = request.ChunkOverlap
            };

            // Include blob storage information in chunks
            var result = await ProcessTextWithBlobInfoAsync(textRequest, blobPath, textContentBlobPath, request.File.FileName, contentType);
            
            // 5. Add file-specific information to response
            result.FileName = request.File.FileName;
            result.FileType = Path.GetExtension(request.File.FileName);
            result.FileSizeInBytes = request.File.Length;

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
                FileSizeInBytes = request.File.Length
            };
        }
    }

    private async Task<UploadTextResponse> ProcessTextWithBlobInfoAsync(
        UploadTextRequest request, 
        string? blobPath, 
        string? textContentBlobPath,
        string? originalFileName, 
        string? contentType)
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
                Id = $"{documentId}_{index}",
                Content = chunkData.chunk,
                DocumentId = documentId,
                ChunkIndex = index,
                Embedding = chunkData.embedding,
                Metadata = request.Metadata,
                BlobPath = blobPath,
                BlobContainer = containerName,
                OriginalFileName = originalFileName,
                ContentType = contentType,
                TextContentBlobPath = textContentBlobPath
            }).ToList();

            _logger.LogInformation("Created {ChunkCount} chunks with embeddings for document {DocumentId}", 
                chunks.Count, documentId);

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

    private bool IsDirectTextFile(string fileName)
    {
        var textExtensions = new[] { ".txt", ".md", ".json", ".xml", ".csv", ".log" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return textExtensions.Contains(extension);
    }
}
