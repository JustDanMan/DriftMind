using Azure.AI.OpenAI;
using OpenAI.Chat;
using DriftMind.DTOs;
using DTO = DriftMind.DTOs;

namespace DriftMind.Services;

public interface IQueryExpansionService
{
    Task<string> ExpandQueryAsync(string originalQuery, List<DTO.ChatMessage>? chatHistory = null);
}

public class QueryExpansionService : IQueryExpansionService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<QueryExpansionService> _logger;

    public QueryExpansionService(
        AzureOpenAIClient azureOpenAIClient,
        IConfiguration configuration,
        ILogger<QueryExpansionService> logger)
    {
        var chatModel = configuration["AzureOpenAI:ChatDeploymentName"] ?? "gpt-5-chat";
        _chatClient = azureOpenAIClient.GetChatClient(chatModel);
        _logger = logger;
    }

    public async Task<string> ExpandQueryAsync(string originalQuery, List<DTO.ChatMessage>? chatHistory = null)
    {
        if (string.IsNullOrWhiteSpace(originalQuery))
        {
            _logger.LogDebug("Skipping query expansion because the original query is empty or whitespace");
            return originalQuery;
        }

        try
        {
            bool hasHistory = chatHistory?.Any() == true;

            _logger.LogInformation("Expanding query: '{OriginalQuery}' (HasHistory: {HasHistory})", 
                originalQuery, hasHistory);

            var systemPrompt = BuildExpansionSystemPrompt();
            var userPrompt = BuildExpansionUserPrompt(originalQuery, chatHistory);

            var messages = new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            var response = await _chatClient.CompleteChatAsync(messages);
            var expandedQuery = response.Value.Content[0].Text.Trim();

            // Validate the expanded query is meaningful and different
            if (string.IsNullOrWhiteSpace(expandedQuery) || 
                expandedQuery.Equals(originalQuery, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Query expansion returned same or empty query, using original: '{OriginalQuery}'", originalQuery);
                return originalQuery;
            }

            _logger.LogInformation("Query expanded from '{OriginalQuery}' to '{ExpandedQuery}'", originalQuery, expandedQuery);
            return expandedQuery;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error expanding query '{OriginalQuery}', using original", originalQuery);
            return originalQuery;
        }
    }

    private string BuildExpansionSystemPrompt()
    {
        return @"
You are a query expansion specialist. Your task is to transform short, vague, or context-poor search queries into more detailed and specific search queries that will yield better document search results.

RULES:
1. Expand short queries (under 20 characters) into more detailed search terms
2. Add relevant context and synonyms to improve search accuracy
3. Transform vague language into specific search terms
4. Keep the core intent of the original query
5. Generate 1-2 additional relevant search terms or phrases
6. Do not completely change the meaning of the query
7. Focus on making the query more searchable and specific
8. Respond ONLY with the expanded query, no explanations
9. Keep the expanded query under 100 words
10. Use German if the original query is in German, English if in English

EXAMPLES:
Input: ""Infos zu XY""
Output: ""Informationen Details Eigenschaften Merkmale XY""

Input: ""Was hast du zu Projekt Alpha?""  
Output: ""Projekt Alpha Dokumentation Spezifikationen Details Anforderungen Status""

Input: ""Tell me about the integration""
Output: ""integration process implementation details configuration steps setup documentation""
";
    }

    private string BuildExpansionUserPrompt(string originalQuery, List<DTO.ChatMessage>? chatHistory)
    {
        var prompt = $"Original query: {originalQuery}";

        if (chatHistory?.Any() == true)
        {
            var contextMessages = chatHistory
                .TakeLast(5) // Only use last 5 messages for context
                .Where(m => !string.IsNullOrWhiteSpace(m.Content))
                .ToList();

            if (contextMessages.Any())
            {
                prompt += "\n\nRecent conversation context:";
                foreach (var message in contextMessages)
                {
                    prompt += $"\n{message.Role}: {message.Content.Substring(0, Math.Min(message.Content.Length, 150))}";
                    if (message.Content.Length > 150) prompt += "...";
                }
            }
        }

        prompt += "\n\nExpand this query to make it more specific and searchable:";
        return prompt;
    }

}
