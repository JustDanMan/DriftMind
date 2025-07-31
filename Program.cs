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
            Message = "Text darf nicht leer sein."
        });
    }

    if (request.ChunkSize <= 0)
    {
        return Results.BadRequest(new UploadTextResponse
        {
            Success = false,
            Message = "ChunkSize muss größer als 0 sein."
        });
    }

    var response = await documentService.ProcessTextAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Fehler beim Verarbeiten des Texts",
        detail: response.Message,
        statusCode: 500);
})
.WithName("UploadText")
.WithOpenApi()
.WithSummary("Lädt Text hoch, teilt ihn in Chunks auf und erstellt Embeddings")
.WithDescription("Dieser Endpunkt nimmt einen Text entgegen, teilt ihn in Chunks auf, erstellt Embeddings und speichert sie in Azure AI Search.");

// Search Endpoint
app.MapPost("/search", async (SearchRequest request, ISearchOrchestrationService searchService) =>
{
    if (string.IsNullOrWhiteSpace(request.Query))
    {
        return Results.BadRequest(new SearchResponse
        {
            Query = request.Query,
            Success = false,
            Message = "Suchanfrage darf nicht leer sein."
        });
    }

    if (request.MaxResults <= 0 || request.MaxResults > 50)
    {
        return Results.BadRequest(new SearchResponse
        {
            Query = request.Query,
            Success = false,
            Message = "MaxResults muss zwischen 1 und 50 liegen."
        });
    }

    var response = await searchService.SearchAsync(request);
    
    return response.Success ? Results.Ok(response) : Results.Problem(
        title: "Fehler bei der Suche",
        detail: response.Message,
        statusCode: 500);
})
.WithName("SearchDocuments")
.WithOpenApi()
.WithSummary("Durchsucht die Dokumente und generiert eine Antwort mit GPT-4o")
.WithDescription("Dieser Endpunkt führt eine semantische Suche in der Azure AI Search Datenbank durch und generiert eine Antwort mit GPT-4o basierend auf den gefundenen Dokumenten.");

app.Run();
