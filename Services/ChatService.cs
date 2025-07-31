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
                return "No relevant information found in the database.";
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
            _logger.LogError(ex, "Error generating answer for query: {Query}", query);
            return "An error occurred while generating the answer.";
        }
    }

    private string BuildContextFromResults(List<SearchResult> searchResults)
    {
        var contextParts = searchResults
            .Take(5) // Limit to top 5 results
            .Select((result, index) => 
                $"[Source {index + 1}]\n" +
                $"Document ID: {result.DocumentId}\n" +
                $"Relevance Score: {result.Score:F2}\n" +
                $"Content: {result.Content}\n" +
                (string.IsNullOrEmpty(result.Metadata) ? "" : $"Metadata: {result.Metadata}\n"))
            .ToList();

        return string.Join("\n---\n", contextParts);
    }

    private string BuildSystemPrompt()
    {
        return @"You are a helpful AI assistant that answers questions based on provided documents.

Tasks:
1. Answer the user's question precisely and helpfully based on the provided sources
2. Use only information from the provided sources
3. If the information is insufficient, say so honestly
4. Provide specific source references (e.g., ""According to Source 1..."")
5. Respond in German in a natural, understandable style

Guidelines:
- Be precise and factual
- Use information from the sources directly
- Do not invent information
- Structure your answer logically
- Indicate if information differs between sources";
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"User question: {query}

Available sources:
{context}

Please answer the question based on the provided sources:";
    }
}
