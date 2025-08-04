using Azure.AI.OpenAI;
using OpenAI.Chat;
using DriftMind.DTOs;
using System.Text;
using DTO = DriftMind.DTOs;

namespace DriftMind.Services;

public interface IChatService
{
    Task<string> GenerateAnswerAsync(string query, List<SearchResult> searchResults);
    Task<string> GenerateAnswerWithHistoryAsync(string query, List<SearchResult> searchResults, List<DTO.ChatMessage>? chatHistory);
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

            _logger.LogDebug("Using {ResultCount} pre-filtered and limited results for answer generation", searchResults.Count);

            // Use all the already filtered, diversified and limited results from SearchOrchestrationService
            // No additional filtering or limiting to ensure consistency between AI input and displayed sources
            var relevantResults = searchResults.ToList();

            if (!relevantResults.Any())
            {
                _logger.LogWarning("No relevant results found for answer generation. Query: {Query}, Total results: {Count}", 
                    query, searchResults.Count);
                return "Es konnten keine ausreichend relevanten Informationen gefunden werden, um Ihre Frage zu beantworten. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
            }

            // Log source information
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

            var messages = new List<OpenAI.Chat.ChatMessage>
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

    public async Task<string> GenerateAnswerWithHistoryAsync(string query, List<SearchResult> searchResults, List<DTO.ChatMessage>? chatHistory)
    {
        try
        {
            if (!searchResults.Any())
            {
                // When no search results, check if we can answer from chat history
                if (chatHistory?.Any() == true)
                {
                    return await GenerateAnswerFromHistoryOnlyAsync(query, chatHistory);
                }
                return "I couldn't find any relevant information to answer your question.";
            }

            _logger.LogDebug("Using {ResultCount} pre-filtered and limited results for answer generation with history", searchResults.Count);

            // Use all the already filtered, diversified and limited results from SearchOrchestrationService
            // No additional filtering or limiting to ensure consistency between AI input and displayed sources
            var relevantResults = searchResults.ToList();

            if (!relevantResults.Any())
            {
                _logger.LogWarning("No relevant results found for answer generation with history. Query: {Query}, Total results: {Count}", 
                    query, searchResults.Count);
                
                // Fallback to history-only answer if available
                if (chatHistory?.Any() == true)
                {
                    return await GenerateAnswerFromHistoryOnlyAsync(query, chatHistory);
                }
                
                return "Es konnten keine ausreichend relevanten Informationen gefunden werden, um Ihre Frage zu beantworten. Bitte versuchen Sie eine andere Formulierung oder spezifischere Begriffe.";
            }

            // Log source information
            var uniqueDocuments = relevantResults.Select(r => r.DocumentId).Distinct().Count();
            var documentCounts = relevantResults.GroupBy(r => r.DocumentId)
                .Select(g => new { DocumentId = g.Key, Count = g.Count() })
                .ToList();

            _logger.LogInformation("Using {ChunkCount} chunks from {DocumentCount} different documents for answer generation with history. Query: {Query}", 
                relevantResults.Count, uniqueDocuments, query);

            _logger.LogDebug("Source distribution with history: {SourceDistribution}", 
                string.Join(", ", documentCounts.Select(d => $"{d.DocumentId.Substring(0, Math.Min(8, d.DocumentId.Length))}...({d.Count})")));

            var context = await BuildContextFromResultsAsync(relevantResults);
            var systemPrompt = BuildEnhancedSystemPromptWithHistory();

            // Build messages including chat history
            var messages = new List<OpenAI.Chat.ChatMessage>();
            messages.Add(new SystemChatMessage(systemPrompt));

            // Add chat history if available
            if (chatHistory?.Any() == true)
            {
                foreach (var historyMessage in chatHistory.TakeLast(10)) // Limit to last 10 messages to avoid token overflow
                {
                    if (historyMessage.Role.ToLower() == "user")
                    {
                        messages.Add(new UserChatMessage(historyMessage.Content));
                    }
                    else if (historyMessage.Role.ToLower() == "assistant")
                    {
                        messages.Add(new AssistantChatMessage(historyMessage.Content));
                    }
                }
            }

            // Add current query with context
            var userPrompt = BuildUserPromptWithContext(query, context);
            messages.Add(new UserChatMessage(userPrompt));

            var response = await _chatClient.CompleteChatAsync(messages);
            
            _logger.LogInformation("Generated answer with history using {SourceCount} relevant sources for query: {Query}", 
                relevantResults.Count, query);
            
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating answer with history for query: {Query}", query);
            return "I apologize, but I encountered an error while generating the answer. Please try again.";
        }
    }

    private async Task<string> GenerateAnswerFromHistoryOnlyAsync(string query, List<DTO.ChatMessage> chatHistory)
    {
        try
        {
            _logger.LogInformation("Generating answer from chat history only for query: {Query}", query);

            var systemPrompt = @"
You are a helpful assistant. Answer the user's question ONLY based on the previous chat history.
If the chat history contains no relevant information for the current question, you MUST state that you cannot answer the question because no relevant information is available in the chat history or uploaded documents. 
DO NOT use your general knowledge or training data to answer questions.
IMPORTANT: Do not correct false statements using your general knowledge. Only address information that is actually present in the chat history. If a user makes an incorrect statement that is not addressed in the chat history, do not provide corrections."
;

            var messages = new List<OpenAI.Chat.ChatMessage>();
            messages.Add(new SystemChatMessage(systemPrompt));

            // Add chat history (limit to last 15 messages to avoid token overflow)
            foreach (var historyMessage in chatHistory.TakeLast(15))
            {
                if (historyMessage.Role.ToLower() == "user")
                {
                    messages.Add(new UserChatMessage(historyMessage.Content));
                }
                else if (historyMessage.Role.ToLower() == "assistant")
                {
                    messages.Add(new AssistantChatMessage(historyMessage.Content));
                }
            }

            // Add current query
            messages.Add(new UserChatMessage(query));

            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating answer from history only for query: {Query}", query);
            return "I apologize, but I could not generate an answer based on the chat history.";
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
You are a helpful assistant who answers questions exclusively based on the provided source material.

IMPORTANT RULES:
1. Use only information that is directly relevant to the user's question
2. If the provided sources contain no relevant information, state this clearly
3. Always cite which source you are referring to (e.g., ""According to Source 1..."")
4. Do not invent information that is not in the sources
5. If sources contradict each other, mention this
6. Be precise but comprehensive
7. Respond in German in a natural, understandable style
8. If the relevance score is low (<0.5), mention that the information may not be directly related
9. Also accept sources with medium relevance scores (0.3-0.5) as usable
10. Combine information from multiple sources when they complement each other
11. NEVER correct false statements using your general knowledge - only use sources or chat history to address incorrect information
12. If a user makes a false claim that is not addressed in the sources or chat history, ignore the false claim and focus only on what you can answer from the available information

Format your answer clearly with source citations.";
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"Question: {query}

{context}

Please answer the question based only on the provided sources. If the sources don't contain relevant information for the question, please state this clearly:";
    }

    private string BuildEnhancedSystemPromptWithHistory()
    {
        return @"
You are a helpful assistant who answers questions based on the provided source material and chat history.

IMPORTANT RULES:
1. Use information from the provided sources as the primary source
2. Use the chat history for context and to establish references to previous conversations
3. If the provided sources contain no relevant information but the chat history is helpful, use it
4. Always cite which source you are referring to (e.g., ""According to Source 1..."" or ""As mentioned earlier..."")
5. Do not invent information that is neither in the sources nor in the chat history
6. If sources and chat history contradict, mention this and prioritize the sources
7. Be precise but comprehensive
8. Respond in German in a natural, understandable style
9. Establish references to the previous conversation when relevant
10. Meaningfully combine information from sources and chat history

Format your answer clearly with source citations and references to the chat history.";
    }

    private string BuildUserPromptWithContext(string query, string context)
    {
        return $@"Based on the previous chat history and the following sources, please answer the question:

Question: {query}

{context}

Please answer the question based on the provided sources and chat history. If the sources contain no relevant information but the chat history is helpful, use it:";
    }
}
