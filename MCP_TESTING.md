# Testing MCP Server with MCP Inspector

## Overview

Your mcp-nano-banana MCP server uses **HTTP transport** (not stdio), which means the standard MCP Inspector has limitations. This guide provides multiple ways to test your MCP server.

## Prerequisites

1. **Start your mcp-nano-banana server** (if not already running):
   ```bash
   # Using docker-compose
   docker-compose up -d
   
   # Or using the start script
   ./start.sh
   ```

2. **Verify the server is running**:
   - The server should be accessible at `http://localhost:8080`
   - MCP endpoint: `http://localhost:8080/api/mcp?owner=<owner>&name=<name>`
   - SSE endpoint: `http://localhost:8080/api/mcp/sse?owner=<owner>&name=<name>`

3. **Ensure you have a warehouse configured**:
   - You need a warehouse with `owner` and `name` parameters
   - Example: `owner=guilhermeaumo&name=mcp-nano-banana`

## Method 1: Using MCP Inspector (Limited Support)

The standard MCP Inspector (`@modelcontextprotocol/inspector`) is designed for stdio-based servers. However, you can try using it with a wrapper:

### Option A: Using MCP Inspector with HTTP Bridge

1. **Install MCP Inspector**:
   ```bash
   npm install -g @modelcontextprotocol/inspector
   ```

2. **Create a test script** (see `test-mcp-http.js` below) that bridges HTTP to stdio

3. **Run the inspector**:
   ```bash
   npx @modelcontextprotocol/inspector node test-mcp-http.js
   ```

### Option B: Direct HTTP Testing (Recommended)

Since your server uses HTTP, direct HTTP testing is more appropriate.

## Method 2: Using curl to Test MCP Endpoints

### Test 1: Initialize MCP Session

```bash
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "capabilities": {},
      "clientInfo": {
        "name": "test-client",
        "version": "1.0.0"
      }
    }
  }'
```

### Test 2: List Available Tools

```bash
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 2,
    "method": "tools/list"
  }'
```

### Test 3: Call ask_repository Tool

Ask questions about the repository and get detailed answers based on its code and documentation.

```bash
curl -X POST "http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 3,
    "method": "tools/call",
    "params": {
      "name": "ask_repository",
      "arguments": {
        "question": "What is mcp-nano-banana?"
      }
    }
  }'
```

**Important**: The parameter name is `question`, not `query`!

## Method 3: Using a Custom Test Script

See `test-mcp-server.js` for a comprehensive Node.js test script.

## Method 4: Using Postman or Similar Tools

1. **Create a new request**:
   - Method: POST
   - URL: `http://localhost:8080/api/mcp?owner=guilhermeaumo&name=mcp-nano-banana`
   - Headers: `Content-Type: application/json`

2. **Test Initialize**:
   ```json
   {
     "jsonrpc": "2.0",
     "id": 1,
     "method": "initialize",
     "params": {
       "protocolVersion": "2024-11-05",
       "capabilities": {},
       "clientInfo": {
         "name": "postman-client",
         "version": "1.0.0"
       }
     }
   }
   ```

3. **Test Tools/List**:
   ```json
   {
     "jsonrpc": "2.0",
     "id": 2,
     "method": "tools/list"
   }
   ```

## Method 5: Using Python Script

See `test_mcp_server.py` for a Python-based test script.

## Testing SSE Endpoint

For SSE (Server-Sent Events) endpoint testing:

```bash
curl -N "http://localhost:8080/api/mcp/sse?owner=guilhermeaumo&name=mcp-nano-banana" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
      "protocolVersion": "2024-11-05",
      "capabilities": {},
      "clientInfo": {
        "name": "curl-client",
        "version": "1.0.0"
      }
    }
  }'
```

## Available Tools

### ask_repository

**What it does**: Ask questions about the repository and get detailed answers based on its code and documentation.

**Parameters**:
- `question` (string, required): The question you want to ask about the repository

**Example**:
```json
{
  "name": "ask_repository",
  "arguments": {
    "question": "How does the image generation work in this project?"
  }
}
```

### rag_search (Optional - Requires Mem0)

**What it does**: Semantic search tool that retrieves relevant code or documentation from the indexed repository using vector search.

**Availability**: Only available if `EnableMem0` is enabled in your configuration.

**Parameters**:
- `query` (string, required): Detailed description of what you're looking for
- `limit` (int, optional, default: 5): Number of results to return
- `minRelevance` (double, optional, default: 0.3): Minimum relevance threshold (0-1)

**To enable**: Set `ENABLE_MEM0=true` in your environment variables or system settings.

## Troubleshooting

1. **404 Not Found**: 
   - Check that the warehouse exists in the database
   - Verify `owner` and `name` parameters match exactly (case-insensitive)

2. **Connection Refused**:
   - Ensure the server is running on port 8080
   - Check firewall settings

3. **Invalid Response**:
   - Verify the JSON-RPC format is correct
   - Check server logs for errors

4. **Unknown tool error**:
   - Make sure you're using the correct tool name: `ask_repository`
   - Use `question` parameter, not `query` for ask_repository

## Next Steps

- Test all available tools
- Verify tool responses
- Test error handling
- Test concurrent requests

