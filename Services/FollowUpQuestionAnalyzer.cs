namespace DriftMind.Services;

/// <summary>
/// Analyzer for detecting follow-up questions and related topic questions
/// </summary>
public static class FollowUpQuestionAnalyzer
{
    private static readonly string[] FollowUpPatterns = 
    {
        // German patterns
        "beispiel", "beispiele", "mehr über", "mehr dazu", "mehr infos", "mehr details", 
        "weitere informationen", "nachteile davon", "vorteile davon", "probleme dabei", 
        "schwierigkeiten", "andere aspekte", "zusätzlich", "außerdem", "darüber hinaus",
        "kannst du", "könntest du", "erklär mir", "sag mir mehr", "gib mir", "zeig mir",
        "was meinst du", "erkläre das", "genauer", "spezifischer", "details",
        
        // English patterns
        "example", "examples", "can you", "could you", "tell me more", "give me", "show me",
        "what do you mean", "explain that", "more about", "more details", "more info",
        "disadvantages", "advantages", "problems with", "issues with", "other aspects",
        "additionally", "furthermore", "more specific", "more precise", "elaborate"
    };

    private static readonly string[] QuestionWords = 
    { 
        // German
        "welche", "welcher", "welches", "was", "wie", "warum", "weshalb", "wo", "wann", "wer",
        // English
        "what", "how", "why", "where", "when", "who", "which"
    };

    /// <summary>
    /// Determines if the current query is a follow-up question based on typical patterns
    /// </summary>
    public static bool IsFollowUpQuestion(string query, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;

        var queryLower = query.ToLowerInvariant().Trim();
        
        logger?.LogInformation("🔍 FOLLOW-UP CHECK: Query='{Query}', Length={Length}, Words={WordCount}", 
            query, queryLower.Length, queryLower.Split(' ').Length);
        
        // Only very short queries are likely follow-ups
        if (queryLower.Length < 10 || queryLower.Split(' ').Length <= 2)
        {
            logger?.LogInformation("✅ FOLLOW-UP DETECTED: Short query - '{Query}'", query);
            return true;
        }

        // Check for question words at the beginning (standalone questions vs follow-ups)
        bool startsWithQuestionWord = QuestionWords.Any(qw => queryLower.StartsWith(qw + " "));
        
        // If it starts with a question word and is reasonably long, it's likely a new question
        if (startsWithQuestionWord && queryLower.Length > 20)
        {
            return false;
        }
        
        // Check for follow-up patterns
        var hasFollowUpPattern = FollowUpPatterns.Any(pattern => queryLower.Contains(pattern));
        
        if (hasFollowUpPattern)
        {
            var matchedPattern = FollowUpPatterns.First(pattern => queryLower.Contains(pattern));
            logger?.LogInformation("✅ FOLLOW-UP DETECTED: Pattern match '{Pattern}' in query '{Query}'", 
                matchedPattern, query);
        }
        else
        {
            logger?.LogInformation("❌ NO FOLLOW-UP: No pattern match in query '{Query}'", query);
        }
        
        return hasFollowUpPattern;
    }

    /// <summary>
    /// Calculates cosine similarity between two embedding vectors
    /// </summary>
    public static double CalculateCosineSimilarity(IReadOnlyList<float> vector1, IReadOnlyList<float> vector2)
    {
        if (vector1.Count != vector2.Count) return 0.0;

        double dotProduct = 0.0;
        double magnitude1 = 0.0;
        double magnitude2 = 0.0;

        for (int i = 0; i < vector1.Count; i++)
        {
            dotProduct += vector1[i] * vector2[i];
            magnitude1 += vector1[i] * vector1[i];
            magnitude2 += vector2[i] * vector2[i];
        }

        if (magnitude1 == 0.0 || magnitude2 == 0.0) return 0.0;

        return dotProduct / (Math.Sqrt(magnitude1) * Math.Sqrt(magnitude2));
    }

    /// <summary>
    /// Checks if two questions have similar structure patterns
    /// </summary>
    public static bool HasSimilarQuestionStructure(string query1, string query2)
    {
        var q1Lower = query1.ToLowerInvariant();
        var q2Lower = query2.ToLowerInvariant();
        
        // Check for similar question words
        var q1QuestionWords = QuestionWords.Where(w => q1Lower.Contains(w)).ToList();
        var q2QuestionWords = QuestionWords.Where(w => q2Lower.Contains(w)).ToList();
        
        if (q1QuestionWords.Any() && q2QuestionWords.Any() && q1QuestionWords.Intersect(q2QuestionWords).Any())
        {
            return true;
        }
        
        // Check for similar action words
        var actionWords = new[] { "empfiehlt", "vorgeschlagen", "definiert", "beschreibt", "erklärt", "zeigt" };
        var q1ActionWords = actionWords.Where(w => q1Lower.Contains(w)).ToList();
        var q2ActionWords = actionWords.Where(w => q2Lower.Contains(w)).ToList();
        
        return q1ActionWords.Any() && q2ActionWords.Any() && q1ActionWords.Intersect(q2ActionWords).Any();
    }
}
