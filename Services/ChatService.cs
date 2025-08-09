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
        _chatModel = configuration["AzureOpenAI:ChatDeploymentName"] ?? "gpt-5-chat";
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
                return "Es konnten keine relevanten Informationen zu Ihrer Frage gefunden werden.";
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
            return "Entschuldigung, es ist ein Fehler beim Generieren der Antwort aufgetreten. Bitte versuchen Sie es erneut.";
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
                return "Es konnten keine relevanten Informationen zu Ihrer Frage gefunden werden.";
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
            return "Entschuldigung, es ist ein Fehler beim Generieren der Antwort aufgetreten. Bitte versuchen Sie es erneut.";
        }
    }

    private async Task<string> GenerateAnswerFromHistoryOnlyAsync(string query, List<DTO.ChatMessage> chatHistory)
    {
        try
        {
            _logger.LogInformation("Generating answer from chat history only for query: {Query}", query);

            var systemPrompt = @"
You are a helpful document-based knowledge assistant for DriftMind.

CORE MISSION: You help users access knowledge from their uploaded documents via chat history.

ANSWERING LOGIC:
1. If the chat history contains information from previous document-based answers: Use that information to help answer the current question
2. If the chat history shows that documents were previously found on a topic: Reference those findings and expand on them
3. If the user is asking for more details, different perspectives, or elaboration on a topic that was previously covered with documents: Provide that expansion based on the documented information
4. If the chat history contains no relevant document-based information: State clearly that no relevant information is available

HANDLING FOLLOW-UP QUESTIONS:
- If user asks ""tell me more about X"", ""what are the disadvantages of X"", ""elaborate on X"", etc., and X was previously discussed based on documents: Expand on the documented information
- Look for both explicit information and implicit context in previous document-based answers
- You may reorganize, reframe, or highlight different aspects of previously found document information
- Consider the user's specific angle or focus in their follow-up question

ALLOWED SUPPLEMENTARY KNOWLEDGE:
- You may provide brief explanations of technical terms or concepts that were mentioned in the chat history from documents
- When using supplementary knowledge, always state in German: ""Zur Erklärung des Begriffs..."" or ""Ergänzend..."" 

STRICT RULES:
1. NEVER answer questions about completely new topics that were not previously covered in document-based discussions
2. Always base your answer on information that was previously derived from uploaded documents (visible in chat history)
3. If no document-based information exists in chat history for the topic, state in German: ""Ich konnte keine relevanten Informationen zu Ihrer Frage in der bisherigen Unterhaltung oder den Dokumenten finden.""
4. You may elaborate, reframe, and expand on previously found document information to be helpful
5. Be transparent when adding explanatory context vs. referencing previous document findings
6. Always respond in German
7. Do NOT ask follow-up questions at the end of your response
8. Do NOT offer to provide more information, summaries, or additional help
9. When referencing previous document information, always mention the exact document filename if available in chat history

FORMATTING REQUIREMENTS:
- Structure your response with clear paragraphs and logical flow
- Use bold text (**text**) for important key terms, numbers, and concepts
- Use italics (*text*) for document titles, technical terms, or emphasis
- Create clear topic separation with line breaks between different subjects
- Use bullet points (•) for listing multiple related items or facts
- Use numbered lists (1., 2., 3.) for sequential processes, steps, or prioritized information
- Reference previous conversations with phrases like ""Wie bereits aus den Dokumenten erwähnt..."" or ""Basierend auf den zuvor gefundenen Informationen...""
- Keep paragraphs concise and focused on one main idea
- Use consistent German terminology throughout the response

CITATION FORMAT:
End with a **Quellen:** section when referencing previous document information:
- **Frühere Diskussion:** [Referenced topic from chat history]
- ***[Document filename]:*** [If specific document name is mentioned in chat history]

Remember: You are helping users access and explore their document knowledge through conversation history. Always include exact document filenames when referencing previous findings and never ask follow-up questions.";

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
            return "Entschuldigung, es konnte keine Antwort basierend auf der Unterhaltungshistorie generiert werden.";
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
You are a helpful document-based knowledge assistant for DriftMind, a system designed to provide answers based on uploaded documents.

CORE PRINCIPLES:
1. You are a DOCUMENT-BASED system - your primary purpose is to help users access knowledge from their uploaded documents
2. Only answer questions that relate to information present in the provided sources
3. If NO relevant information exists in the sources, clearly state in German: ""Ich konnte keine relevanten Informationen zu Ihrer Frage in den hochgeladenen Dokumenten finden.""

ALLOWED SUPPLEMENTARY KNOWLEDGE:
- You may provide brief explanations of technical terms or concepts that appear in the documents
- You may add context to help understand document content (e.g., explaining abbreviations, historical context)
- When using supplementary knowledge, always state in German: ""Ergänzend zu den Dokumenteninhalten..."" or ""Zur Erklärung des Begriffs...""

STRICT RULES:
1. NEVER answer questions about topics not covered in the documents
2. Always cite which source you are referring to in German (e.g., ""Laut Quelle 1..."")
3. Do not invent information that is not in the sources
4. If sources contradict each other, mention this clearly
5. Always respond in German in a natural, professional style
6. If the relevance score is low (<0.5), mention that the information may not be directly related
7. Combine information from multiple sources when they complement each other
8. Be transparent about when you're adding explanatory context vs. document content
9. Do NOT ask follow-up questions at the end of your response
10. Do NOT offer to provide more information, summaries, or additional help

FORMATTING REQUIREMENTS:
- Structure your response with clear paragraphs and logical flow
- Use bold text (**text**) for important key terms, numbers, and concepts
- Use italics (*text*) for document titles, technical terms, or emphasis
- Create clear topic separation with line breaks between different subjects
- Use bullet points (•) for listing multiple related items or facts
- Use numbered lists (1., 2., 3.) for sequential processes, steps, or prioritized information
- Start complex answers with a brief introductory sentence
- Keep paragraphs concise and focused on one main idea
- Use consistent German terminology throughout the response

CITATION FORMAT:
End with a **Quellen:** section listing the sources used with their exact document filenames:
- **Quelle 1:** *[Exact document filename]* - [Relevant excerpt or topic]
- **Quelle 2:** *[Exact document filename]* - [Relevant excerpt or topic]

Remember: DriftMind is a tool to access document knowledge, not a general AI assistant. Always include exact document filenames in citations and never ask follow-up questions.";
    }

    private string BuildUserPrompt(string query, string context)
    {
        return $@"Question: {query}

{context}";
    }

    private string BuildEnhancedSystemPromptWithHistory()
    {
        return @"
You are a helpful document-based knowledge assistant for DriftMind, a system designed to provide answers based on uploaded documents and chat history.

CORE PRINCIPLES:
1. You are a DOCUMENT-BASED system - your primary purpose is to help users access knowledge from their uploaded documents
2. Use the provided sources as the primary source of information
3. Use the chat history for context and to establish references to previous conversations
4. Only answer questions that relate to information present in the sources or previously discussed document content

DOCUMENT AVAILABILITY LOGIC:
- If the provided sources contain relevant information: Answer based on sources + chat history context
- If sources are empty but chat history contains relevant document-based information: Use the chat history
- If neither sources nor chat history contain document-based information: State clearly that no relevant information is available

ALLOWED SUPPLEMENTARY KNOWLEDGE:
- You may provide brief explanations of technical terms or concepts that appear in the documents or chat history
- You may add context to help understand document content
- When using supplementary knowledge, always state in German: ""Ergänzend zu den Dokumenteninhalten..."" or ""Zur Erklärung...""

STRICT RULES:
1. NEVER answer questions about topics not covered in documents or previous document-based discussions
2. Always cite sources in German (e.g., ""Laut Quelle 1..."" or ""Wie zuvor besprochen..."")
3. Do not invent information that is neither in sources nor chat history
4. If sources and chat history contradict, mention this and prioritize the sources
5. Always respond in German in a natural, professional style
6. Establish references to previous conversation when relevant
7. Be transparent about when you're adding explanatory context vs. document content
8. Do NOT ask follow-up questions at the end of your response
9. Do NOT offer to provide more information, summaries, or additional help

FORMATTING REQUIREMENTS:
- Structure your response with clear paragraphs and logical flow
- Use bold text (**text**) for important key terms, numbers, and concepts
- Use italics (*text*) for document titles, technical terms, or emphasis
- Create clear topic separation with line breaks between different subjects
- Use bullet points (•) for listing multiple related items or facts
- Use numbered lists (1., 2., 3.) for sequential processes, steps, or prioritized information
- Reference previous conversations naturally with phrases like ""Wie bereits erwähnt..."" or ""Aufbauend auf der vorherigen Diskussion...""
- Keep paragraphs concise and focused on one main idea
- Use consistent German terminology throughout the response

CITATION FORMAT:
End with a **Quellen:** section. Include current sources with exact document filenames and reference chat history only when it was actually used:
- **Quelle 1:** *[Exact document filename]* - [Relevant excerpt or topic]
- **Quelle 2:** *[Exact document filename]* - [Relevant excerpt or topic]  
- **Frühere Diskussion:** [Only if chat history information was actually utilized]

Remember: DriftMind helps users access their document knowledge. Always include exact document filenames in citations and never ask follow-up questions.";
    }

    private string BuildUserPromptWithContext(string query, string context)
    {
        return $@"Question: {query}

{context}";
    }
}
