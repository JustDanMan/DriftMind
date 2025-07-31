# DriftMind - Text Processing API

Eine ASP.NET Core Web API, die Text in Chunks aufteilt, Embeddings erstellt und in Azure AI Search speichert.

## Features

- **Text Chunking**: Intelligente Aufteilung von Texten in überlappende Chunks
- **Embedding Generation**: Erstellung von Vektorrepräsentationen mit Azure OpenAI
- **Vector Search**: Speicherung und Durchsuchung in Azure AI Search
- **RESTful API**: Einfache HTTP-basierte Schnittstelle

## Voraussetzungen

- .NET 8.0 SDK
- Azure OpenAI Service (mit text-embedding-ada-002 Deployment)
- Azure AI Search Service

## Konfiguration

### 1. Azure OpenAI Setup
1. Erstellen Sie eine Azure OpenAI Resource
2. Deployen Sie das `text-embedding-ada-002` Modell
3. Notieren Sie sich Endpoint und API Key

### 2. Azure AI Search Setup
1. Erstellen Sie einen Azure AI Search Service
2. Notieren Sie sich Endpoint und API Key

### 3. Konfiguration der appsettings.json

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
  }
}
```

## Installation und Start

```bash
# Projekt klonen und Dependencies installieren
dotnet restore

# Anwendung starten
dotnet run
```

Die API ist dann verfügbar unter: `http://localhost:5175`

## API Endpoints

### POST /upload

Lädt Text hoch, teilt ihn in Chunks auf und erstellt Embeddings.

**Request Body:**
```json
{
  "text": "Ihr Text hier...",
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
  "message": "Text erfolgreich verarbeitet"
}
```

**Parameter:**
- `text` (erforderlich): Der zu verarbeitende Text
- `documentId` (optional): Eindeutige ID für das Dokument
- `metadata` (optional): Zusätzliche Metadaten
- `chunkSize` (optional, default: 1000): Maximale Größe eines Chunks
- `chunkOverlap` (optional, default: 200): Überlappung zwischen Chunks

### POST /search

Durchsucht die Dokumente semantisch und generiert Antworten mit GPT-4o.

**Request Body:**
```json
{
  "query": "Ihre Suchanfrage...",
  "maxResults": 10,
  "useSemanticSearch": true,
  "documentId": "optional-filter",
  "includeAnswer": true
}
```

**Response:**
```json
{
  "query": "Ihre Suchanfrage...",
  "results": [
    {
      "id": "document-id_0",
      "content": "Gefundener Text...",
      "documentId": "document-id",
      "chunkIndex": 0,
      "score": 0.85,
      "metadata": "Metadaten",
      "createdAt": "2025-07-31T10:00:00Z"
    }
  ],
  "generatedAnswer": "GPT-4o generierte Antwort basierend auf den Suchergebnissen...",
  "success": true,
  "totalResults": 5
}
```

**Parameter:**
- `query` (erforderlich): Die Suchanfrage
- `maxResults` (optional, default: 10, max: 50): Maximale Anzahl Ergebnisse
- `useSemanticSearch` (optional, default: true): Semantische Vektorsuche verwenden
- `documentId` (optional): Filter auf bestimmtes Dokument
- `includeAnswer` (optional, default: true): GPT-4o Antwort generieren

## Architektur

### Services

- **ITextChunkingService**: Intelligente Textaufteilung basierend auf Sätzen
- **IEmbeddingService**: Embedding-Generierung mit Azure OpenAI
- **ISearchService**: Azure AI Search Integration mit Vektor-Suche
- **IDocumentProcessingService**: Orchestrierung des gesamten Upload-Workflows
- **IChatService**: GPT-4o Integration für Antwortgenerierung
- **ISearchOrchestrationService**: Orchestrierung der Such- und Antwortprozesse

### Datenmodell

**DocumentChunk:**
- `Id`: Eindeutige Chunk-ID
- `Content`: Chunk-Inhalt
- `DocumentId`: Referenz zum ursprünglichen Dokument
- `ChunkIndex`: Position im Dokument
- `Embedding`: 1536-dimensionaler Vektor
- `CreatedAt`: Erstellungszeitpunkt
- `Metadata`: Zusätzliche Informationen

## Verwendung

### Beispiel mit curl (Upload):

```bash
curl -X POST "http://localhost:5175/upload" \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Ihr Beispieltext hier...",
    "chunkSize": 500,
    "chunkOverlap": 100
  }'
```

### Beispiel mit curl (Search):

```bash
curl -X POST "http://localhost:5175/search" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Was ist Machine Learning?",
    "maxResults": 5,
    "useSemanticSearch": true,
    "includeAnswer": true
  }'
```

### Beispiel mit der HTTP-Datei:

Verwenden Sie die bereitgestellte `DriftMind.http` Datei mit VS Code REST Client Extension.

## Entwicklung

### Projekt Structure
```
DriftMind/
├── Models/
│   └── DocumentChunk.cs
├── DTOs/
│   ├── UploadDTOs.cs
│   └── SearchDTOs.cs
├── Services/
│   ├── TextChunkingService.cs
│   ├── EmbeddingService.cs
│   ├── SearchService.cs
│   ├── DocumentProcessingService.cs
│   ├── ChatService.cs
│   └── SearchOrchestrationService.cs
├── Program.cs
├── appsettings.json
└── DriftMind.http
```

### Logging

Das System verwendet strukturiertes Logging. In der Development-Umgebung sind Debug-Logs für Services aktiviert.

## Troubleshooting

### Häufige Probleme:

1. **Index Creation Failed**: Überprüfen Sie die Azure Search Konfiguration und Berechtigungen
2. **Embedding Generation Failed**: Überprüfen Sie Azure OpenAI Endpoint und Deployment-Name
3. **Authentication Failed**: Überprüfen Sie API Keys und Endpoints

### Logs überprüfen:
```bash
dotnet run --verbosity detailed
```

## Lizenz

MIT License