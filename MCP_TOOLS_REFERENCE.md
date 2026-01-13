# MCP Tools Reference

## Overview

Your OpenDeepWiki MCP server provides tools to interact with repository documentation. Here's what each tool does and how to use them.

## Available Tools

### 1. ask_repository

**Purpose**: Ask questions about a repository and get detailed answers based on its code and documentation.

**Parameters**:
- `question` (string, **required**): The question you want to ask about the repository

**Example Request**:
```bash
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "tools/call",
    "params": {
      "name": "ask_repository",
      "arguments": {
        "question": "What is mcp-nano-banana and how does it work?"
      }
    }
  }'
```

**What it does**:
1. Checks if the warehouse has existing documentation
2. Uses the repository's code files and documentation structure
3. Provides detailed answers based on the codebase
4. Caches answers for 3 days (if the same question was asked before)

**Common Use Cases**:
- "What is this repository about?"
- "How does feature X work?"
- "What are the main components?"
- "How do I use this API?"

### 2. rag_search (Optional)

**Purpose**: Semantic search tool that retrieves relevant code or documentation snippets using vector search.

**Availability**: Only available if `ENABLE_MEM0=true` is set in your configuration.

**Parameters**:
- `query` (string, **required**): Detailed description of what you're looking for
- `limit` (int, optional, default: 5): Number of search results to return
- `minRelevance` (double, optional, default: 0.3): Minimum relevance threshold (0-1). Higher values (e.g., 0.7) return more precise matches.

**Example Request**:
```bash
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/call",
    "params": {
      "name": "rag_search",
      "arguments": {
        "query": "image generation function implementation",
        "limit": 5,
        "minRelevance": 0.5
      }
    }
  }'
```

**To Enable**:
1. Set environment variable: `ENABLE_MEM0=true`
2. Configure Mem0 API key and endpoint in your settings
3. Restart the server

## Common Issues

### Issue: "Unknown tool" error

**Solution**: Use the correct tool name `ask_repository`. The method name in the code is different from the tool name exposed via MCP.

### Issue: Parameter name error

**Solution**: 
- For `ask_repository`: use `question` (not `query`)
- For `rag_search`: use `query`

### Issue: "Your warehouse has no documentation"

**Solution**: The repository needs to have documentation generated first. Use the OpenDeepWiki web interface to generate documentation for the repository before using MCP tools.

## Quick Test

Test if your tools are working:

```bash
# 1. List available tools
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'

# 2. Ask a question using ask_repository
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/call",
    "params": {
      "name": "ask_repository",
      "arguments": {
        "question": "What is this repository about?"
      }
    }
  }'
```

