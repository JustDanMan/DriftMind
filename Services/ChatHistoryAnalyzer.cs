using DriftMind.DTOs;

namespace DriftMind.Services;

/// <summary>
/// Analyzer for extracting useful information from chat history
/// </summary>
public static class ChatHistoryAnalyzer
{
    /// <summary>
    /// Extracts relevant keywords from chat history with decay for older messages
    /// </summary>
    public static List<string> ExtractKeywords(List<ChatMessage>? chatHistory, int maxKeywords = 8)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var keywordWeights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var recentMessages = chatHistory.TakeLast(3).ToList();
        
        for (int i = 0; i < recentMessages.Count; i++)
        {
            var message = recentMessages[i];
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            // Apply decay factor: newer messages get higher weight
            double messageWeight = Math.Pow(0.7, recentMessages.Count - i - 1);

            var keywords = TextProcessingHelper.ExtractKeywords(message.Content, 8);
            foreach (var keyword in keywords)
            {
                keywordWeights[keyword] = keywordWeights.GetValueOrDefault(keyword, 0) + messageWeight;
            }
        }

        return keywordWeights
            .OrderByDescending(kv => kv.Value)
            .Take(maxKeywords)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <summary>
    /// Extracts document IDs and filenames from chat history sources
    /// </summary>
    public static List<string> ExtractDocumentReferences(List<ChatMessage>? chatHistory, ILogger? logger = null)
    {
        if (chatHistory?.Any() != true) return new List<string>();

        var documentReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recentAssistantMessages = chatHistory
            .Where(m => m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            .TakeLast(3);
        
        logger?.LogDebug("Extracting document references from {MessageCount} assistant messages", recentAssistantMessages.Count());
        
        foreach (var message in recentAssistantMessages)
        {
            if (string.IsNullOrWhiteSpace(message.Content)) continue;

            // Strategy: Look for source references in German format
            var lines = message.Content.Split('\n');
            bool inSourcesSection = false;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Check if we're in the sources section
                if (trimmedLine.StartsWith("**Quellen:**", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("Quellen:", StringComparison.OrdinalIgnoreCase) ||
                    trimmedLine.StartsWith("**Quelle", StringComparison.OrdinalIgnoreCase))
                {
                    inSourcesSection = true;
                    continue;
                }
                
                // Stop if we reach another section
                if (inSourcesSection && trimmedLine.StartsWith("**") && !trimmedLine.Contains("Quelle"))
                {
                    inSourcesSection = false;
                }
                
                // Extract from sources section
                if (inSourcesSection && !string.IsNullOrEmpty(trimmedLine))
                {
                    ExtractDocumentNamesFromLine(trimmedLine, documentReferences);
                }
            }
        }

        var results = documentReferences.Take(5).ToList();
        logger?.LogInformation("Extracted {Count} document references from chat history: {References}", 
            results.Count, string.Join(", ", results));
        
        return results;
    }

    /// <summary>
    /// Extracts filenames from chat history to find previous context
    /// </summary>
    public static List<string> ExtractDocumentIds(List<ChatMessage> chatHistory, ILogger? logger = null)
    {
        var filenames = new HashSet<string>();
        
        // Look for the most recent assistant response that contains sources
        for (int i = chatHistory.Count - 1; i >= 0; i--)
        {
            var message = chatHistory[i];
            if (message.Role == "assistant" && !string.IsNullOrEmpty(message.Content))
            {
                var sourceMatches = System.Text.RegularExpressions.Regex.Matches(
                    message.Content, 
                    @"\*\*Quelle:\s*([^\*]+)\*\*|\*\*Source:\s*([^\*]+)\*\*",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                
                foreach (System.Text.RegularExpressions.Match match in sourceMatches)
                {
                    var filename = match.Groups[1].Success ? match.Groups[1].Value.Trim() : match.Groups[2].Value.Trim();
                    if (!string.IsNullOrEmpty(filename))
                    {
                        filenames.Add(filename);
                        logger?.LogInformation("Found filename in chat history: '{Filename}'", filename);
                    }
                }
                
                if (filenames.Any())
                {
                    logger?.LogInformation("Found {Count} source references in recent assistant response", filenames.Count);
                    break;
                }
            }
        }
        
        return filenames.ToList();
    }

    /// <summary>
    /// Fallback method for keyword-based similarity detection
    /// </summary>
    public static bool HasSimilarKeywords(string query, List<string> previousQuestions, double threshold = 0.3)
    {
        var queryWords = TextProcessingHelper.ExtractMeaningfulWords(query);
        
        foreach (var previousQuestion in previousQuestions)
        {
            var previousWords = TextProcessingHelper.ExtractMeaningfulWords(previousQuestion);
            var commonWords = queryWords.Intersect(previousWords, StringComparer.OrdinalIgnoreCase).Count();
            var totalUniqueWords = queryWords.Union(previousWords, StringComparer.OrdinalIgnoreCase).Count();
            
            if (totalUniqueWords > 0)
            {
                var similarity = (double)commonWords / totalUniqueWords;
                if (similarity >= threshold)
                {
                    return true;
                }
            }
        }
        
        return false;
    }

    private static void ExtractDocumentNamesFromLine(string line, HashSet<string> documentReferences)
    {
        // Simple extraction: Look for document patterns
        var docMatches = System.Text.RegularExpressions.Regex.Matches(line, 
            @"([a-zA-Z0-9\-_]+\.(?:pdf|docx?|txt|md))", 
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        
        foreach (System.Text.RegularExpressions.Match match in docMatches)
        {
            var docName = match.Groups[1].Value.Trim();
            if (docName.Length > 3 && docName.Length < 100)
            {
                documentReferences.Add(docName);
            }
        }
    }

    /// <summary>
    /// Checks if a document is referenced in the chat history based on document ID and metadata
    /// </summary>
    public static bool DocumentIsReferencedInHistory(string? documentId, string? metadata, List<string> documentReferences)
    {
        if (string.IsNullOrWhiteSpace(documentId) || !documentReferences.Any()) return false;

        // Check if document ID matches any reference
        if (documentReferences.Any(reference => 
            documentId.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
            reference.Contains(documentId, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Check metadata if available
        if (!string.IsNullOrWhiteSpace(metadata))
        {
            return documentReferences.Any(reference =>
                metadata.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
                reference.Contains(metadata, StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }
}
