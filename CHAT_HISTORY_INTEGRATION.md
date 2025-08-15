# Chat History Integration - DriftMind

## Overview

DriftMind supports contextual conversations through chat history integration, enabling GPT-5 to maintain context across multiple interactions. This feature transforms simple document search into intelligent, conversational knowledge assistance.

## Key Features

### Contextual Conversations
- **Memory**: GPT-5 remembers previous questions and answers
- **Follow-up Questions**: Natural conversation flow with context awareness
- **Topic Continuity**: References to earlier discussion points
- **Intelligent Fallback**: Answers from history when no relevant documents found

### Smart Context Management
- **Token Optimization**: Automatic limitation to last 10-15 messages
- **Relevance Filtering**: Focuses on most recent and relevant conversation history
- **Graceful Degradation**: Works with or without chat history

## Data Models

### ChatMessage DTO
```csharp
public class ChatMessage
{
    public string Role { get; set; } = string.Empty;      // "user" or "assistant"
    public string Content { get; set; } = string.Empty;   // Message content
    public DateTime Timestamp { get; set; } = DateTime.UtcNow; // When sent
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
    public bool EnableQueryExpansion { get; set; } = true;
    public List<ChatMessage>? ChatHistory { get; set; } = null;  // NEW
}
```

## Usage Examples

### Basic Search (Without History)
```json
{
  "query": "What is Azure Active Directory?",
  "maxResults": 5,
  "useSemanticSearch": true,
  "includeAnswer": true
}
```

### Conversational Search (With History)
```json
{
  "query": "How do I configure that for my application?",
  "maxResults": 5,
  "includeAnswer": true,
  "chatHistory": [
    {
      "role": "user",
      "content": "What is Azure Active Directory?",
      "timestamp": "2025-08-15T10:00:00Z"
    },
    {
      "role": "assistant",
      "content": "Azure Active Directory (Azure AD) is Microsoft's cloud-based identity and access management service...",
      "timestamp": "2025-08-15T10:00:30Z"
    }
  ]
}
```

## Conversation Behavior

### 1. Document-Based Answers with Context
When both relevant documents and chat history are available:
- **Primary Source**: Uses found documents as the main information source
- **Context Enhancement**: Chat history provides additional context for interpretation
- **Reference Integration**: Establishes connections to previous conversation topics

### 2. History-Only Fallback
When no relevant documents are found but chat history exists:
- **Memory-Based Response**: GPT-5 attempts to answer based solely on conversation history
- **Clarification Support**: Useful for follow-up questions and clarifications
- **Conversation Continuity**: Maintains discussion flow even without new document matches

### 3. Token Management
- **Automatic Limiting**: System automatically limits to last 10-15 messages
- **Context Truncation**: Prevents token overflow while preserving recent context
- **Intelligent Pruning**: Removes older, less relevant conversation segments

## Real-World Examples

### Follow-Up Questions
```bash
# First question
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What authentication methods does Azure support?",
    "includeAnswer": true
  }'

# Follow-up with context
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Which one is most secure?",
    "includeAnswer": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "What authentication methods does Azure support?",
        "timestamp": "2025-08-15T10:00:00Z"
      },
      {
        "role": "assistant",
        "content": "Azure supports multiple authentication methods including managed identities, service principals, and certificate-based authentication...",
        "timestamp": "2025-08-15T10:00:30Z"
      }
    ]
  }'
```

### Clarification Requests
```bash
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Can you explain that in simpler terms?",
    "includeAnswer": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "How does OAuth 2.0 work with Azure AD?",
        "timestamp": "2025-08-15T10:00:00Z"
      },
      {
        "role": "assistant",
        "content": "OAuth 2.0 with Azure AD involves a complex flow of authorization codes, access tokens, and refresh tokens...",
        "timestamp": "2025-08-15T10:00:30Z"
      }
    ]
  }'
```

### Topic References
```bash
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What was the first topic we discussed?",
    "includeAnswer": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "Tell me about Azure Blob Storage",
        "timestamp": "2025-08-15T09:45:00Z"
      },
      {
        "role": "assistant",
        "content": "Azure Blob Storage is a service for storing large amounts of unstructured data...",
        "timestamp": "2025-08-15T09:45:30Z"
      },
      {
        "role": "user",
        "content": "How does it integrate with applications?",
        "timestamp": "2025-08-15T09:50:00Z"
      }
    ]
  }'
```

## Technical Implementation

### Service Integration
```csharp
// IChatService Interface Extensions
Task<string> GenerateAnswerWithHistoryAsync(
    string query, 
    List<SearchResult> searchResults, 
    List<ChatMessage>? chatHistory
);

// Enhanced processing methods
private async Task<string> GenerateAnswerFromHistoryOnlyAsync(
    string query, 
    List<ChatMessage> chatHistory
);

private string BuildEnhancedSystemPromptWithHistory();
private string BuildUserPromptWithContext(string query, string context);
```

### SearchOrchestrationService Integration
The `SearchOrchestrationService` automatically detects chat history availability and routes to appropriate processing methods:

1. **History Available + Documents Found**: Enhanced context generation
2. **History Available + No Documents**: History-only fallback
3. **No History**: Standard document-based response

## Configuration

Uses existing ChatService configuration:

```json
{
  "ChatService": {
    "MaxSourcesForAnswer": 10,
    "MinScoreForAnswer": 0.25,
    "MaxContextLength": 16000,
    "AdjacentChunksToInclude": 5
  }
}
```

## Response Format

```json
{
  "query": "How do I configure that?",
  "results": [
    {
      "id": "doc-123_5",
      "content": "To configure Azure authentication...",
      "score": 0.87,
      "documentId": "azure-guide"
    }
  ],
  "generatedAnswer": "Based on our previous discussion about Azure Active Directory and the documentation, here's how to configure authentication for your application:\n\n1. Register your application...\n\n*This builds on what we discussed earlier about Azure AD capabilities.*",
  "success": true,
  "totalResults": 3
}
```

## Benefits

### Enhanced User Experience
- **Natural Conversations**: Users can ask follow-up questions naturally
- **Context Awareness**: No need to repeat context in each query
- **Improved Relevance**: Answers consider full conversation context

### Better AI Performance
- **Contextual Understanding**: GPT-5 has broader context for interpretation
- **Intelligent Fallbacks**: Can answer even when documents aren't found
- **Conversation Flow**: Maintains logical discussion progression

### System Flexibility
- **Backward Compatible**: Works with existing API calls
- **Optional Feature**: Chat history is completely optional
- **Graceful Degradation**: Functions normally without history

## Compatibility & Migration

### Backward Compatibility
- ✅ **Existing API calls work unchanged**
- ✅ **`chatHistory` is optional and can be omitted**
- ✅ **No breaking changes in existing DTOs or interfaces**
- ✅ **Default behavior remains the same**

### Logging Enhancements
- Distinction between history-aware and standard operations
- Tracking of fallback scenarios (history-only responses)
- Detailed information about sources used vs. chat history
- Performance metrics for context processing

## Best Practices

### Chat History Management
1. **Limit History Size**: Include only last 10-15 messages to prevent token overflow
2. **Recent Context**: Focus on most recent and relevant conversation parts
3. **Clean Timestamps**: Ensure proper timestamp formatting for context ordering

### Query Optimization
1. **Natural Language**: Users can ask follow-up questions naturally
2. **Reference Previous Topics**: "What did you mean by X?" works well
3. **Build on Context**: Each question can build on previous answers

### Error Handling
1. **Graceful Fallback**: System works even if history processing fails
2. **Token Limits**: Automatic truncation prevents API errors
3. **Invalid History**: Malformed history is ignored, not breaking the request
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
