# Query Expansion Feature - DriftMind

## Overview

The Query Expansion feature automatically enhances short, vague, or context-poor user queries to improve document search results. This AI-powered approach expands queries intelligently to find more relevant content while maintaining the original intent.

## How It Works

### 1. Query Analysis
The system determines if a query needs expansion based on:
- **Query length**: Configurable threshold (default: < 20 characters)
- **Word count**: Configurable threshold (default: ≤ 3 words)
- **Vague language detection**: Identifies non-specific terms and phrases

### 2. AI-Powered Expansion
Uses GPT-5 to expand queries by:
- Adding relevant synonyms and related terms
- Incorporating context from chat history if available
- Maintaining the original query intent
- Generating 1-3 additional search terms

### 3. Enhanced Search
Performs document search using the expanded query for better results.

## Configuration

Add to `appsettings.json`:

```json
{
  "QueryExpansion": {
    "EnabledByDefault": true,
    "MaxQueryLengthToExpand": 20,
    "MaxQueryWordsToExpand": 3
  }
}
```

### Configuration Parameters

- **`EnabledByDefault`**: Whether query expansion is enabled by default (can be overridden per request)
- **`MaxQueryLengthToExpand`**: Queries shorter than this will be expanded (default: 20)
- **`MaxQueryWordsToExpand`**: Queries with fewer or equal words will be expanded (default: 3)

## API Usage

### SearchRequest Properties

```json
{
  "query": "Azure info",
  "enableQueryExpansion": true,
  "chatHistory": [
    {
      "role": "user",
      "content": "I need help with Azure configuration",
      "timestamp": "2025-08-15T10:00:00Z"
    }
  ]
}
```

### SearchResponse Properties

```json
{
  "query": "Azure info",
  "expandedQuery": "Azure information configuration setup details documentation",
  "results": [...],
  "success": true
}
```

## Examples

### Simple Query Expansion

**Before:**
- Input: "PDF"
- Search: Limited results due to vague query

**After:**
- Input: "PDF"
- Expanded: "PDF document file format processing extraction"
- Search: More comprehensive and relevant results

### Context-Aware Expansion

**Chat History:**
```
User: "How do I configure Azure authentication?"
Assistant: "Azure authentication can be configured using Azure Active Directory..."
```

**Follow-up Query:**
- Input: "What about permissions?"
- Expanded: "Azure Active Directory permissions roles access control configuration"

### Multi-Language Support

**German Query:**
- Input: "Infos zu Azure"
- Expanded: "Informationen Details Konfiguration Setup Azure Dokumentation"

**English Query:**
- Input: "Azure infos"
- Expanded: "Azure information configuration setup documentation details"

## Language Detection

The system supports expansion in multiple languages:

### German Vague Terms
- "infos", "was ist", "kannst du", "wie geht", "erzähl mir"

### English Vague Terms
- "tell me", "what about", "anything about", "info on", "help with"

## Performance Considerations

- **Latency**: Adds ~500-1000ms per query (AI processing time)
- **Efficiency**: Only applied to queries that need expansion
- **Cost**: Uses existing GPT-4 deployment (minimal additional cost)
- **Caching**: Results can be cached for common expansion patterns

## Testing Examples

```bash
# Test 1: Short vague query (should expand)
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "PDF",
    "enableQueryExpansion": true,
    "maxResults": 5
  }'

# Test 2: Expansion disabled (should not expand)
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "PDF",
    "enableQueryExpansion": false,
    "maxResults": 5
  }'

# Test 3: Context-aware expansion with chat history
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "How does that work?",
    "enableQueryExpansion": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "What is Azure Active Directory?",
        "timestamp": "2025-08-15T10:00:00Z"
      },
      {
        "role": "assistant",
        "content": "Azure Active Directory is a cloud identity service...",
        "timestamp": "2025-08-15T10:00:30Z"
      }
    ]
  }'
```

## Benefits

### Improved Search Quality
- **Better Recall**: Finds more relevant documents through enhanced terms
- **Semantic Understanding**: AI identifies intent behind vague queries
- **Context Awareness**: Uses conversation history for relevant expansion

### User Experience
- **No Additional Effort**: Works automatically with existing queries
- **Maintains Intent**: Preserves original query meaning
- **Faster Results**: Users don't need to refine queries manually

### System Intelligence
- **Language Agnostic**: Works with German and English queries
- **Adaptive**: Learns from chat context for better expansion
- **Configurable**: Adjustable thresholds for different use cases

## Integration Notes

- **Backward Compatible**: Existing API calls work unchanged
- **Optional Feature**: Can be disabled per request or globally
- **Performance Impact**: Minimal overhead for queries that don't need expansion
- **Logging**: Detailed logs show original vs. expanded queries for debugging
    {
      "role": "user",
      "content": "Previous conversation context",
      "timestamp": "2025-08-07T10:00:00Z"
    }
  ]
}
```

### SearchResponse Properties

```json
{
  "query": "Infos zu XY",
  "expandedQuery": "Informationen Details Eigenschaften Merkmale XY",
  "results": [...],
  "success": true
}
```

## Examples

### Before Query Expansion
**User Query**: "Infos zu XY"
**Search Results**: Poor, as the query is too vague

### After Query Expansion
**Original Query**: "Infos zu XY"
**Expanded Query**: "Informationen Details Eigenschaften Merkmale Anwendungsmöglichkeiten XY"
**Search Results**: Much better, as the query contains specific search terms

## Chat History Integration

When chat history is provided, the expansion considers previous conversation context:

**Chat History**: 
- User: "Ich habe Fragen zur Dokumentenverarbeitung"
- Assistant: "Diese umfasst PDF-Extraktion, Text-Chunking..."

**User Query**: "Was meinst du damit?"
**Expanded Query**: "Erklärung Definition Details Dokumentenverarbeitung PDF-Extraktion Text-Chunking"

## Implementation Details

### New Services
- `IQueryExpansionService`: Main interface for query expansion
- `QueryExpansionService`: Implementation with AI-powered expansion

### Enhanced Services
- `SearchOrchestrationService`: Integrated with query expansion
- `SearchRequest/SearchResponse`: Extended DTOs with expansion support

### Detection Logic
The system detects queries that need expansion using:
- Length-based rules (configurable)
- Word count analysis (configurable) 
- Vague language detection (predefined phrases)

### Language Support
- **German**: "infos", "was ist", "kannst du", etc.
- **English**: "tell me", "what about", "anything about", etc.

## Performance Considerations

- Expansion adds ~500-1000ms per query (AI call)
- Only applied to queries that need it (smart detection)
- Can be disabled per request if needed
- Uses existing GPT-5 deployment (no additional costs)

## Testing

The feature can be tested using the `/search` endpoint with different query types:

1. Short vague queries (should expand)
2. Expansion disabled (should not expand)
3. German queries (should expand with German context)
4. Detailed queries (expansion depends on configuration)
5. Chat history context (should incorporate previous conversation)
