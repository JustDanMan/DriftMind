namespace DriftMind.Models;

public class FileUploadOptions
{
    public int MaxFileSizeInMB { get; set; } = 3;
    public List<string> AllowedExtensions { get; set; } = new() { ".txt", ".md", ".pdf", ".docx" };
    
    public long MaxFileSizeInBytes => MaxFileSizeInMB * 1024 * 1024;
}
