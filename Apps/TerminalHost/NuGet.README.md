# DevProjex headless CLI

DevProjex turns a folder or repository into focused context for AI. This package
contains the CLI, interactive terminal workspace, and local read-only MCP server;
it does not contain the desktop application.

Requires the .NET SDK 10.0.100 or later.

```shell
dnx devprojex tree .
dnx devprojex analyze . --compress-code
dnx devprojex mcp --root .
```

See the [installation guide](https://github.com/Avazbek22/DevProjex/blob/v5.2/Docs/Installation.md)
for supported platforms and desktop installation options.
