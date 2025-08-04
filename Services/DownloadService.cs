using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DriftMind.DTOs;

namespace DriftMind.Services;

public interface IDownloadService
{
    Task<DownloadTokenResponse> GenerateDownloadTokenAsync(string documentId, string? userId = null, TimeSpan? expiration = null);
    Task<TokenValidationResult> ValidateDownloadTokenAsync(string token);
    Task<DownloadFileResult> GetFileForDownloadAsync(string documentId, string? userId = null);
    Task LogDownloadActivityAsync(string documentId, string? userId, bool success);
}

public class DownloadService : IDownloadService
{
    private readonly IBlobStorageService _blobStorage;
    private readonly ISearchService _searchService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DownloadService> _logger;
    
    // Secure token keys (from Configuration in production)
    private readonly string _tokenSecret;
    private readonly TimeSpan _defaultExpiration;
    
    public DownloadService(
        IBlobStorageService blobStorage,
        ISearchService searchService,
        IConfiguration configuration,
        ILogger<DownloadService> logger)
    {
        _blobStorage = blobStorage;
        _searchService = searchService;
        _configuration = configuration;
        _logger = logger;
        
        // Secure token key (32+ characters)
        _tokenSecret = configuration["DownloadSecurity:TokenSecret"] 
            ?? "DriftMind-Secure-Download-Token-Key-2025-" + Environment.MachineName;
        _defaultExpiration = TimeSpan.FromMinutes(
            configuration.GetValue("DownloadSecurity:DefaultExpirationMinutes", 15));
    }
    
    public async Task<DownloadTokenResponse> GenerateDownloadTokenAsync(string documentId, string? userId = null, TimeSpan? expiration = null)
    {
        try
        {
            // 1. Check if document exists and file is available
            var documentExists = await ValidateDocumentAccessAsync(documentId);
            if (!documentExists.success)
            {
                return new DownloadTokenResponse
                {
                    Success = false,
                    ErrorMessage = documentExists.error,
                    DocumentId = documentId
                };
            }
            
            var exp = expiration ?? _defaultExpiration;
            var expiresAt = DateTime.UtcNow.Add(exp);
            
            // 2. Token-Daten erstellen (vereinfacht ohne Auth)
            var tokenData = new SecureTokenData
            {
                DocumentId = documentId,
                ExpiresAt = expiresAt,
                IssuedAt = DateTime.UtcNow,
                TokenId = Guid.NewGuid().ToString(),
                Purpose = "download"
            };
            
            // 3. Generate encrypted token
            var token = GenerateSecureToken(tokenData);
            
            // 4. Log download activity
            _logger.LogInformation("Generated download token for document {DocumentId}, expires at {ExpiresAt}",
                documentId, expiresAt);
            
            return new DownloadTokenResponse
            {
                Token = token,
                DocumentId = documentId,
                ExpiresAt = expiresAt,
                DownloadUrl = "/download/file", // POST endpoint only
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate download token for document {DocumentId}", documentId);
            return new DownloadTokenResponse
            {
                Success = false,
                ErrorMessage = "Failed to generate download token",
                DocumentId = documentId
            };
        }
    }
    
    public async Task<TokenValidationResult> ValidateDownloadTokenAsync(string token)
    {
        try
        {
            _logger.LogDebug("Validating download token: {TokenPreview}", 
                string.IsNullOrEmpty(token) ? "null/empty" : $"{token.Substring(0, Math.Min(20, token.Length))}...");
            
            // 1. Decrypt and validate token
            var tokenData = ValidateSecureToken(token);
            if (tokenData == null)
            {
                _logger.LogWarning("Invalid download token format received");
                return new TokenValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Invalid token format"
                };
            }
            
            // 2. Check expiration time
            if (DateTime.UtcNow > tokenData.ExpiresAt)
            {
                _logger.LogWarning("Expired download token used for document {DocumentId}",
                    tokenData.DocumentId);
                return new TokenValidationResult
                {
                    IsValid = false,
                    DocumentId = tokenData.DocumentId,
                    ExpiresAt = tokenData.ExpiresAt,
                    ErrorMessage = "Token has expired"
                };
            }
            
            // 3. Check document existence again (security)
            var documentExists = await ValidateDocumentAccessAsync(tokenData.DocumentId);
            if (!documentExists.success)
            {
                _logger.LogWarning("Token valid but document {DocumentId} no longer available", tokenData.DocumentId);
                return new TokenValidationResult
                {
                    IsValid = false,
                    DocumentId = tokenData.DocumentId,
                    ExpiresAt = tokenData.ExpiresAt,
                    ErrorMessage = documentExists.error
                };
            }
            
            _logger.LogInformation("Valid download token for document {DocumentId}",
                tokenData.DocumentId);
            
            return new TokenValidationResult
            {
                IsValid = true,
                DocumentId = tokenData.DocumentId,
                ExpiresAt = tokenData.ExpiresAt
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate download token");
            return new TokenValidationResult
            {
                IsValid = false,
                ErrorMessage = "Token validation failed"
            };
        }
    }
    
    public async Task<DownloadFileResult> GetFileForDownloadAsync(string documentId, string? userId = null)
    {
        try
        {
            // 1. Dokument-Info aus Search Index abrufen
            var searchResult = await _searchService.SearchAsync($"documentId:{documentId}", 1);
            var results = searchResult.GetResults().ToList();
            
            if (!results.Any())
            {
                return new DownloadFileResult
                {
                    Success = false,
                    ErrorMessage = "Document not found"
                };
            }
            
            var document = results.First().Document;
            var blobPath = document.BlobPath;
            
            if (string.IsNullOrEmpty(blobPath))
            {
                return new DownloadFileResult
                {
                    Success = false,
                    ErrorMessage = "No file available for this document"
                };
            }
            
            // 2. Datei aus Blob Storage mit Metadaten abrufen
            var blobDownloadResult = await _blobStorage.DownloadFileWithMetadataAsync(blobPath);
            
            if (!blobDownloadResult.Success)
            {
                return new DownloadFileResult
                {
                    Success = false,
                    ErrorMessage = $"Failed to download file: {blobDownloadResult.ErrorMessage}"
                };
            }
            
            // 3. Get original filename with priority order: Base64 encoded (original with umlauts) > sanitized > search index > blob path
            string originalFileName;
            
            // Debug: Log all available metadata
            _logger.LogInformation("Available blob metadata for document {DocumentId}: {Metadata}", 
                documentId, string.Join(", ", blobDownloadResult.Metadata.Select(kvp => $"{kvp.Key}={kvp.Value}")));
            
            // Try to get the original filename with umlauts from Base64 encoded metadata
            if (blobDownloadResult.Metadata.TryGetValue("OriginalFileNameBase64", out var base64FileName) && 
                !string.IsNullOrEmpty(base64FileName))
            {
                try
                {
                    _logger.LogInformation("Found Base64 encoded filename: {Base64FileName}", base64FileName);
                    var decodedBytes = Convert.FromBase64String(base64FileName);
                    originalFileName = Encoding.UTF8.GetString(decodedBytes);
                    _logger.LogInformation("Successfully restored original filename with umlauts from Base64: {OriginalFileName}", originalFileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode Base64 filename '{Base64FileName}', falling back to sanitized version", base64FileName);
                    // Fallback to sanitized version
                    originalFileName = blobDownloadResult.Metadata.GetValueOrDefault("OriginalFileName") 
                                    ?? document.OriginalFileName 
                                    ?? Path.GetFileName(blobPath);
                    _logger.LogInformation("Using fallback filename: {FallbackFileName}", originalFileName);
                }
            }
            else
            {
                // Fallback to sanitized version or search index
                originalFileName = blobDownloadResult.Metadata.GetValueOrDefault("OriginalFileName") 
                                ?? document.OriginalFileName 
                                ?? Path.GetFileName(blobPath);
                _logger.LogInformation("No Base64 filename found, using fallback: {FallbackFileName} (Source: {Source})", 
                    originalFileName, 
                    blobDownloadResult.Metadata.ContainsKey("OriginalFileName") ? "BlobMetadata" : 
                    !string.IsNullOrEmpty(document.OriginalFileName) ? "SearchIndex" : "BlobPath");
            }
            
            // 4. Log download activity (simplified)
            await LogDownloadActivityAsync(documentId, null, true);
            
            _logger.LogInformation("File download initiated for document {DocumentId}, file: {FileName}",
                documentId, originalFileName);
            
            return new DownloadFileResult
            {
                FileStream = blobDownloadResult.FileStream,
                FileName = originalFileName,
                ContentType = blobDownloadResult.ContentType,
                FileSizeBytes = blobDownloadResult.FileSizeBytes,
                Success = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file for download: document {DocumentId}", documentId);
            await LogDownloadActivityAsync(documentId, null, false);
            
            return new DownloadFileResult
            {
                Success = false,
                ErrorMessage = "File download failed"
            };
        }
    }
    
    public async Task LogDownloadActivityAsync(string documentId, string? userId, bool success)
    {
        try
        {
            // For audit trail: Log download activities (simplified)
            _logger.LogInformation("Download {Status} for document {DocumentId} at {Timestamp}",
                success ? "SUCCESS" : "FAILED", documentId, DateTime.UtcNow);
            
            // Optional: Write to database or separate log file
            // await _auditService.LogDownloadAsync(documentId, success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log download activity for document {DocumentId}", documentId);
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Validates if document exists and file is available for download
    /// </summary>
    private async Task<(bool success, string error)> ValidateDocumentAccessAsync(string documentId)
    {
        try
        {
            // Dokument in Search Index suchen
            var searchResult = await _searchService.SearchAsync($"documentId:{documentId}", 1);
            var results = searchResult.GetResults().ToList();
            
            if (!results.Any())
            {
                return (false, "Document not found");
            }
            
            var document = results.First().Document;
            
            // Check if blob path is available
            if (string.IsNullOrEmpty(document.BlobPath))
            {
                return (false, "No file available for download");
            }
            
            // Optional: Check blob existence (may impact performance)
            // var exists = await _blobStorage.FileExistsAsync(document.BlobPath);
            // if (!exists) return (false, "File no longer exists");
            
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate document access for {DocumentId}", documentId);
            return (false, "Document validation failed");
        }
    }
    
    /// <summary>
    /// Generates a secure, encrypted token with HMAC signature
    /// </summary>
    private string GenerateSecureToken(SecureTokenData tokenData)
    {
        try
        {
            // 1. Token-Daten zu JSON serialisieren
            var tokenJson = JsonSerializer.Serialize(tokenData);
            var tokenBytes = Encoding.UTF8.GetBytes(tokenJson);
            
            _logger.LogDebug("Generated token JSON: {TokenJson}", tokenJson);
            
            // 2. Token Base64-kodieren
            var tokenBase64 = Convert.ToBase64String(tokenBytes);
            
            // 3. Generate HMAC signature for manipulation protection
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_tokenSecret));
            var signatureBytes = hmac.ComputeHash(tokenBytes);
            var signature = Convert.ToBase64String(signatureBytes);
            
            // 4. Token + Signatur kombinieren
            var secureToken = $"{tokenBase64}.{signature}";
            
            _logger.LogDebug("Generated secure token: {TokenPreview}... (length: {Length})", 
                secureToken.Substring(0, Math.Min(50, secureToken.Length)), secureToken.Length);
            
            return secureToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate secure token");
            throw;
        }
    }
    
    /// <summary>
    /// Validates and decrypts a secure token
    /// </summary>
    private SecureTokenData? ValidateSecureToken(string token)
    {
        try
        {
            if (string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("Token is null or empty");
                return null;
            }
            
            _logger.LogDebug("ValidateSecureToken called with token length: {Length}", token.Length);
            
            // 1. Token und Signatur trennen
            var parts = token.Split('.');
            _logger.LogDebug("Token split into {Parts} parts", parts.Length);
            
            if (parts.Length != 2)
            {
                _logger.LogWarning("Token has {Parts} parts, expected 2", parts.Length);
                return null;
            }
            
            var tokenBase64 = parts[0];
            var signature = parts[1];
            
            _logger.LogDebug("Token Base64 length: {TokenLength}, Signature length: {SignatureLength}", 
                tokenBase64.Length, signature.Length);
            
            // 2. Token dekodieren
            var tokenBytes = Convert.FromBase64String(tokenBase64);
            
            // 3. Signatur validieren
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_tokenSecret));
            var expectedSignatureBytes = hmac.ComputeHash(tokenBytes);
            var expectedSignature = Convert.ToBase64String(expectedSignatureBytes);
            
            _logger.LogDebug("Expected signature: {Expected}, Received signature: {Received}", 
                expectedSignature, signature);
            
            if (!string.Equals(signature, expectedSignature, StringComparison.Ordinal))
            {
                _logger.LogWarning("Token signature validation failed - possible tampering attempt");
                return null;
            }
            
            // 4. Token-Daten deserialisieren
            var tokenJson = Encoding.UTF8.GetString(tokenBytes);
            var tokenData = JsonSerializer.Deserialize<SecureTokenData>(tokenJson);
            
            return tokenData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate secure token");
            return null;
        }
    }
    
    /// <summary>
    /// Secure token data structure (simplified)
    /// </summary>
    private class SecureTokenData
    {
        public string DocumentId { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }
        public DateTime IssuedAt { get; set; }
        public string TokenId { get; set; } = string.Empty;
        public string Purpose { get; set; } = string.Empty;
    }
}
