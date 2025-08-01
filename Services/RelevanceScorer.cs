using System.Text.RegularExpressions;

namespace DriftMind.Services;

public static class RelevanceScorer
{
    public static double CalculateRelevanceScore(string content, string query, double? searchScore = null)
    {
        var relevanceScore = 0.0;
        
        // Base score from Azure Search (if available)
        if (searchScore.HasValue)
        {
            relevanceScore += Math.Min(searchScore.Value, 1.0) * 0.4; // 40% weight
        }
        
        // Term matching score
        var termScore = CalculateTermMatchingScore(content, query);
        relevanceScore += termScore * 0.4; // 40% weight
        
        // Semantic proximity score
        var proximityScore = CalculateProximityScore(content, query);
        relevanceScore += proximityScore * 0.2; // 20% weight
        
        return Math.Min(relevanceScore, 1.0);
    }
    
    public static bool IsRelevant(string content, string query, double? searchScore = null, double threshold = 0.3)
    {
        var relevanceScore = CalculateRelevanceScore(content, query, searchScore);
        return relevanceScore >= threshold;
    }
    
    private static double CalculateTermMatchingScore(string content, string query)
    {
        var queryTerms = ExtractTerms(query);
        if (!queryTerms.Any())
            return 0.0;
            
        var contentLower = content.ToLowerInvariant();
        var matchedTerms = 0;
        var totalWeight = 0.0;
        
        foreach (var term in queryTerms)
        {
            var weight = GetTermWeight(term);
            totalWeight += weight;
            
            // Exact match
            if (contentLower.Contains(term.ToLowerInvariant()))
            {
                matchedTerms++;
                continue;
            }
            
            // Partial match (for longer terms)
            if (term.Length > 4)
            {
                var partialMatch = FindPartialMatch(contentLower, term.ToLowerInvariant());
                if (partialMatch > 0.7)
                {
                    matchedTerms++;
                }
            }
        }
        
        return queryTerms.Count > 0 ? (double)matchedTerms / queryTerms.Count : 0.0;
    }
    
    private static double CalculateProximityScore(string content, string query)
    {
        var queryTerms = ExtractTerms(query);
        if (queryTerms.Count < 2)
            return 0.0;
            
        var contentWords = ExtractTerms(content);
        var proximityScore = 0.0;
        var comparisons = 0;
        
        for (int i = 0; i < queryTerms.Count - 1; i++)
        {
            for (int j = i + 1; j < queryTerms.Count; j++)
            {
                var term1 = queryTerms[i].ToLowerInvariant();
                var term2 = queryTerms[j].ToLowerInvariant();
                
                var pos1 = FindTermPosition(contentWords, term1);
                var pos2 = FindTermPosition(contentWords, term2);
                
                if (pos1 >= 0 && pos2 >= 0)
                {
                    var distance = Math.Abs(pos1 - pos2);
                    // Closer terms get higher scores
                    var score = distance <= 5 ? 1.0 : Math.Max(0.0, 1.0 - (distance - 5) / 20.0);
                    proximityScore += score;
                }
                comparisons++;
            }
        }
        
        return comparisons > 0 ? proximityScore / comparisons : 0.0;
    }
    
    private static List<string> ExtractTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();
            
        // Remove punctuation and split by whitespace
        var cleaned = Regex.Replace(text, @"[^\w\s]", " ");
        return cleaned.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length > 2) // Ignore very short words
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
    
    private static double GetTermWeight(string term)
    {
        // Longer terms and technical terms get higher weight
        if (term.Length > 8) return 1.5;
        if (term.Length > 5) return 1.2;
        
        // Common stop words get lower weight
        var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "or", "but", "in", "on", "at", "to", "for", "of", "with", "by",
            "der", "die", "das", "und", "oder", "aber", "in", "an", "zu", "für", "von", "mit"
        };
        
        return stopWords.Contains(term) ? 0.5 : 1.0;
    }
    
    private static double FindPartialMatch(string content, string term)
    {
        if (term.Length < 4) return 0.0;
        
        var bestMatch = 0.0;
        var words = content.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var word in words)
        {
            if (word.Length < 3) continue;
            
            var similarity = CalculateStringSimilarity(word, term);
            if (similarity > bestMatch)
            {
                bestMatch = similarity;
            }
        }
        
        return bestMatch;
    }
    
    private static int FindTermPosition(List<string> words, string term)
    {
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].Equals(term, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }
    
    private static double CalculateStringSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2))
            return 0.0;
            
        var longer = s1.Length > s2.Length ? s1 : s2;
        var shorter = s1.Length > s2.Length ? s2 : s1;
        
        if (longer.Length == 0)
            return 1.0;
            
        var editDistance = CalculateEditDistance(longer, shorter);
        return (longer.Length - editDistance) / (double)longer.Length;
    }
    
    private static int CalculateEditDistance(string s1, string s2)
    {
        var matrix = new int[s1.Length + 1, s2.Length + 1];
        
        for (int i = 0; i <= s1.Length; i++)
            matrix[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++)
            matrix[0, j] = j;
            
        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }
        
        return matrix[s1.Length, s2.Length];
    }
}
