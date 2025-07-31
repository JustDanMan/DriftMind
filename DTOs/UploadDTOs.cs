namespace DriftMind.DTOs;

public class UploadTextRequest
{
    public string Text { get; set; } = string.Empty;
    public string? DocumentId { get; set; }
    public string? Metadata { get; set; }
    public int ChunkSize { get; set; } = 1000;
    public int ChunkOverlap { get; set; } = 200;
}

public class UploadTextResponse
{
    public string DocumentId { get; set; } = string.Empty;
    public int ChunksCreated { get; set; }
    public bool Success { get; set; }
    public string? Message { get; set; }
}
