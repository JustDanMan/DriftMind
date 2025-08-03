# DriftMind - Text Processing API

An ASP.NET Core Web API that extracts text from files, splits it into chunks, creates embeddings, and stores them in Azure AI Search. Supports file uploads (.txt, .md, .pdf, .docx).

## Features

- **Text Chunking**: Intelligent splitting of texts into overlapping chunks
- **Embedding Generation**: Creation of vector representations using Azure OpenAI
- **Vector Search**: Storage and search in Azure AI Search
- **File Upload**: Support for .txt, .md, .pdf, and .docx files (max 12MB)
- **Document Management**: Full CRUD operations for documents
- **Chat History Integration**: Contextual conversations with AI remembering previous interactions
- **RESTful API**: Simple HTTP-based interface

## Prerequisites

- .NET 8.0 SDK
- Azure OpenAI Service (with text-embedding-ada-002 deployment)
- Azure AI Search Service

## Configuration

### 1. Azure OpenAI Setup
1. Create an Azure OpenAI Resource
2. Deploy the `text-embedding-ada-002` model
3. Note down endpoint and API key

### 2. Azure AI Search Setup
1. Create an Azure AI Search Service
2. Note down endpoint and API key

### 3. Configure appsettings.json

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-openai-resource.openai.azure.com/",
    "ApiKey": "your-api-key",
    "EmbeddingDeploymentName": "text-embedding-ada-002",
    "ChatDeploymentName": "gpt-4o"
  },
  "AzureSearch": {
    "Endpoint": "https://your-search-service.search.windows.net",
    "ApiKey": "your-search-api-key"
  },
  "FileUpload": {
    "MaxFileSizeInMB": 12,
    "AllowedExtensions": [".txt", ".md", ".pdf", ".docx"]
  },
  "ChatService": {
    "MaxSourcesForAnswer": 10,
    "MinScoreForAnswer": 0.3,
    "MaxContextLength": 16000
  }
}
```

## Installation and Start

```bash
# Clone project and install dependencies
dotnet restore

# Start application
dotnet run
```

The API is then available at: `http://localhost:5175`

## API Endpoints

### POST /upload

Uploads a file, extracts text, splits it into chunks, and creates embeddings.

**Request:** Multipart form data
- `file` (required): The file to upload (.txt, .md, .pdf, .docx)
- `documentId` (optional): Unique ID for the document
- `metadata` (optional): Additional metadata
- `chunkSize` (optional, default: 300): Maximum size of a chunk
- `chunkOverlap` (optional, default: 20): Overlap between chunks

**Response:**
```json
{
  "documentId": "generated-or-provided-id",
  "chunksCreated": 5,
  "success": true,
  "message": "File 'document.pdf' successfully processed into 5 chunks and indexed.",
  "fileName": "document.pdf",
  "fileType": ".pdf",
  "fileSizeInBytes": 245760
}
```

**Supported File Types:**
- **Text files (.txt)**: Plain text files
- **Markdown files (.md)**: Markdown formatted files  
- **PDF files (.pdf)**: Portable Document Format files (text-based or with metadata extraction for image-based PDFs)
- **Word documents (.docx)**: Microsoft Word documents

**File Size Limit:** 12MB (configurable in appsettings.json)

### POST /search

Searches documents semantically and generates answers with GPT-4o.

**Request Body:**
```json
{
  "query": "Your search query...",
  "maxResults": 10,
  "useSemanticSearch": true,
  "documentId": "optional-filter",
  "includeAnswer": true,
  "chatHistory": [
    {
      "role": "user",
      "content": "Previous question...",
      "timestamp": "2025-08-03T10:00:00Z"
    },
    {
      "role": "assistant", 
      "content": "Previous answer...",
      "timestamp": "2025-08-03T10:00:30Z"
    }
  ]
}
```

**Response:**
```json
{
  "query": "Your search query...",
  "results": [
    {
      "id": "document-id_0",
      "content": "Found text...",
      "documentId": "document-id",
      "chunkIndex": 0,
      "score": 0.85,
      "metadata": "Metadata",
      "createdAt": "2025-07-31T10:00:00Z"
    }
  ],
  "generatedAnswer": "GPT-4o generated answer based on search results and chat history...",
  "success": true,
  "totalResults": 5
}
```

**Parameters:**
- `query` (required): The search query
- `maxResults` (optional, default: 10, max: 50): Maximum number of results
- `useSemanticSearch` (optional, default: true): Use semantic vector search
- `documentId` (optional): Filter to specific document
- `includeAnswer` (optional, default: true): Generate GPT-4o answer
- `chatHistory` (optional): Array of previous conversation messages for context

#### Chat History Integration

The search endpoint now supports optional chat history to enable contextual conversations:

**Chat History Behavior:**
1. **With Documents + History**: Uses documents as primary source, chat history for additional context
2. **No Documents + History**: Falls back to answering from chat history only
3. **Token Management**: Automatically limits to last 10-15 messages to prevent overflow
4. **Backward Compatible**: Existing requests without `chatHistory` work unchanged

**ChatMessage Format:**
```json
{
  "role": "user|assistant",
  "content": "Message content",
  "timestamp": "2025-08-03T10:00:00Z"
}
```

**Example Use Cases:**
- **Follow-up Questions**: "Can you tell me more about that?" (references previous conversation)
- **Clarifications**: "What did you mean by X?" (asks about previous AI response)
- **Topic References**: "What was the first topic we discussed?" (answered from history if no documents found)

**Example with Chat History:**
```bash
curl -X POST "http://localhost:5151/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "How does that work in practice?",
    "maxResults": 5,
    "includeAnswer": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "What is Machine Learning?",
        "timestamp": "2025-08-03T09:00:00Z"
      },
      {
        "role": "assistant", 
        "content": "Machine Learning is a subset of AI that enables computers to learn from data...",
        "timestamp": "2025-08-03T09:00:30Z"
      }
    ]
  }'
```

### POST /documents

Lists all documents stored in the database with their metadata and statistics.

**Request Body:**
```json
{
  "maxResults": 20,
  "skip": 0,
  "documentIdFilter": "optional-document-id"
}
```

**Response:**
```json
{
  "documents": [
    {
      "documentId": "document-1",
      "chunkCount": 5,
      "fileName": "example.pdf",
      "fileType": ".pdf",
      "fileSizeInBytes": 245760,
      "metadata": "File: example.pdf, Additional info",
      "createdAt": "2025-07-31T10:00:00Z",
      "lastUpdated": "2025-07-31T10:00:00Z",
      "sampleContent": [
        "This is the beginning of the document...",
        "The second paragraph contains...",
        "Additional content follows..."
      ]
    }
  ],
  "totalDocuments": 1,
  "returnedDocuments": 1,
  "success": true,
  "message": "Retrieved 1 documents successfully."
}
```

**Parameters:**
- `maxResults` (optional, default: 50, max: 100): Maximum number of documents to return
- `skip` (optional, default: 0): Number of documents to skip for pagination
- `documentIdFilter` (optional): Filter to show only a specific document

### GET /documents

Alternative GET endpoint to list documents using query parameters.

**Query Parameters:**
- `maxResults` (optional, default: 50, max: 100): Maximum number of documents
- `skip` (optional, default: 0): Number of documents to skip
- `documentId` (optional): Filter to specific document

### DELETE /documents/{documentId}

Deletes a document and all its associated chunks from the search index.

**URL Parameters:**
- `documentId` (required): The ID of the document to delete

**Response:**
```json
{
  "documentId": "document-1",
  "success": true,
  "chunksDeleted": 5,
  "message": "Document and 5 chunks successfully deleted"
}
```

### POST /documents/delete

Alternative endpoint to delete documents using a JSON request body.

**Request Body:**
```json
{
  "documentId": "document-1"
}
```

**Response:**
```json
{
  "documentId": "document-1",
  "success": true,
  "chunksDeleted": 5,
  "message": "Document and 5 chunks successfully deleted"
}
```

**Error Responses:**
- `400 Bad Request`: Invalid or missing document ID
- `404 Not Found`: Document does not exist
- `500 Internal Server Error`: Deletion failed due to system error

⚠️ **Warning**: Document deletion is permanent and cannot be undone.

## Secure Download System

The system includes a secure download mechanism that provides access to original files while preventing unauthorized downloads through direct URLs.

### POST /download/token

Generates a secure, time-limited download token for a document.

**Request Body:**
```json
{
  "documentId": "document-1",
  "expirationMinutes": 15
}
```

**Response:**
```json
{
  "token": "eyJkb2N1bWVudElkI...[encrypted-token]...ABC123",
  "documentId": "document-1",
  "expiresAt": "2025-08-02T17:00:00Z",
  "downloadUrl": "/download/file",
  "success": true
}
```

**Parameters:**
- `documentId` (required): The ID of the document to download
- `expirationMinutes` (optional, default: 15, max: 60): Token validity period

### POST /download/file

Downloads a file using a secure token provided in the request body.

**Request Body:**
```json
{
  "token": "eyJkb2N1bWVudElkI...[encrypted-token]...ABC123"
}
```

**Response:** File download with appropriate Content-Type and filename

**Error Responses:**
- `400 Bad Request`: Missing or invalid token
- `401 Unauthorized`: Invalid token signature or format
- `410 Gone`: Token has expired
- `500 Internal Server Error`: Download failed

### Security Features

#### Simple Token-Based Security
1. **No Direct File URLs**: Files are never accessible via direct URLs
2. **HMAC-SHA256 Signed Tokens**: Prevent token tampering and manipulation
3. **Time-Limited Access**: Tokens expire automatically (default: 15 minutes, max: 60 minutes)
4. **Document Validation**: Verifies document exists before allowing download
5. **Audit Logging**: All download attempts are logged for security monitoring

#### Token Security
- **Encryption**: HMAC-SHA256 signature prevents token manipulation
- **Short Expiration**: 15-minute default, 60-minute maximum
- **Document-Specific**: Each token is valid for only one document
- **Tamper-Proof**: Any modification invalidates the token

#### Simple Architecture
The system provides secure downloads without complex user authentication:
- **API-Level Security**: Access control through your application layer
- **Token-Based Downloads**: Temporary, secure download links
- **Frontend Flexibility**: Authentication can be handled in your frontend/application layer
- **Audit Trail**: Download activity logging for security monitoring

#### Example Download Flow
```bash
# Step 1: Generate download token
curl -X POST "http://localhost:8081/download/token" \
  -H "Content-Type: application/json" \
  -d '{"documentId": "doc-123", "expirationMinutes": 15}'

# Response: {"token": "eyJ...", "downloadUrl": "/download/file"}

# Step 2: Download file with token in request body (POST only)
curl -X POST "http://localhost:8081/download/file" \
  -H "Content-Type: application/json" \
  -d '{"token": "eyJ..."}' \
  --output downloaded-file.pdf

# Step 3: Token expires automatically after 15 minutes
```

#### Search Results with Download Links
Search results now include download information for available files:

```json
{
  "query": "Azure configuration",
  "results": [
    {
      "id": "doc-123_0",
      "content": "Azure configuration guide...",
      "documentId": "doc-123",
      "originalFileName": "azure-guide.pdf",
      "isFileAvailable": true,
      "download": {
        "documentId": "doc-123",
        "tokenEndpoint": "/download/token",
        "fileName": "azure-guide.pdf",
        "fileType": ".pdf",
        "tokenExpirationMinutes": 15
      }
    }
  ]
}
```

#### Configuration
Add to your `appsettings.json`:

```json
{
  "DownloadSecurity": {
    "TokenSecret": "CHANGE-THIS-IN-PRODUCTION-USE-STRONG-SECRET-KEY-32-CHARS-MIN",
    "DefaultExpirationMinutes": 15,
    "MaxExpirationMinutes": 60,
    "EnableAuditLogging": true
  }
}
```

⚠️ **Security Notice**: In production, ensure `TokenSecret` is a strong, unique key. Authentication and authorization should be handled at the application/frontend level.

## Architecture

### Services

- **ITextChunkingService**: Intelligent text splitting based on sentences
- **IEmbeddingService**: Embedding generation with Azure OpenAI
- **ISearchService**: Azure AI Search integration with vector search
- **IFileProcessingService**: File content extraction for multiple formats
- **IDocumentProcessingService**: Orchestration of the entire upload workflow (text and files)
- **IChatService**: GPT-4o integration for answer generation
- **ISearchOrchestrationService**: Orchestration of search and answer processes
- **IDocumentManagementService**: Document listing and metadata management

### Data Model

**DocumentChunk:**
- `Id`: Unique chunk ID
- `Content`: Chunk content
- `DocumentId`: Reference to original document
- `ChunkIndex`: Position in document
- `Embedding`: 1536-dimensional vector
- `CreatedAt`: Creation timestamp
- `Metadata`: Additional information

## Usage

### Example with curl (Upload File):

```bash
curl -X POST "http://localhost:5151/upload" \
  -F "file=@path/to/your/document.pdf" \
  -F "documentId=my-doc-1" \
  -F "metadata=Important document" \
  -F "chunkSize=300" \
  -F "chunkOverlap=20"
```

### Example with curl (Search):

```bash
curl -X POST "http://localhost:5151/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What is Machine Learning?",
    "maxResults": 5,
    "useSemanticSearch": true,
    "includeAnswer": true
  }'
```

### Example with curl (Search with Chat History):

```bash
curl -X POST "http://localhost:5151/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Can you explain that more simply?",
    "maxResults": 5,
    "useSemanticSearch": true,
    "includeAnswer": true,
    "chatHistory": [
      {
        "role": "user",
        "content": "What is Neural Networks?",
        "timestamp": "2025-08-03T09:00:00Z"
      },
      {
        "role": "assistant",
        "content": "Neural Networks are computational models inspired by biological neurons...",
        "timestamp": "2025-08-03T09:00:30Z"
      }
    ]
  }'
```

### Example with the HTTP file:

Use the provided `DriftMind.http` file with VS Code REST Client Extension for comprehensive API testing.
The file includes examples for:
- Basic file upload scenarios
- Standard search requests  
- Search with chat history integration
- Follow-up questions with context
- Fallback scenarios (history-only answers)
- Document management operations
- Download token generation and file downloads

## Development

### Project Structure
```
DriftMind/
├── Models/
│   ├── DocumentChunk.cs
│   └── FileUploadOptions.cs
├── DTOs/
│   ├── UploadDTOs.cs
│   ├── SearchDTOs.cs (includes ChatMessage)
│   ├── DocumentDTOs.cs
│   └── DownloadDTOs.cs
├── Services/
│   ├── TextChunkingService.cs
│   ├── EmbeddingService.cs
│   ├── SearchService.cs
│   ├── FileProcessingService.cs
│   ├── DocumentProcessingService.cs
│   ├── ChatService.cs
│   ├── SearchOrchestrationService.cs
│   └── DocumentManagementService.cs
├── Program.cs
├── appsettings.json
└── DriftMind.http (includes chat history examples)
```

### Logging

The system uses structured logging. In the development environment, debug logs for services are enabled.

## Search Quality & Relevance Filtering

DriftMind implements advanced relevance filtering and scoring to ensure high-quality search results, especially for short queries and diverse content types.

### Hybrid Search Architecture

The search system combines two complementary approaches:

1. **Vector Search**: Uses Azure OpenAI embeddings for semantic similarity
2. **Text Search**: Traditional full-text search for exact term matching
3. **Hybrid Scoring**: Combines both approaches with weighted relevance scores

### Relevance Scoring Algorithm

#### Combined Scoring Formula
```
Final Score = (Vector Score × 0.7) + (Text Relevance × 0.3)
```

#### Text Relevance Calculation
- **Exact Term Matches**: Direct word matches in content (weighted 3x)
- **Partial Matches**: Substring matches within words (weighted 2x)
- **Synonym Matches**: Multi-language synonym recognition (weighted 1.5x)
- **Meaningful Terms**: Filters out stop words and terms < 3 characters
- **Adaptive Thresholds**: Adjusts based on query and content characteristics

### Adaptive Filtering

The system applies different filtering strategies based on query characteristics:

#### Short Queries (< 15 characters, ≤ 2 terms)
- **Threshold**: Score > 0.2 OR Vector Score > 0.5
- **Multiplier**: 4x results for better filtering
- **Text Match Requirement**: 20% of query terms
- **Use Case**: Single words like "PDF", "Azure", "Python"

#### Medium Queries (< 50 characters, ≤ 5 terms)
- **Threshold**: IsRelevant OR Score > 0.3
- **Multiplier**: 3x results for filtering
- **Text Match Requirement**: 20% of query terms
- **Use Case**: "How to configure Azure Files"

#### Long Queries (≥ 50 characters, > 5 terms)
- **Threshold**: IsRelevant OR Score > 0.4
- **Multiplier**: 3x results for filtering
- **Text Match Requirement**: 25% of query terms
- **Use Case**: Complex technical questions

### Content-Aware Filtering

#### Short Content (< 200 characters)
- **Reduced Threshold**: 15% term match requirement
- **Purpose**: Ensures metadata, titles, and brief notes are found
- **Examples**: PDF metadata, file names, quick notes

#### Regular Content (≥ 200 characters)
- **Standard Threshold**: 20% term match requirement
- **Purpose**: Standard document chunks and paragraphs

### High-Confidence Scoring

#### Auto-Relevant Criteria
- **Vector Score > 0.75**: Automatically considered relevant (reduced from 0.8)
- **Purpose**: Trust high semantic similarity even without exact word matches
- **Use Case**: Synonyms, related concepts, multilingual content

### Answer Generation Quality

#### Source Filtering for GPT-4o
- **Minimum Score**: Configurable via `ChatService:MinScoreForAnswer` (default: 0.3)
- **Maximum Sources**: Configurable via `ChatService:MaxSourcesForAnswer` (default: 10 in development, 8 in production)
- **Source Diversification**: Maximum 1 chunk per document to ensure source variety (10 different documents instead of 10 chunks from 1 document)
- **Fallback Strategy**: Uses all IsRelevant=true results if none meet score threshold
- **Fallback Messages**: Clear communication when sources aren't relevant
- **Source Attribution**: Each answer includes source references with scores
- **Language**: Responses in German with proper source citations

#### Answer Quality Controls
```
"According to Source 1 (Score: 0.82, Document: azure-guide.pdf): ..."
```

#### Source Diversification Strategy

The system implements intelligent source diversification to maximize information variety:

```csharp
// Example: From 50 search results across 8 documents
// Before diversification: Could select 10 chunks all from Document A
// After diversification: Selects best chunk from each of 8 different documents

var diversifiedSources = searchResults
    .GroupBy(r => r.DocumentId)                           // Group by document
    .Select(g => g.OrderByDescending(r => r.Score).First()) // Best chunk per document
    .OrderByDescending(r => r.Score)                      // Order by relevance
    .Take(maxSources)                                     // Take top N documents
    .ToList();
```

**Benefits:**
- **Broader Coverage**: Information from multiple documents instead of deep diving into one
- **Balanced Perspective**: Prevents over-representation of a single source
- **Efficient Context Usage**: Complete files are loaded anyway, so chunk diversity is more valuable than chunk quantity
- **Better Answers**: GPT-4o receives varied perspectives from different sources

**Logging Example:**
```
info: Using 8 chunks from 8 different documents for answer generation
debug: Source distribution: doc-123...(1), pdf-456...(1), guide-789...(1)
```

### Performance Optimizations

#### Query Processing
- **Embedding Caching**: Reduces API calls for repeated queries
- **Batch Operations**: Efficient indexing and deletion
- **Adaptive Result Limits**: More results for complex queries

#### Memory Efficiency
- **Streaming Results**: Processes search results as they arrive
- **Diversified Source Limits**: Configurable maximum sources per answer (default: 10) with 1 chunk per document
- **Context Truncation**: Prevents token limit issues
- **Smart File Loading**: Complete files loaded for context, chunks used for source attribution

### Search Quality Metrics

#### Relevance Indicators
- **Vector Score**: Semantic similarity (0.0 - 1.0)
- **Text Score**: Term match percentage (0.0 - 1.0)
- **Combined Score**: Weighted final relevance (0.0 - 1.0)
- **IsRelevant**: Boolean relevance determination

#### Quality Thresholds
```json
{
  "vector_confidence": {
    "high": "> 0.75",
    "medium": "0.5 - 0.75", 
    "low": "< 0.5"
  },
  "text_relevance": {
    "high": "> 0.5",
    "medium": "0.2 - 0.5",
    "low": "< 0.2"
  }
}
```

### Multi-Language Support

#### Language-Aware Processing
- **German Stop Words**: Extended list (50+ terms) for German content
- **English Stop Words**: Extended list (50+ terms) for English content
- **Multi-Language Synonyms**: Cross-language synonym recognition (German ↔ English)
- **Semantic Embedding**: Language-agnostic vector representations
- **Mixed Content**: Handles documents with multiple languages

#### Cross-Language Synonym Examples
```csharp
// German to English synonyms
"betreiben" → ["operate", "run", "host", "deploy", "manage"]
"datenbank" → ["database", "storage", "repository"]
"konfigurieren" → ["configure", "setup", "install"]

// English to German synonyms  
"deploy" → ["betreiben", "einrichten", "installieren"]
"database" → ["datenbank", "speicher", "datenspeicher"]
"configure" → ["konfigurieren", "einrichten", "einstellung"]
```

#### Multi-Language Query Examples
```bash
# German query finding English content
curl -X POST "http://localhost:5151/search" \
  -d '{"query": "SQLite Datenbank betreiben", "maxResults": 3}'
# Finds: "How to operate SQLite database", "Deploy SQLite", etc.

# English query finding German content  
curl -X POST "http://localhost:5151/search" \
  -d '{"query": "deploy database Azure", "maxResults": 3}'
# Finds: "SQLite Datenbank auf Azure betreiben", etc.
```

### Example Search Flow

```
1. Query: "PDF" (Short query)
   ├── Generate embedding for "PDF"
   ├── Hybrid search with 4x multiplier (20 results)
   ├── Apply lenient filtering (Score > 0.2)
   ├── Return top relevant results
   └── Generate answer with best sources

2. Query: "How to configure Azure Files SMB?" (Medium query)
   ├── Extract meaningful terms: ["configure", "Azure", "Files", "SMB"]
   ├── Hybrid search with 3x multiplier
   ├── Apply lenient filtering (IsRelevant OR Score > 0.3)
   ├── Combine vector and text scores with synonym matching
   └── Return precise, relevant results

3. Query: "Wie kann ich eine SQLite Datenbank betreiben?" (Long German query)
   ├── Extract meaningful terms: ["sqlite", "datenbank", "betreiben"]
   ├── Apply multi-language synonyms: ["database", "operate", "run"]
   ├── Hybrid search with 3x multiplier
   ├── Apply adaptive filtering (IsRelevant OR Score > 0.4)
   └── Find relevant content across languages
```

### Search Configuration Parameters

#### Vector Search Configuration
```json
{
  "vectorSearch": {
    "algorithm": "HNSW",
    "metric": "Cosine",
    "parameters": {
      "m": 4,
      "efConstruction": 400,
      "efSearch": 500
    }
  }
}
```

#### Relevance Tuning Parameters
```csharp
// RelevanceAnalyzer Configuration (Updated)
public static class SearchConfig
{
    // Score thresholds (reduced for better recall)
    public const double HIGH_CONFIDENCE_THRESHOLD = 0.75; // Reduced from 0.8
    public const double MEDIUM_CONFIDENCE_THRESHOLD = 0.5;
    public const double ANSWER_GENERATION_THRESHOLD = 0.5;
    
    // Text relevance thresholds (more lenient)
    public const double SHORT_QUERY_THRESHOLD = 0.2;  // Reduced from 0.3
    public const double MEDIUM_QUERY_THRESHOLD = 0.2; // Reduced from 0.35
    public const double LONG_QUERY_THRESHOLD = 0.25;  // Reduced from 0.4
    public const double SHORT_CONTENT_THRESHOLD = 0.15; // Reduced from 0.25
    
    // Query categorization
    public const int SHORT_QUERY_LENGTH = 15;
    public const int MEDIUM_QUERY_LENGTH = 50;
    public const int SHORT_QUERY_TERMS = 2;
    public const int MEDIUM_QUERY_TERMS = 5;
    
    // Content categorization
    public const int SHORT_CONTENT_LENGTH = 200;
    
    // Search multipliers
    public const int SHORT_QUERY_MULTIPLIER = 4;
    public const int STANDARD_QUERY_MULTIPLIER = 3;
    
    // Answer generation
    public const int MAX_SOURCES_FOR_ANSWER = 5;
    
    // Score weights (enhanced with synonyms)
    public const double VECTOR_SCORE_WEIGHT = 0.7;
    public const double TEXT_SCORE_WEIGHT = 0.3;
    public const double EXACT_MATCH_WEIGHT = 3.0;     // Increased from 2.0
    public const double PARTIAL_MATCH_WEIGHT = 2.0;   // Increased from 1.0
    public const double SYNONYM_MATCH_WEIGHT = 1.5;   // New: synonym matching
}
```

#### Customizing Search Behavior

To modify search behavior, adjust parameters in the `RelevanceAnalyzer` class:

```csharp
// For more lenient filtering (finds more results) - CURRENT SETTINGS
private static double CalculateAdaptiveThreshold(...)
{
    if (queryTermCount <= 2) return 0.2; // More lenient for short queries
    if (contentLength < 200) return 0.15; // More lenient for short content
    return 0.25; // More lenient overall
}

// For stricter filtering (higher precision) - ALTERNATIVE
private static double CalculateAdaptiveThreshold(...)
{
    if (queryTermCount <= 2) return 0.4; // Stricter for short queries
    if (contentLength < 200) return 0.35; // Stricter for short content
    return 0.5; // Stricter overall
}
```

### Search API Response Format

#### Extended Search Result with Scoring
```json
{
  "query": "Azure Files configuration",
  "results": [
    {
      "id": "doc-123_0",
      "content": "To configure Azure Files, you need...",
      "documentId": "azure-guide-v2",
      "chunkIndex": 0,
      "score": 0.85,
      "vectorScore": 0.82,
      "metadata": "File: azure-setup.pdf",
      "createdAt": "2025-08-01T10:00:00Z",
      "isRelevant": true,
      "relevanceScore": 0.85
    }
  ],
  "generatedAnswer": "According to Source 1 (Score: 0.85): To configure Azure Files...",
  "success": true,
  "totalResults": 3
}
```

#### Score Interpretation Guide
- **Score 0.9-1.0**: Highly relevant, exact match
- **Score 0.7-0.9**: Very relevant, strong semantic match
- **Score 0.5-0.7**: Relevant, good semantic or text match
- **Score 0.3-0.5**: Potentially relevant, weak match
- **Score 0.0-0.3**: Low relevance, likely not useful

### Performance Monitoring

#### Search Quality Metrics
Monitor these metrics to assess search quality:

```bash
# In application logs, look for:
info: SearchOrchestrationService[0] 
      "Filtered 12 results to 3 relevant results for query: 'Azure Files'"

# Quality indicators:
# - High filter ratio (12→3) = good precision
# - Low filter ratio (5→4) = potential recall issues
# - Zero results after filtering = thresholds too strict
```

#### Debugging Search Issues

```bash
# Enable debug logging for detailed search flow
export ASPNETCORE_ENVIRONMENT=Development

# Check search orchestration logs
grep "SearchOrchestrationService" logs/app.log

# Check relevance analyzer decisions
grep "RelevanceAnalyzer" logs/app.log

# Monitor vector search performance
grep "vector search" logs/app.log
```

## Troubleshooting

### Common Issues:

1. **Index Creation Failed**: Check Azure Search configuration and permissions
2. **Embedding Generation Failed**: Check Azure OpenAI endpoint and deployment name
3. **Authentication Failed**: Check API keys and endpoints
4. **No Search Results Found**: See search-specific troubleshooting below
5. **Poor Search Quality**: Adjust relevance thresholds or check content quality

### Search-Specific Troubleshooting

#### Problem: No results for short queries (e.g., "PDF", "Azure")
**Solutions:**
- Check if `SHORT_QUERY_THRESHOLD = 0.3` is too strict
- Verify vector embeddings are being generated correctly
- Ensure content actually contains the search terms
- Check debug logs for filtering details

#### Problem: Too many irrelevant results
**Solutions:**
- Increase relevance thresholds in `RelevanceAnalyzer`
- Adjust `VECTOR_SCORE_WEIGHT` vs `TEXT_SCORE_WEIGHT` ratio
- Reduce search multipliers to get fewer initial results
- Enable stricter filtering for your content type

#### Problem: Relevant content not found
**Solutions:**
- Decrease relevance thresholds (make filtering more lenient)
- Check if stop words are filtering out important terms
- Verify embeddings capture semantic meaning correctly
- Review chunk size and overlap settings

#### Problem: Poor answer quality from GPT-4o
**Solutions:**
- Increase `ANSWER_GENERATION_THRESHOLD` to 0.7 for higher quality sources
- Reduce `MAX_SOURCES_FOR_ANSWER` to 3 for more focused context
- Check source attribution in generated answers
- Verify relevant sources are being passed to ChatService

#### Problem: Slow search performance
**Solutions:**
- Reduce search multipliers (3x instead of 4x for short queries)
- Implement result caching for common queries
- Optimize Azure Search index configuration
- Monitor embedding generation latency

### Debug Search Flow

To debug search issues, enable detailed logging and follow this process:

```bash
# 1. Check embedding generation
curl -X POST "http://localhost:5151/search" \
  -H "Content-Type: application/json" \
  -d '{"query": "test", "maxResults": 1, "includeAnswer": false}'

# 2. Look for these log entries:
# "Embedding generated for search query"
# "Search results received: X for query: 'test'"
# "Filtered X results to Y relevant results"

# 3. If no results, check:
grep "No text could be extracted" logs/app.log  # File processing issues
grep "Error in hybrid search" logs/app.log      # Search service issues
grep "Filtered.*to 0 relevant" logs/app.log     # Filtering too strict
```

### Search Quality Assessment

Use these queries to test search quality:

```bash
# Test 1: Short, specific terms
curl -X POST "http://localhost:5151/search" \
  -d '{"query": "PDF", "maxResults": 3}'

# Test 2: Medium complexity
curl -X POST "http://localhost:5151/search" \
  -d '{"query": "Azure Files configuration", "maxResults": 5}'

# Test 3: Long, complex query
curl -X POST "http://localhost:5151/search" \
  -d '{"query": "How do I configure SMB file shares in Azure Files service?", "maxResults": 3}'

# Expected behaviors:
# - Test 1: Should find documents mentioning PDF
# - Test 2: Should find Azure Files related content
# - Test 3: Should provide comprehensive, relevant answers
```

### Check logs:
```bash
dotnet run --verbosity detailed
```

## License

MIT License