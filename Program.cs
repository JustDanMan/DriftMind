using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents.Indexes;
using Azure.Storage.Blobs;
using DriftMind.DTOs;
using DriftMind.Services;
using DriftMind.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure file upload options
builder.Services.Configure<FileUploadOptions>(
    builder.Configuration.GetSection("FileUpload"));

// Azure OpenAI Configuration
var azureOpenAIEndpoint = builder.Configuration["AzureOpenAI:Endpoint"]!;
var azureOpenAIApiKey = builder.Configuration["AzureOpenAI:ApiKey"]!;
builder.Services.AddSingleton(sp => new AzureOpenAIClient(new Uri(azureOpenAIEndpoint), new AzureKeyCredential(azureOpenAIApiKey)));

// Azure Search Configuration
var azureSearchEndpoint = builder.Configuration["AzureSearch:Endpoint"]!;
var azureSearchApiKey = builder.Configuration["AzureSearch:ApiKey"]!;
builder.Services.AddSingleton(sp => new SearchIndexClient(new Uri(azureSearchEndpoint), new AzureKeyCredential(azureSearchApiKey)));

// Azure Blob Storage Configuration
var azureStorageConnectionString = builder.Configuration["AzureStorage:ConnectionString"]!;
builder.Services.AddSingleton(sp => new BlobServiceClient(azureStorageConnectionString));

// Register Services
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IFileProcessingService, FileProcessingService>();
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<ISearchOrchestrationService, SearchOrchestrationService>();
builder.Services.AddScoped<IDocumentManagementService, DocumentManagementService>();
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddScoped<IDownloadService, DownloadService>();
builder.Services.AddScoped<IDataMigrationService, DataMigrationService>();

// Configure URLs for production deployment
builder.WebHost.UseUrls("http://0.0.0.0:8081");

var app = builder.Build();

// Initialize Azure Search Index and Blob Storage
using (var scope = app.Services.CreateScope())
{
    var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
    await searchService.InitializeIndexAsync();
    
    var blobStorageService = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
    await blobStorageService.InitializeAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Upload Endpoint (File Upload Only)
app.MapPost("/upload", async (IFormFile file, string? documentId, string? metadata, 
    int? chunkSize, int? chunkOverlap, IDocumentProcessingService documentService) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest(new UploadTextResponse
        {
            Success = false,
            Message = "No file provided or file is empty."
        });
    }

    var request = new UploadFileRequest
    {
        File = file,
        DocumentId = documentId,
        Metadata = metadata,
        ChunkSize = chunkSize ?? 300,
        ChunkOverlap = chunkOverlap ?? 20
    };

    var response = await documentService.ProcessFileAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Error processing file",
        detail: response.Message,
        statusCode: 500);
})
.WithName("UploadFile")
.WithOpenApi()
.WithSummary("Uploads a file, extracts text, splits it into chunks and creates embeddings")
.WithDescription("This endpoint accepts files (.txt, .md, .pdf, .docx), extracts text, splits it into chunks, creates embeddings and stores them in Azure AI Search. Maximum file size: 12MB.")
.DisableAntiforgery();

// Search Endpoint
app.MapPost("/search", async (SearchRequest request, ISearchOrchestrationService searchService) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new SearchResponse
        {
            Query = request.Query,
            Success = false,
            Message = "Search query cannot be empty."
        });
    }

    if (request.MaxResults <= 0 || request.MaxResults > 50)
    {
        return Results.BadRequest(new SearchResponse
        {
            Query = request.Query,
            Success = false,
            Message = "MaxResults must be between 1 and 50."
        });
    }

    var response = await searchService.SearchAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Error during search",
        detail: response.Message,
        statusCode: 500);
})
.WithName("SearchDocuments")
.WithOpenApi()
.WithSummary("Searches documents and generates an answer with GPT-4o")
.WithDescription("This endpoint performs a semantic search in the Azure AI Search database and generates an answer with GPT-4o based on the found documents.");

// Documents List Endpoint
app.MapPost("/documents", async (DocumentListRequest request, IDocumentManagementService documentService) =>
{
    if (request.MaxResults <= 0 || request.MaxResults > 100)
    {
        return Results.BadRequest(new DocumentListResponse
        {
            Success = false,
            Message = "MaxResults must be between 1 and 100."
        });
    }

    if (request.Skip < 0)
    {
        return Results.BadRequest(new DocumentListResponse
        {
            Success = false,
            Message = "Skip must be 0 or greater."
        });
    }

    var response = await documentService.GetAllDocumentsAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Error retrieving documents",
        detail: response.Message,
        statusCode: 500);
})
.WithName("ListDocuments")
.WithOpenApi()
.WithSummary("Lists all documents in the database")
.WithDescription("This endpoint retrieves a list of all documents stored in Azure AI Search with their metadata, chunk counts, and sample content.");

// Alternative GET endpoint for simple document listing
app.MapGet("/documents", async (int maxResults, int skip, string? documentId, IDocumentManagementService documentService) =>
{
    var request = new DocumentListRequest
    {
        MaxResults = maxResults > 0 ? Math.Min(maxResults, 100) : 50,
        Skip = Math.Max(skip, 0),
        DocumentIdFilter = documentId
    };

    var response = await documentService.GetAllDocumentsAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Error retrieving documents",
        detail: response.Message,
        statusCode: 500);
})
.WithName("ListDocumentsGet")
.WithOpenApi()
.WithSummary("Lists all documents in the database (GET)")
.WithDescription("This endpoint retrieves a list of all documents stored in Azure AI Search. Use query parameters: maxResults (1-100, default 50), skip (default 0), documentId (optional filter).");

// Delete Document Endpoint
app.MapDelete("/documents/{documentId}", async (string documentId, IDocumentManagementService documentService) =>
{
    if (string.IsNullOrWhiteSpace(documentId))
    {
        return Results.BadRequest(new DeleteDocumentResponse
        {
            DocumentId = documentId,
            Success = false,
            Message = "Document ID cannot be empty."
        });
    }

    var request = new DeleteDocumentRequest { DocumentId = documentId };
    var response = await documentService.DeleteDocumentAsync(request);
    
    if (response.Success)
    {
        return Results.Ok(response);
    }
    else if (response.Message.Contains("not found"))
    {
        return Results.NotFound(response);
    }
    else
    {
        return Results.Problem(
            title: "Error deleting document",
            detail: response.Message,
            statusCode: 500);
    }
})
.WithName("DeleteDocument")
.WithOpenApi()
.WithSummary("Deletes a document and all its chunks")
.WithDescription("This endpoint deletes a document and all its associated chunks from Azure AI Search. The operation cannot be undone.");

// Alternative DELETE endpoint using request body
app.MapPost("/documents/delete", async (DeleteDocumentRequest request, IDocumentManagementService documentService) =>
{
    if (string.IsNullOrWhiteSpace(request.DocumentId))
    {
        return Results.BadRequest(new DeleteDocumentResponse
        {
            DocumentId = request.DocumentId,
            Success = false,
            Message = "Document ID cannot be empty."
        });
    }

    var response = await documentService.DeleteDocumentAsync(request);
    
    if (response.Success)
    {
        return Results.Ok(response);
    }
    else if (response.Message.Contains("not found"))
    {
        return Results.NotFound(response);
    }
    else
    {
        return Results.Problem(
            title: "Error deleting document",
            detail: response.Message,
            statusCode: 500);
    }
})
.WithName("DeleteDocumentPost")
.WithOpenApi()
.WithSummary("Deletes a document and all its chunks (POST)")
.WithDescription("This endpoint deletes a document and all its associated chunks from Azure AI Search using a POST request with JSON body. The operation cannot be undone.");

// Secure Download Endpoints
app.MapPost("/download/token", async (GenerateDownloadTokenRequest request, IDownloadService downloadService) =>
{
    if (string.IsNullOrWhiteSpace(request.DocumentId))
    {
        return Results.BadRequest(new { error = "DocumentId is required" });
    }
    
    if (request.ExpirationMinutes <= 0 || request.ExpirationMinutes > 60)
    {
        return Results.BadRequest(new { error = "ExpirationMinutes must be between 1 and 60" });
    }
    
    var response = await downloadService.GenerateDownloadTokenAsync(
        request.DocumentId, 
        userId: null, // No user tracking needed
        expiration: TimeSpan.FromMinutes(request.ExpirationMinutes));
    
    if (!response.Success)
    {
        if (response.ErrorMessage?.Contains("not found") == true)
        {
            return Results.NotFound(new { error = response.ErrorMessage });
        }
        return Results.Problem(response.ErrorMessage ?? "Failed to generate download token");
    }
    
    return Results.Ok(response);
})
.WithName("GenerateDownloadToken")
.WithOpenApi()
.WithSummary("Generates a secure, time-limited download token for a document")
.WithDescription("Creates a secure token that allows downloading a specific document. The token expires after the specified time and can only be used for the requested document.")
.WithTags("Downloads");

// POST endpoint for secure file downloads using token in request body
app.MapPost("/download/file", async (TokenDownloadRequest request, IDownloadService downloadService) =>
{
    if (string.IsNullOrWhiteSpace(request.Token))
    {
        return Results.BadRequest(new { error = "Download token is required" });
    }
    
    // 1. Token validieren
    var validation = await downloadService.ValidateDownloadTokenAsync(request.Token);
    if (!validation.IsValid)
    {
        if (validation.ErrorMessage?.Contains("expired") == true)
        {
            return Results.Problem(
                title: "Token Expired",
                detail: "The download token has expired. Please generate a new one.",
                statusCode: 410); // Gone
        }
        return Results.Problem(
            title: "Invalid Token",
            detail: validation.ErrorMessage ?? "Invalid download token",
            statusCode: 401); // Unauthorized
    }
    
    // 2. Datei herunterladen
    try
    {
        var fileResult = await downloadService.GetFileForDownloadAsync(validation.DocumentId);
        if (!fileResult.Success || fileResult.FileStream == null)
        {
            return Results.NotFound(new { error = fileResult.ErrorMessage ?? "File not found" });
        }
        
        return Results.File(fileResult.FileStream, fileResult.ContentType, fileResult.FileName);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Download failed: {ex.Message}");
    }
})
.WithName("DownloadFileWithToken")
.WithOpenApi()
.WithSummary("Downloads a file using a secure token")
.WithDescription("Downloads the file associated with the provided download token. The token must be valid and not expired. Token is provided in the request body for better security.")
.WithTags("Downloads");

// Data Migration Endpoint (for administrators)
app.MapPost("/admin/migrate/optimize-metadata", async (IDataMigrationService migrationService) =>
{
    var success = await migrationService.MigrateToOptimizedMetadataStorageAsync();
    
    if (success)
    {
        return Results.Ok(new { 
            success = true, 
            message = "Migration completed successfully. Metadata is now stored only in chunk 0, reducing storage redundancy by ~98%."
        });
    }
    else
    {
        return Results.Problem(
            title: "Migration Failed",
            detail: "Failed to complete metadata optimization migration. Check logs for details.",
            statusCode: 500);
    }
})
.WithName("MigrateOptimizeMetadata")
.WithOpenApi()
.WithSummary("Migrates existing documents to optimized metadata storage")
.WithDescription("Optimizes storage by moving all document metadata (filename, size, content type) to chunk 0 only, removing redundancy from other chunks. This reduces storage usage by ~98% for metadata.")
.WithTags("Administration", "Migration");

app.Run();
