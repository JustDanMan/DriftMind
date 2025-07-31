using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents.Indexes;
using DriftMind.DTOs;
using DriftMind.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
builder.Services.AddScoped<IDocumentProcessingService, DocumentProcessingService>();
builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<ISearchOrchestrationService, SearchOrchestrationService>();

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

app.Run();
