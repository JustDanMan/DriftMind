using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using System.Text;

namespace DriftMind.Services;

public class BlobUploadResult
{
    public bool Success { get; set; }
    public string? BlobPath { get; set; }
    public string? ErrorMessage { get; set; }
}

public interface IBlobStorageService
{
    Task InitializeAsync();
    Task<string> UploadFileAsync(string fileName, Stream fileStream, string contentType);
    Task<BlobUploadResult> UploadFileAsync(string fileName, Stream fileStream, string contentType, Dictionary<string, string>? metadata);
    Task<BlobUploadResult> UploadTextContentAsync(string fileName, string textContent, string originalFileName, Dictionary<string, string>? metadata);
    Task<string> GetFileContentAsync(string fileName);
    Task<string> GetTextContentAsync(string fileName);
    Task<bool> FileExistsAsync(string fileName);
    Task<bool> DeleteFileAsync(string fileName);
    Task<BlobDownloadInfo> DownloadFileAsync(string fileName);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly BlobContainerClient _containerClient;
    private readonly ILogger<BlobStorageService> _logger;
    private readonly string _containerName;

    public BlobStorageService(BlobServiceClient blobServiceClient, IConfiguration configuration, ILogger<BlobStorageService> logger)
    {
        _blobServiceClient = blobServiceClient;
        _logger = logger;
        _containerName = configuration["AzureStorage:ContainerName"] ?? "documents";
        _containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
    }

    public async Task InitializeAsync()
    {
        try
        {
            await _containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            _logger.LogInformation("Blob container '{ContainerName}' initialized successfully", _containerName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize blob container '{ContainerName}'", _containerName);
            throw;
        }
    }

    public async Task<string> UploadFileAsync(string fileName, Stream fileStream, string contentType)
    {
        try
        {
            // Generate unique blob name without date folders to avoid conflicts
            var blobName = $"{Guid.NewGuid()}_{fileName}";
            var blobClient = _containerClient.GetBlobClient(blobName);

            var blobHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            };

            var metadata = new Dictionary<string, string>
            {
                ["OriginalFileName"] = fileName,
                ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                ["ContentType"] = contentType
            };

            fileStream.Position = 0; // Reset stream position
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions
            {
                HttpHeaders = blobHeaders,
                Metadata = metadata
            });

            _logger.LogInformation("File '{FileName}' uploaded to blob storage as '{BlobName}'", fileName, blobName);
            return blobName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file '{FileName}' to blob storage", fileName);
            throw;
        }
    }

    public async Task<BlobUploadResult> UploadFileAsync(string fileName, Stream fileStream, string contentType, Dictionary<string, string>? metadata)
    {
        try
        {
            // Generate unique blob name without date folders to avoid conflicts
            var blobName = $"{Guid.NewGuid()}_{fileName}";
            var blobClient = _containerClient.GetBlobClient(blobName);

            var blobHeaders = new BlobHttpHeaders
            {
                ContentType = contentType
            };

            var blobMetadata = new Dictionary<string, string>
            {
                ["OriginalFileName"] = fileName,
                ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                ["ContentType"] = contentType
            };

            // Merge provided metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    blobMetadata[kvp.Key] = kvp.Value;
                }
            }

            fileStream.Position = 0; // Reset stream position
            await blobClient.UploadAsync(fileStream, new BlobUploadOptions
            {
                HttpHeaders = blobHeaders,
                Metadata = blobMetadata
            });

            _logger.LogInformation("File '{FileName}' uploaded to blob storage as '{BlobName}'", fileName, blobName);
            return new BlobUploadResult
            {
                Success = true,
                BlobPath = blobName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload file '{FileName}' to blob storage", fileName);
            return new BlobUploadResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> GetFileContentAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning("Blob '{BlobName}' does not exist", blobName);
                return string.Empty;
            }

            var response = await blobClient.DownloadContentAsync();
            var content = response.Value.Content.ToString();
            
            _logger.LogDebug("Retrieved content from blob '{BlobName}', length: {Length}", blobName, content.Length);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get content from blob '{BlobName}'", blobName);
            return string.Empty;
        }
    }

    public async Task<BlobUploadResult> UploadTextContentAsync(string fileName, string textContent, string originalFileName, Dictionary<string, string>? metadata)
    {
        try
        {
            // Generate unique blob name for text content with .txt extension
            var textFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_content.txt";
            var blobName = $"{Guid.NewGuid()}_{textFileName}";
            var blobClient = _containerClient.GetBlobClient(blobName);

            var blobHeaders = new BlobHttpHeaders
            {
                ContentType = "text/plain; charset=utf-8"
            };

            var blobMetadata = new Dictionary<string, string>
            {
                ["OriginalFileName"] = originalFileName,
                ["TextExtractedFrom"] = fileName,
                ["UploadedAt"] = DateTime.UtcNow.ToString("O"),
                ["ContentType"] = "text/plain",
                ["IsTextContent"] = "true"
            };

            // Merge provided metadata
            if (metadata != null)
            {
                foreach (var kvp in metadata)
                {
                    blobMetadata[kvp.Key] = kvp.Value;
                }
            }

            // Upload text content as UTF-8 encoded bytes
            var textBytes = Encoding.UTF8.GetBytes(textContent);
            using var textStream = new MemoryStream(textBytes);
            
            await blobClient.UploadAsync(textStream, new BlobUploadOptions
            {
                HttpHeaders = blobHeaders,
                Metadata = blobMetadata
            });

            _logger.LogInformation("Text content for '{OriginalFileName}' uploaded to blob storage as '{BlobName}'", 
                originalFileName, blobName);
            
            return new BlobUploadResult
            {
                Success = true,
                BlobPath = blobName
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload text content for '{OriginalFileName}' to blob storage", originalFileName);
            return new BlobUploadResult
            {
                Success = false,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> GetTextContentAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            
            if (!await blobClient.ExistsAsync())
            {
                _logger.LogWarning("Text blob '{BlobName}' does not exist", blobName);
                return string.Empty;
            }

            // Check if this is a text content blob
            var properties = await blobClient.GetPropertiesAsync();
            if (!properties.Value.Metadata.ContainsKey("IsTextContent"))
            {
                _logger.LogWarning("Blob '{BlobName}' is not a text content blob", blobName);
                return string.Empty;
            }

            var response = await blobClient.DownloadContentAsync();
            var content = response.Value.Content.ToString();
            
            _logger.LogDebug("Retrieved text content from blob '{BlobName}', length: {Length}", blobName, content.Length);
            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get text content from blob '{BlobName}'", blobName);
            return string.Empty;
        }
    }

    public async Task<bool> FileExistsAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.ExistsAsync();
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check if blob '{BlobName}' exists", blobName);
            return false;
        }
    }

    public async Task<bool> DeleteFileAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.DeleteIfExistsAsync();
            
            if (response.Value)
            {
                _logger.LogInformation("Blob '{BlobName}' deleted successfully", blobName);
            }
            else
            {
                _logger.LogWarning("Blob '{BlobName}' does not exist or was already deleted", blobName);
            }
            
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete blob '{BlobName}'", blobName);
            return false;
        }
    }

    public async Task<BlobDownloadInfo> DownloadFileAsync(string blobName)
    {
        try
        {
            var blobClient = _containerClient.GetBlobClient(blobName);
            var response = await blobClient.DownloadAsync();
            
            _logger.LogDebug("Downloaded blob '{BlobName}'", blobName);
            return response.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download blob '{BlobName}'", blobName);
            throw;
        }
    }
}
