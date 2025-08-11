namespace DriftMind.Services;

/// <summary>
/// Helper class for text processing and analysis in search operations
/// </summary>
public static class TextProcessingHelper
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // German stop words
        "der", "die", "und", "in", "den", "von", "zu", "das", "mit", "sich", "des", "auf", 
        "für", "ist", "im", "eine", "als", "auch", "dem", "wird", "an", "dass", "kann", 
        "sind", "nach", "nicht", "werden", "bei", "einer", "ein", "war", "hat", "ich", 
        "es", "sie", "haben", "er", "über", "so", "hier", "oder", "was", "aber", "mehr", 
        "aus", "wenn", "nur", "noch", "wie", "bis", "dann", "diese", "um", "vor", "durch", 
        "man", "sein", "soll", "etwa", "alle", "seine", "wo", "unter", "sehr", "zum", 
        "einem", "könnte", "ihren", "seiner", "zwei", "zwischen", "wieder", "diesem", 
        "hatte", "ihre", "eines", "gegen", "vom", "können", "weitere", "sollte", "seit", 
        "wurde", "während", "dazu", "bereits", "dabei",
        
        // English stop words
        "the", "is", "at", "which", "on", "and", "a", "to", "as", "are", "was", "will", 
        "an", "be", "or", "of", "with", "by", "from", "up", "about", "into", "through", 
        "during", "before", "after", "above", "below", "between", "among", "throughout", 
        "despite", "towards", "upon", "concerning", "within", "without", "again", "further", 
        "then", "once", "here", "there", "when", "where", "why", "how", "all", "any", 
        "both", "each", "few", "more", "most", "other", "some", "such", "no", "nor", 
        "not", "only", "own", "same", "so", "than", "too", "very", "can", "will", "just", 
        "should", "now"
    };

    private static readonly HashSet<string> FollowUpWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // German follow-up words
        "mehr", "weitere", "andere", "zusätzliche", "gehe", "bitte", "kannst", "könntest", 
        "würdest", "sagen", "erzählen", "erklären", "erläutern", "beschreiben", 
        
        // English follow-up words
        "more", "additional", "further", "other", "please", "could", "would", "tell", 
        "explain", "describe", "elaborate"
    };

    /// <summary>
    /// Checks if a word is a stop word that should be filtered out
    /// </summary>
    public static bool IsStopWord(string word) => StopWords.Contains(word);

    /// <summary>
    /// Checks if a word is a typical follow-up question word
    /// </summary>
    public static bool IsFollowUpWord(string word) => FollowUpWords.Contains(word);

    /// <summary>
    /// Extracts meaningful words from text, filtering out stop words
    /// </summary>
    public static List<string> ExtractMeaningfulWords(string text)
    {
        return text.ToLowerInvariant()
            .Split(new[] { ' ', '\n', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !IsStopWord(w))
            .ToList();
    }

    /// <summary>
    /// Extracts keywords from text with weights, filtering stop words and follow-up words
    /// </summary>
    public static List<string> ExtractKeywords(string text, int maxKeywords = 8)
    {
        return text.Split(new[] { ' ', '\n', '\t', '.', ',', ';', ':', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3 && !IsStopWord(w) && !IsFollowUpWord(w))
            .Take(maxKeywords)
            .Select(w => w.Trim())
            .ToList();
    }

    /// <summary>
    /// Checks if content contains any of the given keywords (case-insensitive)
    /// </summary>
    public static bool ContainsAnyKeyword(string content, IEnumerable<string> keywords)
    {
        var contentLower = content.ToLowerInvariant();
        return keywords.Any(keyword => contentLower.Contains(keyword.ToLowerInvariant()));
    }
}
