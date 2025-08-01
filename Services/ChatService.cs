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
    private readonly ILogger<ChatService> _logger;
    private readonly string _chatModel;

    public ChatService(AzureOpenAIClient azureOpenAIClient, IConfiguration configuration, ILogger<ChatService> logger)
    {
        _chatModel = configuration["AzureOpenAI:ChatDeploymentName"] ?? "gpt-4o";
        _chatClient = azureOpenAIClient.GetChatClient(_chatModel);
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

            // Filter and prioritize results (more lenient for multi-language support)
            var relevantResults = searchResults
                .Where(r => r.IsRelevant && (r.Score ?? 0) > 0.3) // Reduced from 0.5 to 0.3
                .OrderByDescending(r => r.Score)
                .Take(5) // Limit to top 5 most relevant
                .ToList();

            // If no results meet the strict criteria, use all results marked as relevant
            if (!relevantResults.Any())
            {
                relevantResults = searchResults
                    .Where(r => r.IsRelevant)
                    .OrderByDescending(r => r.Score)
                    .Take(3) // Use top 3 if we lower the bar
                    .ToList();
            }

            if (!relevantResults.Any())
            {
                _logger.LogWarning("No relevant results found for answer generation. Query: {Query}, Total results: {Count}", 
                    query, searchResults.Count);
                return "Es konnten keine ausreichend relevanten Informationen gefunden werden, um Ihre Frage zu beantworten. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
            }

            _logger.LogInformation("Using {Count} highly relevant chunks to generate answer for query: {Query}", 
                relevantResults.Count, query);

            var context = BuildContextFromResults(relevantResults);
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

    private string BuildContextFromResults(List<SearchResult> searchResults)
    {
        // Build context from relevant results only
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("Based on the following relevant information:");
        contextBuilder.AppendLine();

        for (int i = 0; i < searchResults.Count; i++)
        {
            var result = searchResults[i];
            contextBuilder.AppendLine($"Source {i + 1} (Score: {result.Score:F2}, Document: {result.DocumentId}):");
            contextBuilder.AppendLine(result.Content);
            contextBuilder.AppendLine();
        }

        return contextBuilder.ToString();
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

    private string BuildContextFromResults_Old(List<SearchResult> searchResults)
    {
        // Filter and prioritize results by relevance
        var relevantResults = searchResults
            .Where(r => r.IsRelevant && r.RelevanceScore > 0.3)
            .OrderByDescending(r => r.RelevanceScore)
            .ThenByDescending(r => r.Score)
            .Take(5) // Limit to top 5 most relevant results
            .ToList();

        if (!relevantResults.Any())
        {
            // Fallback to original results if no relevant ones found
            relevantResults = searchResults.Take(3).ToList();
        }

        var contextParts = relevantResults
            .Select((result, index) => 
                $"[Source {index + 1}]\n" +
                $"Document ID: {result.DocumentId}\n" +
                $"Search Score: {result.Score:F2}\n" +
                $"Relevance Score: {result.RelevanceScore:F2}\n" +
                $"Content: {result.Content}\n" +
                (string.IsNullOrEmpty(result.Metadata) ? "" : $"Metadata: {result.Metadata}\n"))
            .ToList();

        return string.Join("\n---\n", contextParts);
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"Question: {query}

{context}

Please answer the question based only on the provided sources. If the sources don't contain relevant information for the question, please state this clearly:";
    }
}
