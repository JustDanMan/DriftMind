using Azure.AI.OpenAI;
using OpenAI.Chat;
using DriftMind.DTOs;

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
                return "Keine relevanten Informationen in der Datenbank gefunden.";
            }

            var context = BuildContextFromResults(searchResults);
            var systemPrompt = BuildSystemPrompt();
            var userPrompt = BuildUserPrompt(query, context);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fehler beim Generieren der Antwort für Query: {Query}", query);
            return "Es ist ein Fehler beim Generieren der Antwort aufgetreten.";
        }
    }

    private string BuildContextFromResults(List<SearchResult> searchResults)
    {
        var contextParts = searchResults
            .Take(5) // Begrenzen auf die top 5 Ergebnisse
            .Select((result, index) => 
                $"[Quelle {index + 1}]\n" +
                $"Dokument-ID: {result.DocumentId}\n" +
                $"Relevanz-Score: {result.Score:F2}\n" +
                $"Inhalt: {result.Content}\n" +
                (string.IsNullOrEmpty(result.Metadata) ? "" : $"Metadaten: {result.Metadata}\n"))
            .ToList();

        return string.Join("\n---\n", contextParts);
    }

    private string BuildSystemPrompt()
    {
        return @"Du bist ein hilfreicher AI-Assistent, der Fragen basierend auf bereitgestellten Dokumenten beantwortet.

Aufgaben:
1. Beantworte die Benutzerfrage präzise und hilfreich basierend auf den bereitgestellten Quellen
2. Verwende nur Informationen aus den bereitgestellten Quellen
3. Wenn die Informationen nicht ausreichen, sage das ehrlich
4. Gib konkrete Quellenangaben an (z.B. ""Laut Quelle 1..."")
5. Antworte auf Deutsch in einem natürlichen, verständlichen Stil

Richtlinien:
- Sei präzise und sachlich
- Verwende die Informationen aus den Quellen direkt
- Erfinde keine Informationen
- Strukturiere deine Antwort logisch
- Gib an, wenn sich Informationen zwischen Quellen unterscheiden";
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"Benutzerfrage: {query}

Verfügbare Quellen:
{context}

Bitte beantworte die Frage basierend auf den bereitgestellten Quellen:";
    }
}
