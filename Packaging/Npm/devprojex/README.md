# DevProjex

DevProjex turns a folder or repository into focused context for AI. This package
contains the CLI, interactive terminal workspace, and local read-only MCP server;
it does not contain the desktop application.

Requires Node.js 20 or later. The matching self-contained binary is installed as an
optional dependency for Windows, Linux (glibc), and macOS on x64 and arm64.

```shell
npx -y devprojex tree .
npx -y devprojex analyze . --compress-code
npx -y devprojex mcp --root .
```

See the [installation guide](https://github.com/Avazbek22/DevProjex/blob/v5.2/Docs/Installation.md)
for supported platforms and desktop installation options.
