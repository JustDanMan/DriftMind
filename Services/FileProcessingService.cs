using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;
using DriftMind.Models;
using Microsoft.Extensions.Options;

namespace DriftMind.Services;

public interface IFileProcessingService
{
    Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromFileAsync(IFormFile file);
    bool IsFileTypeSupported(string fileName);
    bool IsFileSizeValid(long fileSizeInBytes);
}

public class FileProcessingService : IFileProcessingService
{
    private readonly ILogger<FileProcessingService> _logger;
    private readonly FileUploadOptions _fileUploadOptions;

    public FileProcessingService(ILogger<FileProcessingService> logger, IOptions<FileUploadOptions> fileUploadOptions)
    {
        _logger = logger;
        _fileUploadOptions = fileUploadOptions.Value;
    }

    public bool IsFileTypeSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return _fileUploadOptions.AllowedExtensions.Contains(extension);
    }

    public bool IsFileSizeValid(long fileSizeInBytes)
    {
        return fileSizeInBytes <= _fileUploadOptions.MaxFileSizeInBytes;
    }

    public async Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromFileAsync(IFormFile file)
    {
        try
        {
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
            
            _logger.LogInformation("Processing file {FileName} with extension {Extension}", file.FileName, extension);

            return extension switch
            {
                ".txt" => await ExtractTextFromPlainTextAsync(file),
                ".md" => await ExtractTextFromMarkdownAsync(file),
                ".pdf" => await ExtractTextFromPdfAsync(file),
                ".docx" => await ExtractTextFromWordAsync(file),
                _ => (false, string.Empty, $"Unsupported file type: {extension}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file {FileName}", file.FileName);
            return (false, string.Empty, $"Error processing file: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromPlainTextAsync(IFormFile file)
    {
        try
        {
            using var stream = file.OpenReadStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            
            if (string.IsNullOrWhiteSpace(text))
            {
                return (false, string.Empty, "The text file is empty or contains only whitespace.");
            }

            _logger.LogInformation("Successfully extracted {Length} characters from text file", text.Length);
            return (true, text, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading text file {FileName}", file.FileName);
            return (false, string.Empty, $"Error reading text file: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromMarkdownAsync(IFormFile file)
    {
        // Markdown files are essentially text files, so we can use the same logic
        return await ExtractTextFromPlainTextAsync(file);
    }

    private async Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromPdfAsync(IFormFile file)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var pdfReader = new PdfReader(memoryStream);
            using var pdfDocument = new PdfDocument(pdfReader);

            var text = new StringBuilder();
            var pageCount = pdfDocument.GetNumberOfPages();

            for (int i = 1; i <= pageCount; i++)
            {
                var page = pdfDocument.GetPage(i);
                var pageText = PdfTextExtractor.GetTextFromPage(page);
                text.AppendLine(pageText);
            }

            var extractedText = text.ToString().Trim();
            
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return (false, string.Empty, "No text could be extracted from the PDF file.");
            }

            _logger.LogInformation("Successfully extracted {Length} characters from PDF file with {PageCount} pages", 
                extractedText.Length, pageCount);
            return (true, extractedText, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading PDF file {FileName}", file.FileName);
            return (false, string.Empty, $"Error reading PDF file: {ex.Message}");
        }
    }

    private async Task<(bool Success, string Text, string ErrorMessage)> ExtractTextFromWordAsync(IFormFile file)
    {
        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0;

            using var wordDocument = WordprocessingDocument.Open(memoryStream, false);
            var body = wordDocument.MainDocumentPart?.Document?.Body;
            
            if (body == null)
            {
                return (false, string.Empty, "The Word document appears to be empty or corrupted.");
            }

            var text = new StringBuilder();
            
            // Extract text from paragraphs
            var paragraphs = body.Elements<Paragraph>();
            foreach (var paragraph in paragraphs)
            {
                var paragraphText = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(paragraphText))
                {
                    text.AppendLine(paragraphText);
                }
            }

            // Extract text from tables
            var tables = body.Elements<Table>();
            foreach (var table in tables)
            {
                foreach (var row in table.Elements<TableRow>())
                {
                    var rowTexts = new List<string>();
                    foreach (var cell in row.Elements<TableCell>())
                    {
                        var cellText = cell.InnerText?.Trim();
                        if (!string.IsNullOrWhiteSpace(cellText))
                        {
                            rowTexts.Add(cellText);
                        }
                    }
                    if (rowTexts.Any())
                    {
                        text.AppendLine(string.Join(" | ", rowTexts));
                    }
                }
                text.AppendLine(); // Add spacing after table
            }

            var extractedText = text.ToString().Trim();
            
            if (string.IsNullOrWhiteSpace(extractedText))
            {
                return (false, string.Empty, "No text could be extracted from the Word document.");
            }

            _logger.LogInformation("Successfully extracted {Length} characters from Word document", extractedText.Length);
            return (true, extractedText, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading Word file {FileName}", file.FileName);
            return (false, string.Empty, $"Error reading Word file: {ex.Message}");
        }
    }
}
