namespace DriftMind.Services;

public interface ITextChunkingService
{
    List<string> ChunkText(string text, int chunkSize = 1000, int overlap = 200);
}

public class TextChunkingService : ITextChunkingService
{
    public List<string> ChunkText(string text, int chunkSize = 1000, int overlap = 200)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var chunks = new List<string>();
        var sentences = SplitIntoSentences(text);
        
        var currentChunk = "";
        
        foreach (var sentence in sentences)
        {
            // Wenn das Hinzufügen des Satzes die Chunk-Größe überschreitet
            if (currentChunk.Length + sentence.Length > chunkSize && !string.IsNullOrEmpty(currentChunk))
            {
                chunks.Add(currentChunk.Trim());
                
                // Overlap: Behalte die letzten Wörter des vorherigen Chunks
                if (overlap > 0)
                {
                    var words = currentChunk.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var overlapWords = words.TakeLast(Math.Min(overlap / 10, words.Length)).ToArray();
                    currentChunk = string.Join(" ", overlapWords) + " ";
                }
                else
                {
                    currentChunk = "";
                }
            }
            
            currentChunk += sentence + " ";
        }
        
        // Füge den letzten Chunk hinzu, falls vorhanden
        if (!string.IsNullOrWhiteSpace(currentChunk))
        {
            chunks.Add(currentChunk.Trim());
        }
        
        return chunks;
    }
    
    private List<string> SplitIntoSentences(string text)
    {
        // Einfache Satzaufteilung basierend auf Satzzeichen
        var sentences = new List<string>();
        var currentSentence = "";
        
        for (int i = 0; i < text.Length; i++)
        {
            currentSentence += text[i];
            
            // Prüfe auf Satzende
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && 
                (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1])))
            {
                sentences.Add(currentSentence.Trim());
                currentSentence = "";
            }
        }
        
        // Füge den letzten Teil hinzu, falls kein Satzzeichen am Ende
        if (!string.IsNullOrWhiteSpace(currentSentence))
        {
            sentences.Add(currentSentence.Trim());
        }
        
        return sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }
}
