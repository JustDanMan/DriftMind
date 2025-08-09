# Query Expansion Feature - DriftMind

## Overview

The Query Expansion feature automatically enhances short, vague, or context-poor user queries to improve document search results. This two-stage approach first expands queries using AI, then performs enhanced search with the expanded terms.

## How It Works

1. **Query Analysis**: Determines if a query needs expansion based on:
   - Query length (configurable, default < 20 characters)
   - Word count (configurable, default ≤ 3 words)
   - Presence of vague language ("infos", "was ist", "tell me", etc.)

2. **AI-Powered Expansion**: Uses GPT-5 Chat to expand queries by:
   - Adding relevant synonyms and related terms
   - Incorporating context from chat history if available
   - Maintaining the original query intent
   - Generating 1-2 additional search terms

3. **Enhanced Search**: Performs document search using the expanded query for better results

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
- **`MaxQueryLengthToExpand`**: Queries shorter than this number of characters will be expanded (default: 20)
- **`MaxQueryWordsToExpand`**: Queries with fewer or equal words will be expanded (default: 3)

## API Usage

### SearchRequest Properties

```json
{
  "query": "Infos zu XY",
  "enableQueryExpansion": true,
  "chatHistory": [
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
- Uses existing GPT-5 Chat deployment (no additional costs)

## Testing

The feature can be tested using the `/search` endpoint with different query types:

1. Short vague queries (should expand)
2. Expansion disabled (should not expand)
3. German queries (should expand with German context)
4. Detailed queries (expansion depends on configuration)
5. Chat history context (should incorporate previous conversation)
