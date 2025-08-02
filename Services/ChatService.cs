using Azure.AI.OpenAI;
using OpenAI.Chat;
using DriftMind.DTOs;
using System.Text;

namespace DriftMind.Services;

public interface IChatService
{
    Task<string> GenerateAnswerAsync(string query, List<SearchResult> searchResults);
}

public class ChatService : IChatService
{
    private readonly ChatClient _chatClient;
    private readonly IBlobStorageService _blobStorageService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatService> _logger;
    private readonly string _chatModel;

    public ChatService(
        AzureOpenAIClient azureOpenAIClient, 
        IBlobStorageService blobStorageService,
        IConfiguration configuration, 
        ILogger<ChatService> logger)
    {
        _chatModel = configuration["AzureOpenAI:ChatDeploymentName"] ?? "gpt-4o";
        _chatClient = azureOpenAIClient.GetChatClient(_chatModel);
        _blobStorageService = blobStorageService;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> GenerateAnswerAsync(string query, List<SearchResult> searchResults)
    {
        try
        {
            if (!searchResults.Any())
            {
                return "I couldn't find any relevant information to answer your question.";
            }

            // Get configuration values
            var maxSources = _configuration.GetValue<int>("ChatService:MaxSourcesForAnswer", 5);
            var minScore = _configuration.GetValue<double>("ChatService:MinScoreForAnswer", 0.3);

            _logger.LogDebug("Using ChatService configuration: MaxSources={MaxSources}, MinScore={MinScore}", 
                maxSources, minScore);

            // Filter and prioritize results with diversification (max 1 chunk per document)
            var candidateResults = searchResults
                .Where(r => r.IsRelevant && (r.Score ?? 0) > minScore)
                .OrderByDescending(r => r.Score)
                .ToList();

            // Diversify: Select best chunk per document (max 1 per DocumentId)
            var relevantResults = candidateResults
                .GroupBy(r => r.DocumentId)
                .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                .OrderByDescending(r => r.Score)
                .Take(maxSources)
                .ToList();

            _logger.LogDebug("Diversification: {CandidateCount} candidates → {GroupCount} documents → {FinalCount} selected sources", 
                candidateResults.Count, 
                candidateResults.GroupBy(r => r.DocumentId).Count(), 
                relevantResults.Count);

            // If no results meet the strict criteria, use diversified results from all relevant
            if (!relevantResults.Any())
            {
                var fallbackCandidates = searchResults
                    .Where(r => r.IsRelevant)
                    .OrderByDescending(r => r.Score)
                    .ToList();

                relevantResults = fallbackCandidates
                    .GroupBy(r => r.DocumentId)
                    .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
                    .OrderByDescending(r => r.Score)
                    .Take(3) // Use top 3 documents if we lower the bar
                    .ToList();

                _logger.LogDebug("Fallback diversification: {FallbackCount} candidates → {FallbackFinal} selected sources", 
                    fallbackCandidates.Count, relevantResults.Count);
            }

            if (!relevantResults.Any())
            {
                _logger.LogWarning("No relevant results found for answer generation. Query: {Query}, Total results: {Count}", 
                    query, searchResults.Count);
                return "Es konnten keine ausreichend relevanten Informationen gefunden werden, um Ihre Frage zu beantworten. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
            }

            // Log source diversity information
            var uniqueDocuments = relevantResults.Select(r => r.DocumentId).Distinct().Count();
            var documentCounts = relevantResults.GroupBy(r => r.DocumentId)
                .Select(g => new { DocumentId = g.Key, Count = g.Count() })
                .ToList();

            _logger.LogInformation("Using {ChunkCount} chunks from {DocumentCount} different documents for answer generation. Query: {Query}", 
                relevantResults.Count, uniqueDocuments, query);

            _logger.LogDebug("Source distribution: {SourceDistribution}", 
                string.Join(", ", documentCounts.Select(d => $"{d.DocumentId.Substring(0, Math.Min(8, d.DocumentId.Length))}...({d.Count})")));

            var context = await BuildContextFromResultsAsync(relevantResults);
            var systemPrompt = BuildEnhancedSystemPrompt();
            var userPrompt = BuildUserPrompt(query, context);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            
            _logger.LogInformation("Generated answer with {SourceCount} relevant sources for query: {Query}", 
                relevantResults.Count, query);
            
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating answer for query: {Query}", query);
            return "I apologize, but I encountered an error while generating the answer. Please try again.";
        }
    }

    private async Task<string> BuildContextFromResultsAsync(List<SearchResult> searchResults)
    {
        // Build context from relevant results only
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("Based on the following relevant information:");
        contextBuilder.AppendLine();

        // Group results by document to avoid loading same file multiple times
        var documentGroups = searchResults
            .Where(r => !string.IsNullOrEmpty(r.BlobPath))
            .GroupBy(r => r.BlobPath)
            .ToList();

        // Load original file contents for enhanced context (prioritize text content for PDF/Word)
        var originalFileContents = new Dictionary<string, string>();
        foreach (var group in documentGroups)
        {
            try
            {
                var blobPath = group.Key;
                var result = group.First(); // Get first result to check content type
                
                // Try to load text content first (for PDF/Word files)
                if (!string.IsNullOrEmpty(result.TextContentBlobPath))
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    var textContent = await _blobStorageService.GetTextContentAsync(result.TextContentBlobPath);
                    
                    if (!string.IsNullOrEmpty(textContent))
                    {
                        originalFileContents[blobPath!] = textContent;
                        _logger.LogInformation("Loaded extracted text content from blob: {TextBlobPath} for file: {OriginalFileName}", 
                            result.TextContentBlobPath, result.OriginalFileName);
                        continue; // Skip loading original file if we have text content
                    }
                }
                
                // Fallback to original file for direct text files
                if (!string.IsNullOrEmpty(blobPath) && IsTextFile(result.ContentType, result.OriginalFileName))
                {
                    // Use a timeout to prevent hanging
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var fileContent = await _blobStorageService.GetFileContentAsync(blobPath!);
                    
                    if (!string.IsNullOrEmpty(fileContent))
                    {
                        originalFileContents[blobPath!] = fileContent;
                        _logger.LogInformation("Loaded original file content from blob: {BlobPath}", blobPath);
                    }
                }
                else
                {
                    _logger.LogDebug("Skipping non-text file without extracted content: {BlobPath} (ContentType: {ContentType})", 
                        blobPath, result.ContentType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load original file from blob: {BlobPath}", group.Key);
            }
        }

        for (int i = 0; i < searchResults.Count; i++)
        {
            var result = searchResults[i];
            contextBuilder.AppendLine($"Source {i + 1} (Score: {result.Score:F2}, Document: {result.DocumentId}):");
            
            // Include original file context if available
            if (!string.IsNullOrEmpty(result.BlobPath) && originalFileContents.ContainsKey(result.BlobPath))
            {
                var originalContent = originalFileContents[result.BlobPath];
                contextBuilder.AppendLine($"Original File: {result.OriginalFileName}");
                contextBuilder.AppendLine("Full Document Content:");
                contextBuilder.AppendLine(originalContent);
                contextBuilder.AppendLine();
                contextBuilder.AppendLine("Relevant Chunk:");
            }
            
            contextBuilder.AppendLine(result.Content);
            contextBuilder.AppendLine();
        }

        return contextBuilder.ToString();
    }

    private bool IsTextFile(string? contentType, string? fileName)
    {
        // Check content type first
        if (!string.IsNullOrEmpty(contentType))
        {
            var textTypes = new[] { "text/", "application/json", "application/xml" };
            if (textTypes.Any(type => contentType.StartsWith(type, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        // Check file extension as fallback
        if (!string.IsNullOrEmpty(fileName))
        {
            var textExtensions = new[] { ".txt", ".md", ".json", ".xml", ".csv", ".log" };
            var extension = Path.GetExtension(fileName).ToLowerInvariant();
            return textExtensions.Contains(extension);
        }

        return false;
    }

    private string BuildEnhancedSystemPrompt()
    {
        return @"
Du bist ein hilfreicher Assistent, der Fragen ausschließlich basierend auf dem bereitgestellten Quellmaterial beantwortet.

WICHTIGE REGELN:
1. Verwende nur Informationen, die direkt relevant für die Frage des Nutzers sind
2. Wenn die bereitgestellten Quellen keine relevanten Informationen enthalten, sage dies klar
3. Zitiere immer, auf welche Quelle du dich beziehst (z.B. ""Laut Quelle 1..."")
4. Erfinde keine Informationen, die nicht in den Quellen stehen
5. Wenn sich Quellen widersprechen, erwähne dies
6. Sei präzise aber umfassend
7. Antworte in deutscher Sprache in einem natürlichen, verständlichen Stil
8. Wenn der Relevanz-Score niedrig ist (<0.5), erwähne, dass die Information möglicherweise nicht direkt verwandt ist
9. Akzeptiere auch Quellen mit mittleren Relevanz-Scores (0.3-0.5) als brauchbar
10. Kombiniere Informationen aus mehreren Quellen, wenn sie sich ergänzen

Formatiere deine Antwort klar mit Quellenangaben.";
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"Question: {query}

{context}

Please answer the question based only on the provided sources. If the sources don't contain relevant information for the question, please state this clearly:";
    }
}
