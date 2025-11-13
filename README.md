# DriftMind - Text Processing API

**AI-powered document search and Q&A system**

Transform your documents into intelligent, searchable knowledge. Upload files, ask questions, and get contextual answers with source attribution.

[![Frontend — DriftMindWeb](https://img.shields.io/badge/Frontend-DriftMindWeb-informational?style=flat-square&logo=github&logoColor=white)](https://github.com/JustDanMan/DriftMindWeb)

## ✨ Features

- **🔍 Semantic Search**: Vector-based document discovery with Azure AI Search
- **🤖 AI Answers**: GPT-powered contextual responses from your documents  
- **📄 Multi-Format**: PDF, DOCX, TXT, and Markdown support
- **💬 Chat Memory**: Conversational AI with context awareness
- **⚡ Optimized**: 80-95% cost reduction through smart context windows
- **🔒 Secure**: Token-based file downloads with expiration
- **📊 Smart Queries**: AI-enhanced search with query expansion

## 🚀 Quick Start

### Prerequisites
- .NET 10.0 SDK
- Azure OpenAI Service (text-embedding-ada-002, gpt-5-chat)
- Azure AI Search Service  
- Azure Blob Storage

### Installation

```bash
# Clone and setup
git clone <repository-url>
cd DriftMind
dotnet restore

# Configure services (see Configuration section)
# Edit appsettings.json with your Azure credentials

# Run the application
dotnet run

# Access Swagger UI
open http://localhost:5175/swagger
```

## 🐳 Docker Images

Pre-built Docker images are available on GitHub Container Registry (GHCR):

- **Release builds**: Tagged with version numbers (`v0.0.24-alpha`) and `latest`
- **Security builds**: Weekly rebuilds every Monday to include upstream security fixes
- **Manual builds**: Available for critical security updates

```bash
docker pull ghcr.io/justdanman/driftmind:latest
```

## ⚙️ Configuration

### Azure Services Setup

1. **Azure OpenAI Service**
   - Deploy `text-embedding-ada-002` model
   - Deploy `gpt-5-chat` model
   - Note endpoint and API key

2. **Azure AI Search Service**  
   - Create service (Basic tier recommended)
   - Note endpoint and admin API key

3. **Azure Blob Storage**
   - Create storage account
   - Create `documents` container
   - Note connection string

### Application Settings

Update `appsettings.json`:

```json
{
  "AzureOpenAI": {
    "Endpoint": "https://your-openai.openai.azure.com/",
    "ApiKey": "your-api-key",
    "EmbeddingDeploymentName": "text-embedding-ada-002",
    "ChatDeploymentName": "gpt-5-chat"
  },
  "AzureSearch": {
    "Endpoint": "https://your-search.search.windows.net",
    "ApiKey": "your-search-api-key"
  },
  "AzureStorage": {
    "ConnectionString": "your-storage-connection-string",
    "ContainerName": "documents"
  }
}
```

## 📡 API Reference

### Upload Documents

**POST /upload**

Upload and process documents into searchable chunks.

```bash
curl -X POST "http://localhost:5175/upload" \
  -F "file=@document.pdf" \
  -F "documentId=my-doc" \
  -F "metadata=Documentation"
```

**Response:**
```json
{
  "documentId": "my-doc",
  "chunksCreated": 15,
  "success": true,
  "message": "File processed successfully"
}
```

### Search & AI Answers

**POST /search**

Search documents and generate AI-powered answers.

```json
{
  "query": "How do I configure authentication?",
  "maxResults": 10,
  "includeAnswer": true,
  "enableQueryExpansion": true,
  "chatHistory": [
    {
      "role": "user",
      "content": "What is Azure AD?",
      "timestamp": "2025-08-15T10:00:00Z"
    }
  ]
}
```

**Response:**
```json
{
  "query": "How do I configure authentication?",
  "expandedQuery": "configure setup authentication Azure Active Directory",
  "results": [
    {
      "id": "doc-123_5",
      "content": "To configure authentication...",
      "documentId": "doc-123",
      "chunkIndex": 5,
      "score": 0.87,
      "vectorScore": 0.85,
      "metadata": "File: auth-guide.pdf",
      "createdAt": "2025-08-15T10:00:00Z",
      "originalFileName": "auth-guide.pdf",
      "contentType": "application/pdf",
      "fileSizeBytes": 1048576,
      "blobPath": "documents/auth-guide.pdf"
    }
  ],
  "generatedAnswer": "Based on your documents, here's how to configure authentication...",
  "success": true,
  "totalResults": 1
}
```

### Document Management

**GET /documents**
List all documents using query parameters.

Query Parameters:
- `maxResults` (optional, 1-100, default: 50)
- `skip` (optional, default: 0) 
- `documentId` (optional filter)

**POST /documents**
List all documents using request body.

```json
{
  "maxResults": 20,
  "skip": 0,
  "documentIdFilter": "optional-filter"
}
```

**Response:**
```json
{
  "documents": [
    {
      "documentId": "doc-123",
      "chunkCount": 15,
      "fileName": "auth-guide.pdf",
      "fileType": ".pdf",
      "fileSizeBytes": 1048576,
      "metadata": "File: auth-guide.pdf",
      "createdAt": "2025-08-15T10:00:00Z",
      "lastUpdated": "2025-08-15T10:00:00Z",
      "sampleContent": [
        "This guide covers authentication...",
        "Chapter 1: Getting Started..."
      ]
    }
  ],
  "totalDocuments": 1,
  "returnedDocuments": 1,
  "success": true,
  "message": "Retrieved 1 documents successfully."
}
```

**DELETE /documents/{documentId}**
Delete document and all chunks.

**POST /documents/delete**
Alternative delete endpoint using JSON body:
```json
{
  "documentId": "doc-123"
}
```

### Secure Downloads

**POST /download/token**
Generate secure, time-limited download token.

```json
{
  "documentId": "doc-123",
  "expirationMinutes": 15
}
```

**Response:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "documentId": "doc-123",
  "expiresAt": "2025-08-15T10:15:00Z",
  "downloadUrl": "/download/file",
  "success": true
}
```

**POST /download/file**
Download file using secure token in request body.

```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."
}
```

Returns binary file download with appropriate headers.

### Administration

**POST /admin/migrate/optimize-metadata**
Optimize storage by consolidating metadata to first chunk only.

**POST /admin/migrate/fix-content-types**
Fix incorrect MIME types for existing documents.

## 🏗️ Architecture

### Core Components

```
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ File Upload │───▶│ Text Chunks │───▶│ Embeddings  │
└─────────────┘    └─────────────┘    └─────────────┘
       │                                      │
       ▼                                      ▼
┌─────────────┐    ┌─────────────┐    ┌─────────────┐
│ Blob Storage│    │ AI Search   │    │ Vector Store│
└─────────────┘    └─────────────┘    └─────────────┘
       │                   │                   │
       └───────────────────┼───────────────────┘
                           ▼
              ┌─────────────────────┐
              │ Search & AI Answers │
              └─────────────────────┘
```

### Key Services

- **DocumentProcessingService**: File upload and indexing
- **SearchService**: Vector and semantic search  
- **ChatService**: AI answer generation with context
- **QueryExpansionService**: Intelligent query enhancement
- **BlobStorageService**: File storage and retrieval
## 🧠 Advanced Features

### Context Optimization
Uses **adjacent chunks** strategy for 80-95% cost reduction:
- Smart context windows instead of full documents
- Maintains document flow and coherence  
- Linear scaling with document count

### AI Query Enhancement
- **Query Expansion**: Automatically enhances vague queries
- **Multi-Language**: German/English cross-language search
- **Chat Memory**: Contextual conversations with history
- **Smart Relevance**: Hybrid vector + text scoring

### Performance Optimizations
- **Metadata Efficiency**: 98% storage reduction through smart indexing
- **Embedding Cache**: 80-90% API call reduction
- **Batch Processing**: Optimized bulk operations
- **Linear Scaling**: Predictable cost growth

## 🛡️ Security

### Download Security
- **Token-based**: HMAC-SHA256 signed download tokens
- **Time-limited**: 15-minute default, 60-minute maximum
- **Audit logging**: All download activity tracked
- **No direct URLs**: Files never accessible via direct links

### Best Practices
- Use Azure Key Vault for secrets in production
- Enable managed identity for Azure services
- Configure proper CORS policies
- Implement rate limiting as needed

## 📚 Documentation

Additional detailed documentation:
- [Azure Blob Storage Integration](docs/AZURE_BLOB_STORAGE_INTEGRATION.md)
- [Chat History Integration](docs/CHAT_HISTORY_INTEGRATION.md)  
- [Query Expansion Feature](docs/QUERY_EXPANSION_FEATURE.md)
- [Adjacent Chunks Optimization](docs/ADJACENT_CHUNKS_OPTIMIZATION.md)
- [PDF/Word Integration](docs/PDF_WORD_GPT5_INTEGRATION.md)

## 📝 License

MIT License - see [LICENSE](LICENSE) file for details.

## 📦 Third-Party Packages

The following third-party packages are used in this project. Their respective licenses apply to those components. The overall project license remains MIT as noted above.

| Package | Version | License | Copyright |
|---------|---------|---------|-----------|
| Azure.AI.OpenAI | 2.1.0 | MIT | © Microsoft Corporation |
| Azure.Search.Documents | 11.7.0 | MIT | © Microsoft Corporation |
| Azure.Storage.Blobs | 12.26.0 | MIT | © Microsoft Corporation |
| DocumentFormat.OpenXml | 3.3.0 | MIT | © Microsoft Corporation |
| PdfPig | 0.1.11 | Apache-2.0 | © UglyToad / PdfPig Contributors |
| Microsoft.AspNetCore.OpenApi | 10.0.0 | MIT | © Microsoft Corporation |
| Swashbuckle.AspNetCore | 10.0.1 | MIT | © Swashbuckle Contributors |

Full license texts: see [THIRD-PARTY-NOTICES.md](./THIRD-PARTY-NOTICES.md).

---

Built with ❤️ for intelligent document search and AI-powered knowledge management.
