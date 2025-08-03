namespace DriftMind.DTOs;

public class UploadFileRequest
{
    public IFormFile File { get; set; } = null!;
    public string? DocumentId { get; set; }
    public string? Metadata { get; set; }
    public int ChunkSize { get; set; } = 300;
    public int ChunkOverlap { get; set; } = 20;
}

public class UploadTextResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public int ChunksCreated { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? FileName { get; set; }
    public string? FileType { get; set; }
    public long? FileSizeBytes { get; set; }
}
