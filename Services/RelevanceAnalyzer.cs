namespace DriftMind.Services;

public static class RelevanceAnalyzer
{
    // Extended German stop words
    private static readonly string[] GermanStopWords = { 
        "der", "die", "das", "den", "dem", "des", "ein", "eine", "einer", "einem", "eines",
        "und", "oder", "aber", "doch", "sondern", "jedoch", "dennoch",
        "in", "auf", "mit", "von", "zu", "für", "über", "unter", "durch", "gegen", "ohne", "um", "vor", "seit", "bis", "während", "nach", "bei", "als", "wie",
        "ist", "sind", "war", "waren", "wird", "werden", "wurde", "wurden", "haben", "hat", "hatte", "hatten", "kann", "können", "konnte", "konnten", "soll", "sollte", "sollten", "muss", "müssen", "musste", "mussten",
        "ich", "du", "er", "sie", "es", "wir", "ihr", "sich", "mich", "dich", "uns", "euch",
        "wenn", "dass", "weil", "damit", "obwohl", "falls", "sofern",
        "nicht", "kein", "keine", "keiner", "keinem", "keines",
        "sehr", "mehr", "auch", "nur", "noch", "schon", "bereits", "immer", "oft", "manchmal"
    };
    
    // English stop words (extended)
    private static readonly string[] EnglishStopWords = { 
        "the", "and", "or", "but", "a", "an", "in", "on", "at", "to", "for", "of", "with", "by", "from", "up", "about", "into", "through", "during",
        "how", "can", "is", "are", "was", "were", "will", "would", "could", "should", "must", "have", "has", "had", "do", "does", "did", "get", "got",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them", "my", "your", "his", "their", "our",
        "if", "that", "because", "when", "where", "why", "what", "which", "who", "whose", "this", "these", "that", "those",
        "not", "no", "yes", "very", "more", "also", "only", "just", "already", "always", "often", "sometimes", "never", "here", "there", "now", "then"
    };

    // Combined synonyms for German and English terms
    private static readonly Dictionary<string, List<string>> MultiLanguageSynonyms = new()
    {
        // German terms with English equivalents
        { "betreiben", new List<string> { "verwenden", "nutzen", "einsetzen", "laufen", "abgelegt", "eingebunden", "hosten", "ausführen", "verwalten", "operate", "run", "host", "deploy", "manage" } },
        { "datenbank", new List<string> { "database", "db", "sqlite", "daten", "speicher", "datenspeicher", "data", "storage", "repository" } },
        { "azure", new List<string> { "microsoft", "cloud", "files", "webapp", "storage", "service", "platform", "infrastructure" } },
        { "konfigurieren", new List<string> { "einrichten", "setup", "configure", "eingebunden", "konfiguration", "einstellung", "config", "configuration", "setting", "install", "deploy" } },
        { "sqlite", new List<string> { "datenbank", "database", "db", "datei", "lokal", "file", "local", "embedded" } },
        { "files", new List<string> { "dateien", "datei", "storage", "speicher", "ablage", "file", "document", "blob", "share" } },
        { "volume", new List<string> { "laufwerk", "mount", "einbindung", "speicherplatz", "drive", "disk", "storage", "filesystem" } },
        { "option", new List<string> { "parameter", "einstellung", "konfiguration", "flag", "setting", "config", "argument", "switch" } },
        { "nobrl", new List<string> { "byte-range", "locking", "sperren", "lock", "unlock", "disable", "flag" } },
        
        // English terms with German equivalents
        { "operate", new List<string> { "betreiben", "run", "host", "deploy", "manage", "verwenden", "nutzen", "ausführen" } },
        { "database", new List<string> { "datenbank", "db", "sqlite", "data", "storage", "repository", "speicher", "datenspeicher" } },
        { "configure", new List<string> { "konfigurieren", "setup", "config", "setting", "install", "einrichten", "einstellung", "konfiguration" } },
        { "run", new List<string> { "betreiben", "operate", "execute", "host", "deploy", "laufen", "ausführen", "hosten" } },
        { "setup", new List<string> { "konfigurieren", "configure", "install", "deploy", "einrichten", "konfiguration", "einstellung" } },
        { "storage", new List<string> { "speicher", "files", "data", "repository", "ablage", "datenspeicher", "dateien" } },
        { "file", new List<string> { "datei", "document", "blob", "storage", "dateien" } },
        { "cloud", new List<string> { "azure", "microsoft", "platform", "service", "infrastructure", "online" } },
        { "mount", new List<string> { "einbinden", "volume", "drive", "attach", "connect", "laufwerk", "einbindung" } },
        { "deploy", new List<string> { "deployen", "install", "setup", "configure", "host", "einrichten", "installieren" } }
    };

    public static bool IsContentRelevant(string content, string query, double? score = null)
    {
        // If we have a high vector similarity score, trust it (reduced from 0.8 to 0.75)
        if (score.HasValue && score.Value > 0.75)
        {
            return true;
        }

        var queryTerms = ExtractMeaningfulTerms(query);
        var contentTerms = ExtractMeaningfulTerms(content);
        
        if (!queryTerms.Any())
        {
            return score.HasValue && score.Value > 0.4; // Reduced threshold
        }

        // Count exact matches, partial matches, and synonym matches
        var exactMatches = CountExactMatches(queryTerms, contentTerms);
        var partialMatches = CountPartialMatches(queryTerms, content);
        var synonymMatches = CountSynonymMatches(queryTerms, contentTerms);

        // Calculate relevance with enhanced scoring
        var totalRelevance = (exactMatches * 3.0) + (partialMatches * 2.0) + (synonymMatches * 1.5);
        var maxPossibleRelevance = queryTerms.Count * 3.0;
        var relevanceRatio = totalRelevance / maxPossibleRelevance;

        // Adaptive threshold based on content and query characteristics (more lenient)
        var threshold = CalculateAdaptiveThreshold(queryTerms.Count, content.Length, query.Length);
        
        return relevanceRatio >= threshold;
    }

    private static int CountExactMatches(List<string> queryTerms, List<string> contentTerms)
    {
        return queryTerms.Count(queryTerm => 
            contentTerms.Any(contentTerm => 
                string.Equals(queryTerm, contentTerm, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CountPartialMatches(List<string> queryTerms, string content)
    {
        var contentLower = content.ToLower();
        return queryTerms.Count(term => 
            contentLower.Contains(term.ToLower()) && 
            !contentLower.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Any(word => string.Equals(word, term, StringComparison.OrdinalIgnoreCase)));
    }

    private static int CountSynonymMatches(List<string> queryTerms, List<string> contentTerms)
    {
        int matches = 0;
        foreach (var queryTerm in queryTerms)
        {
            var queryLower = queryTerm.ToLower();
            
            // Check if query term has synonyms
            if (MultiLanguageSynonyms.ContainsKey(queryLower))
            {
                var synonyms = MultiLanguageSynonyms[queryLower];
                if (contentTerms.Any(contentTerm => 
                    synonyms.Any(synonym => 
                        string.Equals(synonym, contentTerm, StringComparison.OrdinalIgnoreCase))))
                {
                    matches++;
                }
            }
            
            // Check if content term has synonyms that match query
            foreach (var contentTerm in contentTerms)
            {
                var contentLower = contentTerm.ToLower();
                if (MultiLanguageSynonyms.ContainsKey(contentLower))
                {
                    var synonyms = MultiLanguageSynonyms[contentLower];
                    if (synonyms.Any(synonym => 
                        string.Equals(synonym, queryTerm, StringComparison.OrdinalIgnoreCase)))
                    {
                        matches++;
                        break;
                    }
                }
            }
        }
        return matches;
    }

    private static List<string> ExtractMeaningfulTerms(string text)
    {
        var allStopWords = GermanStopWords.Concat(EnglishStopWords).ToArray();
        
        return text.ToLower()
            .Split(new[] { ' ', ',', '.', '!', '?', ':', ';', '-', '(', ')', '[', ']', '\n', '\r' }, 
                   StringSplitOptions.RemoveEmptyEntries)
            .Where(term => term.Length > 2 && !allStopWords.Contains(term))
            .Distinct()
            .ToList();
    }

    private static double CalculateAdaptiveThreshold(int queryTermCount, int contentLength, int queryLength)
    {
        // More lenient thresholds for German content
        if (queryTermCount <= 2)
        {
            return 0.2; // Very lenient for short queries (was 0.3)
        }

        // For short content (< 200 chars), be more lenient
        if (contentLength < 200)
        {
            return 0.15; // Very lenient for short content (was 0.25)
        }

        // For longer German queries, still be lenient
        if (queryTermCount >= 6)
        {
            return 0.25; // More lenient for complex German queries (was 0.4)
        }

        return 0.2; // Generally more lenient default (was 0.35)
    }

    public static double CalculateRelevanceScore(string content, string query, double? vectorScore = null)
    {
        var queryTerms = ExtractMeaningfulTerms(query);
        var contentTerms = ExtractMeaningfulTerms(content);
        
        if (!queryTerms.Any())
        {
            return vectorScore ?? 0.0;
        }

        // Enhanced text relevance calculation with synonym support
        var exactMatches = CountExactMatches(queryTerms, contentTerms);
        var partialMatches = CountPartialMatches(queryTerms, content);
        var synonymMatches = CountSynonymMatches(queryTerms, contentTerms);
        
        // Calculate weighted text relevance
        var totalMatches = (exactMatches * 2.0) + (partialMatches * 1.0) + (synonymMatches * 1.5);
        var maxPossibleMatches = queryTerms.Count * 2.0;
        var textRelevance = Math.Min(1.0, totalMatches / maxPossibleMatches);

        // Combine vector score and text relevance
        if (vectorScore.HasValue)
        {
            return (vectorScore.Value * 0.7) + (textRelevance * 0.3);
        }

        return textRelevance;
    }
}
