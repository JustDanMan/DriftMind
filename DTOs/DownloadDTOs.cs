namespace DriftMind.DTOs;

/// <summary>
/// Request for downloading a file with token in body
/// </summary>
public class TokenDownloadRequest
{
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Response when generating a download token
/// </summary>
public class DownloadTokenResponse
{
    public string Token { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Result of token validation
/// </summary>
public class TokenValidationResult
{
    public bool IsValid { get; set; }
    public string DocumentId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsExpired => DateTime.UtcNow > ExpiresAt;
}

/// <summary>
/// Result of file download request
/// </summary>
public class DownloadFileResult
{
    public Stream? FileStream { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Blob download result with metadata
/// </summary>
public class BlobDownloadWithMetadataResult
{
    public Stream? FileStream { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Download information added to search results
/// </summary>
public class DownloadInfo
{
    public string DocumentId { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public int TokenExpirationMinutes { get; set; } = 15;
    // FileName and FileType removed - available in SearchResult.OriginalFileName and SearchResult.ContentType
}

/// <summary>
/// Download token generation request
/// </summary>
public class GenerateDownloadTokenRequest
{
    public string DocumentId { get; set; } = string.Empty;
    public int ExpirationMinutes { get; set; } = 15;
}
