using System.Text.Json.Serialization;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace DriftMind.Models;

public class DocumentChunk
{
    [SimpleField(IsKey = true, IsFilterable = true)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [SearchableField(IsSortable = true)]
    public string Content { get; set; } = string.Empty;

    [SearchableField(IsFilterable = true, IsSortable = true)]
    public string DocumentId { get; set; } = string.Empty;

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public int ChunkIndex { get; set; }

    [VectorSearchField(VectorSearchDimensions = 1536, VectorSearchProfileName = "vector-profile")]
    public IReadOnlyList<float> Embedding { get; set; } = new List<float>();

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [SearchableField]
    public string? Metadata { get; set; }

    [SimpleField(IsFilterable = true)]
    public string? BlobPath { get; set; }

    [SimpleField(IsFilterable = true)]
    public string? BlobContainer { get; set; }

    [SimpleField(IsFilterable = true)]
    public string? OriginalFileName { get; set; }

    [SimpleField(IsFilterable = true)]
    public string? ContentType { get; set; }

    [SimpleField(IsFilterable = true)]
    public string? TextContentBlobPath { get; set; }

    [SimpleField(IsFilterable = true, IsSortable = true)]
    public long? FileSizeBytes { get; set; }
}
