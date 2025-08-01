namespace DriftMind.Services;

public interface ITextChunkingService
{
    List<string> ChunkText(string text, int chunkSize = 300, int overlap = 20);
}

public class TextChunkingService : ITextChunkingService
{
    public List<string> ChunkText(string text, int chunkSize = 300, int overlap = 20)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var chunks = new List<string>();
        var sentences = SplitIntoSentences(text);
        
        var currentChunk = "";
        
        foreach (var sentence in sentences)
        {
            // If adding the sentence exceeds the chunk size
            if (currentChunk.Length + sentence.Length > chunkSize && !string.IsNullOrEmpty(currentChunk))
            {
                chunks.Add(currentChunk.Trim());
                
                // Overlap: Keep the last words of the previous chunk
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
        
        // Add the last chunk if present
        if (!string.IsNullOrWhiteSpace(currentChunk))
        {
            chunks.Add(currentChunk.Trim());
        }
        
        return chunks;
    }
    
    private List<string> SplitIntoSentences(string text)
    {
        // Simple sentence splitting based on punctuation
        var sentences = new List<string>();
        var currentSentence = "";
        
        for (int i = 0; i < text.Length; i++)
        {
            currentSentence += text[i];
            
            // Check for end of sentence
            if ((text[i] == '.' || text[i] == '!' || text[i] == '?') && 
                (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1])))
            {
                sentences.Add(currentSentence.Trim());
                currentSentence = "";
            }
        }
        
        // Add the last part if no punctuation at the end
        if (!string.IsNullOrWhiteSpace(currentSentence))
        {
            sentences.Add(currentSentence.Trim());
        }
        
        return sentences.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
    }
}
