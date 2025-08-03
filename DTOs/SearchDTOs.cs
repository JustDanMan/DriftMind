namespace DriftMind.DTOs;

public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // "user" or "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 10;
    public bool UseSemanticSearch { get; set; } = true;
    public string? DocumentId { get; set; }
    public bool IncludeAnswer { get; set; } = true;
    public List<ChatMessage>? ChatHistory { get; set; } = null;
}

public class SearchResponse
{
    public string Query { get; set; } = string.Empty;
    public List<SearchResult> Results { get; set; } = new();
    public string? GeneratedAnswer { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
    public int TotalResults { get; set; }
}

public class SearchResult
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string DocumentId { get; set; } = string.Empty;
    public int ChunkIndex { get; set; }
    public double? Score { get; set; }
    public double? VectorScore { get; set; } // Original vector similarity score
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsRelevant { get; set; } = true;
    public double RelevanceScore { get; set; }
    
    // Blob Storage Information
    public string? BlobPath { get; set; }
    public string? BlobContainer { get; set; }
    public string? OriginalFileName { get; set; }
    public string? ContentType { get; set; }
    public string? TextContentBlobPath { get; set; }
    
    // Download Information
    public bool IsFileAvailable => !string.IsNullOrEmpty(BlobPath);
    public DownloadInfo? Download { get; set; }
}
