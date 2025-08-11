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
    private readonly ISearchService _searchService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatService> _logger;
    private readonly string _chatModel;

    public ChatService(
        AzureOpenAIClient azureOpenAIClient, 
        ISearchService searchService,
        IConfiguration configuration, 
        ILogger<ChatService> logger)
    {
        _chatModel = configuration["AzureOpenAI:ChatDeploymentName"] ?? "gpt-5-chat";
        _chatClient = azureOpenAIClient.GetChatClient(_chatModel);
        _searchService = searchService;
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
            var userPrompt = BuildUserPromptWithContext(query, context, searchResults);
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

IMPORTANT SOURCE RULES:
- Use ONLY real filenames (e.g. ""handbook.pdf"", ""manual.docx"") in citations
- NEVER use DocumentIDs (long alphanumeric combinations like ""a1b2c3d4-e5f6-789..."") as filenames
- If chat history only mentions DocumentIDs, use ""Frühere Diskussion"" (Previous Discussion) instead
- CRITICAL: Only cite sources that actually contain information relevant to answering the current question
- If chat history doesn't contain relevant information for the current question, clearly state this instead of citing irrelevant sources
- Example WRONG: ***a1b2c3d4-e5f6-789g-hijk-lmnop456789q:*** Information...
- Example CORRECT: ***Benutzerhandbuch.pdf:*** Information... OR **Frühere Diskussion:** Information... OR no citations if no relevant info exists

Remember: You are helping users access and explore their document knowledge through conversation history. Use only real filenames, never internal document IDs in citations. Never ask follow-up questions.";

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
        var contextBuilder = new StringBuilder();
        contextBuilder.AppendLine("Based on the following relevant information:");
        contextBuilder.AppendLine();

        var adjacentChunksToInclude = _configuration.GetValue<int>("ChatService:AdjacentChunksToInclude", 2);
        _logger.LogInformation("Building context with {AdjacentCount} adjacent chunks for each relevant result", adjacentChunksToInclude);

        // Group results by document to process them efficiently
        var resultsByDocument = searchResults
            .GroupBy(r => r.DocumentId)
            .ToList();

        var processedChunks = new HashSet<string>(); // Track processed chunks to avoid duplicates
        int sourceCounter = 1;

        foreach (var documentGroup in resultsByDocument)
        {
            var documentId = documentGroup.Key;
            var documentsResults = documentGroup.ToList();

            _logger.LogDebug("Processing document {DocumentId} with {ResultCount} relevant chunks", 
                documentId, documentsResults.Count);

            // Get all unique chunk indices for this document
            var chunkIndices = documentsResults.Select(r => r.ChunkIndex).Distinct().OrderBy(x => x).ToList();

            // For each chunk index, get adjacent chunks
            foreach (var chunkIndex in chunkIndices)
            {
                try
                {
                    // Get adjacent chunks (including the target chunk)
                    var adjacentChunks = await _searchService.GetAdjacentChunksAsync(documentId, chunkIndex, adjacentChunksToInclude);
                    
                    if (!adjacentChunks.Any())
                    {
                        _logger.LogWarning("No adjacent chunks found for DocumentId: {DocumentId}, ChunkIndex: {ChunkIndex}", 
                            documentId, chunkIndex);
                        continue;
                    }

                    // Get the original search result for this chunk to preserve score and metadata
                    var originalResult = documentsResults.FirstOrDefault(r => r.ChunkIndex == chunkIndex);
                    if (originalResult == null) continue;

                    // Build context section for this expanded chunk group
                    contextBuilder.AppendLine($"=== SOURCE {sourceCounter} ===");
                    
                    // CRITICAL FIX: Ensure we never show DocumentIDs as filenames
                    string displayFileName;
                    if (!string.IsNullOrEmpty(originalResult.OriginalFileName))
                    {
                        displayFileName = originalResult.OriginalFileName;
                    }
                    else
                    {
                        displayFileName = $"Document-{sourceCounter}";
                        _logger.LogWarning("No OriginalFileName available for DocumentId {DocumentId}, using fallback: {FileName}", 
                            documentId, displayFileName);
                    }
                    
                    contextBuilder.AppendLine($"📄 DOCUMENT FILENAME: {displayFileName}");
                    contextBuilder.AppendLine($"🎯 RELEVANCE SCORE: {originalResult.Score:F2}");
                    contextBuilder.AppendLine($"📍 TARGET CHUNK: {chunkIndex} (with {adjacentChunks.Count - 1} adjacent chunks)");
                    contextBuilder.AppendLine();

                    // Add all adjacent chunks in order
                    foreach (var chunk in adjacentChunks.OrderBy(c => c.ChunkIndex))
                    {
                        // Skip if we've already processed this chunk
                        if (processedChunks.Contains(chunk.Id))
                            continue;

                        if (chunk.ChunkIndex == chunkIndex)
                        {
                            contextBuilder.AppendLine($"🎯 **RELEVANT CHUNK {chunk.ChunkIndex}** (Target):");
                        }
                        else
                        {
                            contextBuilder.AppendLine($"📄 Context Chunk {chunk.ChunkIndex}:");
                        }
                        
                        contextBuilder.AppendLine(chunk.Content);
                        contextBuilder.AppendLine();

                        processedChunks.Add(chunk.Id);
                    }

                    contextBuilder.AppendLine("=== END SOURCE ===");
                    contextBuilder.AppendLine();
                    sourceCounter++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error loading adjacent chunks for DocumentId: {DocumentId}, ChunkIndex: {ChunkIndex}", 
                        documentId, chunkIndex);
                    
                    // Fallback: Use only the original chunk
                    var originalResult = documentsResults.FirstOrDefault(r => r.ChunkIndex == chunkIndex);
                    if (originalResult != null)
                    {
                        contextBuilder.AppendLine($"=== SOURCE {sourceCounter} (FALLBACK) ===");
                        
                        // CRITICAL FIX: Ensure we never show DocumentIDs as filenames in fallback
                        string fallbackFileName;
                        if (!string.IsNullOrEmpty(originalResult.OriginalFileName))
                        {
                            fallbackFileName = originalResult.OriginalFileName;
                        }
                        else
                        {
                            fallbackFileName = $"Document-{sourceCounter}";
                            _logger.LogWarning("No OriginalFileName in fallback for DocumentId {DocumentId}, using: {FileName}", 
                                documentId, fallbackFileName);
                        }
                        
                        contextBuilder.AppendLine($"📄 DOCUMENT FILENAME: {fallbackFileName}");
                        contextBuilder.AppendLine($"🎯 RELEVANCE SCORE: {originalResult.Score:F2}");
                        contextBuilder.AppendLine($"📍 CHUNK: {chunkIndex}");
                        contextBuilder.AppendLine();
                        contextBuilder.AppendLine("CONTENT:");
                        contextBuilder.AppendLine(originalResult.Content);
                        contextBuilder.AppendLine();
                        contextBuilder.AppendLine("=== END SOURCE ===");
                        contextBuilder.AppendLine();
                        sourceCounter++;
                    }
                }
            }
        }

        var finalContext = contextBuilder.ToString();
        _logger.LogInformation("Built context with {SourceCount} sources and {AdjacentCount} adjacent chunks per relevant result", 
            sourceCounter - 1, adjacentChunksToInclude);
        
        return finalContext;
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

IMPORTANT SOURCE RULES:
- Use ONLY real filenames (e.g. ""user-manual.pdf"", ""instructions.docx"") in citations
- NEVER use DocumentIDs (long alphanumeric combinations) as filenames
- If no real filenames are available, use generic descriptions like ""Document 1"", ""Document 2""
- CRITICAL: Only cite sources that actually contain information relevant to answering the current question
- If provided sources don't contain relevant information, do NOT cite them in the Quellen section

Remember: DriftMind is a tool to access document knowledge, not a general AI assistant. Use only real filenames in citations, never internal document IDs. Never ask follow-up questions.";
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

CITATION AND SOURCE ATTRIBUTION:
CRITICAL: Maintain exact correspondence between information and sources!

1. Each source is clearly marked with === SOURCE X === boundaries
2. NEVER mix information from different sources in citations
3. For current document sources: 
   - Use format: ***[Document filename]:*** [Brief description of content]
4. For history-enhanced sources (marked with �):
   - Use format: ***[Document filename]:*** [Brief description of content] (aus Chatverlauf)
5. NEVER use complex phrases like ""Aus vorheriger Diskussion relevantes Dokument"" or ""Ergänzend zu den Dokumenteninhalten""
6. ONLY cite information that is actually present in the specific document you reference

STRICT ATTRIBUTION RULES:
- If you use information from SOURCE 1, cite the document filename from SOURCE 1
- If you use information from SOURCE 2, cite the document filename from SOURCE 2  
- NEVER attribute information from one source to a different source's document
- When uncertain about source attribution, DO NOT make citations
- CRITICAL: If no sources contain relevant information for the current question, do NOT cite any sources
- Only cite sources that actually contain information relevant to answering the current question

CITATION FORMAT:
End with a simple **Quellen:** section:
- ***Document-Name.pdf:*** [Topic/Content description]
- ***Another-Document.pdf:*** [Topic/Content description] (aus Chatverlauf)

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
End with a **Quellen:** section using the original simple format:
- ***[Document filename]:*** [Brief topic/content description]
- ***[Document filename]:*** [Brief topic/content description] (aus Chatverlauf)

IMPORTANT SOURCE RULES:
- Use ONLY real filenames (e.g. ""user-manual.pdf"", ""instructions.docx"") in citations
- NEVER use DocumentIDs (long alphanumeric combinations) as filenames
- If no real filenames are available, use generic descriptions like ""Document 1"", ""Document 2""
- CRITICAL: Only cite sources that actually contain information relevant to answering the current question
- If provided sources don't contain relevant information, do NOT cite them in the Quellen section

Remember: DriftMind helps users access their document knowledge. Use only real filenames in citations, never internal document IDs. Never ask follow-up questions.";
    }

    private string BuildUserPromptWithContext(string query, string context)
    {
        return $@"Question: {query}

{context}";
    }

    private string BuildUserPromptWithContext(string query, string context, List<SearchResult> searchResults)
    {
        var prompt = new StringBuilder();
        prompt.AppendLine($"Question: {query}");
        prompt.AppendLine();
        
        // Check if results contain history-enhanced sources
        bool hasHistoryBasedResults = searchResults.Any(r => r.Score > 1.0); // History results get score boost
        
        if (hasHistoryBasedResults)
        {
            prompt.AppendLine("KRITISCHER HINWEIS: Ein Teil der folgenden Informationen stammt aus Dokumenten, die in der aktuellen Unterhaltung bereits als relevant identifiziert wurden (History-Enhanced Search).");
            prompt.AppendLine();
        }
        
        prompt.AppendLine(context);
        
        prompt.AppendLine();
        prompt.AppendLine("WICHTIGE QUELLENATTRIBUTION REGELN:");
        prompt.AppendLine("1. Verwenden Sie NUR Informationen aus den bereitgestellten Quellen");
        prompt.AppendLine("2. Jede Quelle ist klar mit === SOURCE X === markiert");
        prompt.AppendLine("3. Für HISTORY-ENHANCED SOURCES: Fügen Sie '(aus Chatverlauf)' am Ende hinzu");
        prompt.AppendLine("4. Für CURRENT SEARCH RESULTS: Normale Quellenangabe ohne Zusatz");
        prompt.AppendLine("5. Verwenden Sie nur den exakten Dateinamen aus '📄 DOCUMENT FILENAME:'");
        prompt.AppendLine("6. NIEMALS Informationen aus einem Dokument einem anderen Dokument zuordnen!");
        
        if (hasHistoryBasedResults)
        {
            prompt.AppendLine();
            prompt.AppendLine("HISTORY-ENHANCED SOURCE INSTRUCTIONS:");
            prompt.AppendLine("- Format: ***filename:*** [description] (aus Chatverlauf)");
            prompt.AppendLine("- Nur verwenden wenn die Information aus einer � HISTORY-ENHANCED SOURCE stammt");
        }
        
        return prompt.ToString();
    }
}
