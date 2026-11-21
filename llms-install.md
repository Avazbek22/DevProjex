# Installing the DevProjex MCP server

DevProjex is a read-only MCP server over stdio. It needs one argument: the absolute path of the project folder the agent may read. No API keys and no environment variables are required.

## Requirement

Node.js with `npx` on `PATH`.

## Step 1. Ask for the project folder

Ask the user for the absolute path of the project folder. Use that path in place of `/absolute/path/to/project` below.

## Step 2. Add the server to the MCP settings

macOS and Linux:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "npx",
      "args": ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]
    }
  }
}
```

Windows (`npx` is a batch file, so it is started through `cmd`):

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "cmd",
      "args": ["/c", "npx", "-y", "devprojex", "mcp", "--root", "C:\\absolute\\path\\to\\project"]
    }
  }
}
```

Keep the existing servers in the settings file and add only the `devprojex` entry.

## Step 3. Verify

The server is connected when the client lists its tools. Call `get_tree` with no arguments: it returns the file tree of the project folder.

## More

Other install channels and every option: [Docs/McpServer.md](Docs/McpServer.md) and [Docs/Installation.md](Docs/Installation.md).
