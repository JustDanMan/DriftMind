# DriftMind - Text Processing API

An ASP.NET Core Web API that splits text into chunks, creates embeddings, and stores them in Azure AI Search. Supports both direct text input and file uploads (.txt, .md, .pdf, .docx).

## Features

- **Text Chunking**: Intelligent splitting of texts into overlapping chunks
- **Embedding Generation**: Creation of vector representations using Azure OpenAI
- **Vector Search**: Storage and search in Azure AI Search
- **File Upload**: Support for .txt, .md, .pdf, and .docx files (max 3MB)
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
    "MaxFileSizeInMB": 3,
    "AllowedExtensions": [".txt", ".md", ".pdf", ".docx"]
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

Uploads text, splits it into chunks, and creates embeddings.

**Request Body:**
```json
{
  "text": "Your text here...",
  "documentId": "optional-document-id",
  "metadata": "Optional metadata",
  "chunkSize": 1000,
  "chunkOverlap": 200
}
```

**Response:**
```json
{
  "documentId": "generated-or-provided-id",
  "chunksCreated": 5,
  "success": true,
  "message": "Text successfully processed"
}
```

**Parameters:**
- `text` (required): The text to be processed
- `documentId` (optional): Unique ID for the document
- `metadata` (optional): Additional metadata
- `chunkSize` (optional, default: 1000): Maximum size of a chunk
- `chunkOverlap` (optional, default: 200): Overlap between chunks

### POST /upload/file

Uploads a file, extracts text, splits it into chunks, and creates embeddings.

**Request:** Multipart form data
- `file` (required): The file to upload (.txt, .md, .pdf, .docx)
- `documentId` (optional): Unique ID for the document
- `metadata` (optional): Additional metadata
- `chunkSize` (optional, default: 1000): Maximum size of a chunk
- `chunkOverlap` (optional, default: 200): Overlap between chunks

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
- **PDF files (.pdf)**: Portable Document Format files
- **Word documents (.docx)**: Microsoft Word documents

**File Size Limit:** 3MB (configurable in appsettings.json)

### POST /search

Searches documents semantically and generates answers with GPT-4o.

**Request Body:**
```json
{
  "query": "Your search query...",
  "maxResults": 10,
  "useSemanticSearch": true,
  "documentId": "optional-filter",
  "includeAnswer": true
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
  "generatedAnswer": "GPT-4o generated answer based on search results...",
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
curl -X POST "http://localhost:5175/upload/file" \
  -F "file=@path/to/your/document.pdf" \
  -F "documentId=my-doc-1" \
  -F "metadata=Important document" \
  -F "chunkSize=500" \
  -F "chunkOverlap=100"
```

### Example with curl (Upload Text):

```bash
curl -X POST "http://localhost:5175/upload" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Your example text here...",
    "chunkSize": 500,
    "chunkOverlap": 100
  }'
```

### Example with curl (Search):

```bash
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "What is Machine Learning?",
    "maxResults": 5,
    "useSemanticSearch": true,
    "includeAnswer": true
  }'
```

### Example with the HTTP file:

Use the provided `DriftMind.http` file with VS Code REST Client Extension.

## Development

### Project Structure
```
DriftMind/
├── Models/
│   ├── DocumentChunk.cs
│   └── FileUploadOptions.cs
├── DTOs/
│   ├── UploadDTOs.cs
│   └── SearchDTOs.cs
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
└── DriftMind.http
```

### Logging

The system uses structured logging. In the development environment, debug logs for services are enabled.

## Troubleshooting

### Common Issues:

1. **Index Creation Failed**: Check Azure Search configuration and permissions
2. **Embedding Generation Failed**: Check Azure OpenAI endpoint and deployment name
3. **Authentication Failed**: Check API keys and endpoints

### Check logs:
```bash
dotnet run --verbosity detailed
```

## License

MIT License