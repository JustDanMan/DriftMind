using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents.Indexes;
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

// Register Services
builder.Services.AddScoped<ITextChunkingService, TextChunkingService>();
builder.Services.AddScoped<IEmbeddingService, EmbeddingService>();
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddScoped<IFileProcessingService, FileProcessingService>();
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<ISearchOrchestrationService, SearchOrchestrationService>();
builder.Services.AddScoped<IDocumentManagementService, DocumentManagementService>();

var app = builder.Build();

// Initialize Azure Search Index
using (var scope = app.Services.CreateScope())
{
    var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
    await searchService.InitializeIndexAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

// Upload Endpoint
app.MapPost("/upload", async (UploadTextRequest request, IDocumentProcessingService documentService) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new UploadTextResponse
        {
            Success = false,
            Message = "Text cannot be empty."
        });
    }

    if (request.ChunkSize <= 0)
    {
        return Results.BadRequest(new UploadTextResponse
        {
            Success = false,
            Message = "ChunkSize must be greater than 0."
        });
    }

    var response = await documentService.ProcessTextAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Error processing text",
        detail: response.Message,
        statusCode: 500);
})
.WithName("UploadText")
.WithOpenApi()
.WithSummary("Uploads text, splits it into chunks and creates embeddings")
.WithDescription("This endpoint accepts text, splits it into chunks, creates embeddings and stores them in Azure AI Search.");

// Upload File Endpoint
app.MapPost("/upload/file", async (IFormFile file, string? documentId, string? metadata, 
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
        ChunkSize = chunkSize ?? 1000,
        ChunkOverlap = chunkOverlap ?? 200
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
.WithDescription("This endpoint accepts files (.txt, .md, .pdf, .docx), extracts text, splits it into chunks, creates embeddings and stores them in Azure AI Search. Maximum file size: 3MB.")
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

app.Run();
