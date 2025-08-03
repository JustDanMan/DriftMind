# Chat History Integration - DriftMind

## Overview

The DriftMind API has been extended with the ability to consider chat history in search requests. This functionality enables contextual conversations where the AI remembers previous interactions and responds accordingly.

## New Functionality

### ChatMessage DTO

```csharp
public class ChatMessage
{
    public string Role { get; set; } = string.Empty; // "user" or "assistant"
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
```

### Extended SearchRequest

```csharp
public class SearchRequest
{
    public string Query { get; set; } = string.Empty;
    public int MaxResults { get; set; } = 10;
    public bool UseSemanticSearch { get; set; } = true;
    public string? DocumentId { get; set; }
    public bool IncludeAnswer { get; set; } = true;
    public List<ChatMessage>? ChatHistory { get; set; } = null; // NEW
}
```

## Usage

### Basic Search (as before)

```json
{
  "query": "What is Artificial Intelligence?",
  "maxResults": 5,
  "useSemanticSearch": true,
  "includeAnswer": true
}
```

### Search with Chat History

```json
{
  "query": "Can you tell me more about that?",
  "maxResults": 5,
  "useSemanticSearch": true,
  "includeAnswer": true,
  "chatHistory": [
    {
      "role": "user",
      "content": "What is Artificial Intelligence?",
      "timestamp": "2025-08-03T10:00:00Z"
    },
    {
      "role": "assistant",
      "content": "Artificial Intelligence (AI) is a field of computer science...",
      "timestamp": "2025-08-03T10:00:30Z"
    }
  ]
}
```

## Chat History Integration Behavior

### 1. **Document-based Answers with Context**
When both relevant documents and chat history are available:
- The AI uses found documents as the primary source
- Chat history is used for additional context
- References to previous conversations are established

### 2. **Fallback to Chat History**
When no relevant documents are found:
- The AI attempts to answer based only on chat history
- Useful for follow-up questions or clarifications

### 3. **Optimizations**
- **Token Management**: Automatic limitation to the last 10-15 messages
- **Contextual Prompts**: Special system prompts for chat history integration
- **Intelligent Fallbacks**: Graceful degradation when information is missing

## Technical Details

### New Service Methods

```csharp
// IChatService Interface
Task<string> GenerateAnswerWithHistoryAsync(
    string query, 
    List<SearchResult> searchResults, 
    List<ChatMessage>? chatHistory
);

// Private Helper Methods
private async Task<string> GenerateAnswerFromHistoryOnlyAsync(
    string query, 
    List<ChatMessage> chatHistory
);

private string BuildEnhancedSystemPromptWithHistory();
private string BuildUserPromptWithContext(string query, string context);
```

### SearchOrchestrationService Integration

The `SearchOrchestrationService` has been extended to automatically detect whether chat history is available and call the appropriate ChatService method accordingly.

## Use Case Examples

### 1. **Conversation Continuation**
```
User: "What is Machine Learning?"
Assistant: "Machine Learning is a subset of AI..."

User: "How does that work in practice?"
// With chat history, the context is understood
```

### 2. **Clarifications and Follow-up Questions**
```
User: "Explain Neural Networks to me"
Assistant: "Neural Networks are..."

User: "What did you mean by 'Backpropagation'?"
// Reference to previous answer possible
```

### 3. **Topic References**
```
User: "What was the first topic we discussed again?"
// Answer based on chat history if no documents found
```

## Configuration

The chat history functionality uses existing ChatService configurations:

```json
{
  "ChatService": {
    "MaxSourcesForAnswer": 10,
    "MinScoreForAnswer": 0.3,
    "MaxContextLength": 16000
  }
}
```

## Logging

Extended logging functionality for chat history operations:
- Distinction between history-aware and standard operations
- Tracking of fallback scenarios
- Detailed information about sources used vs. chat history

## Compatibility

The extension is fully backward compatible:
- Existing API calls work unchanged
- `chatHistory` is optional and can be omitted
- No breaking changes in existing DTOs or interfaces
