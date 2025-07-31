namespace DriftMind.DTOs;

public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 10;
    public bool UseSemanticSearch { get; set; } = true;
    public string? DocumentId { get; set; }
    public bool IncludeAnswer { get; set; } = true;
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
    public double Score { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
