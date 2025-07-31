namespace DriftMind.DTOs;

public class DocumentListRequest
{
    public int MaxResults { get; set; } = 50;
    public int Skip { get; set; } = 0;
    public string? DocumentIdFilter { get; set; }
}

public class DocumentSummary
{
    public string DocumentId { get; set; } = string.Empty;
    public int ChunkCount { get; set; }
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public long? FileSizeInBytes { get; set; }
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public List<string> SampleContent { get; set; } = new();
}

public class DocumentListResponse
{
    public List<DocumentSummary> Documents { get; set; } = new();
    public int TotalDocuments { get; set; }
    public int ReturnedDocuments { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class DeleteDocumentRequest
{
    public string DocumentId { get; set; } = string.Empty;
}

public class DeleteDocumentResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public int ChunksDeleted { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
