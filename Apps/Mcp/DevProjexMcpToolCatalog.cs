using System.Collections;
using System.Reflection;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpToolCatalog : IReadOnlyList<McpServerTool>
{
	private const string MaximumResultSizeKey = "anthropic/maxResultSizeChars";
	private readonly IReadOnlyList<McpServerTool> _tools;

	public DevProjexMcpToolCatalog(DevProjexMcpTools target, bool allowRemote, bool agentExclusions = false)
	{
		ArgumentNullException.ThrowIfNull(target);
		_tools =
		[
			Create(target, nameof(DevProjexMcpTools.ListProjects), "list_projects", "List projects", ListProjectsInput, ListProjectsOutput),
			Create(target, nameof(DevProjexMcpTools.GetTree), "get_tree", "Get project tree", GetTreeInput(agentExclusions), openWorld: allowRemote),
			Create(target, nameof(DevProjexMcpTools.Analyze), "analyze", "Analyze project", AnalyzeInput(agentExclusions), AnalyzeOutput, openWorld: allowRemote),
			Create(target, nameof(DevProjexMcpTools.PackContext), "pack_context", "Pack project context", PackContextInput(agentExclusions), largeResult: true, idempotent: false, openWorld: allowRemote),
			Create(target, nameof(DevProjexMcpTools.ReadPack), "read_pack", "Read context pack", ReadPackInput, largeResult: true),
			Create(target, nameof(DevProjexMcpTools.SearchProject), "search_project", "Search project", SearchInput(agentExclusions), openWorld: allowRemote),
			Create(target, nameof(DevProjexMcpTools.GetFile), "get_file", "Get project file", GetFileInput(agentExclusions), openWorld: allowRemote)
		];
	}

	public int Count => _tools.Count;
	public McpServerTool this[int index] => _tools[index];
	public int IndexOf(string name)
	{
		for (var index = 0; index < _tools.Count; index++)
		{
			if (_tools[index].ProtocolTool.Name.Equals(name, StringComparison.Ordinal))
				return index;
		}
		return int.MaxValue;
	}

	public IEnumerator<McpServerTool> GetEnumerator() => _tools.GetEnumerator();
	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

	private static McpServerTool Create(
		DevProjexMcpTools target,
		string methodName,
		string name,
		string title,
		string inputSchema,
		string? outputSchema = null,
		bool largeResult = false,
		bool idempotent = true,
		bool openWorld = false)
	{
		var method = typeof(DevProjexMcpTools).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.Public) ??
		             throw new MissingMethodException(typeof(DevProjexMcpTools).FullName, methodName);
		var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
		var options = new McpServerToolCreateOptions
		{
			Name = name,
			Title = title,
			Description = description,
			ReadOnly = true,
			Destructive = false,
			Idempotent = idempotent,
			OpenWorld = openWorld
		};
		if (outputSchema is not null)
		{
			options.UseStructuredContent = true;
			options.OutputSchema = ParseSchema(outputSchema);
		}
		var tool = McpServerTool.Create(method, target, options);
		tool.ProtocolTool.InputSchema = ParseSchema(inputSchema);
		if (largeResult)
		{
			tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
			{
				[MaximumResultSizeKey] = 200_000
			};
		}
		return tool;
	}

	private static JsonElement ParseSchema(string json)
	{
		using var document = JsonDocument.Parse(json);
		return document.RootElement.Clone();
	}

	private const string EmptyInput = """
	{
	  "type": "object",
	  "properties": {},
	  "additionalProperties": false
	}
	""";

	private const string ProjectProperty = """
	"project": {
	  "type": "string",
	  "description": "Absolute root path returned by list_projects, or a Git URL when the server allows remote sources. Optional only when one local root is configured."
	}
	""";

	private const string BranchProperty = """
	"branch": {
	  "type": "string",
	  "minLength": 1,
	  "description": "Optional Git branch for a remote project URL; invalid for local project paths."
	}
	""";

	private const string IncludeProperty = """
	"include_patterns": {
	  "type": "array",
	  "maxItems": 256,
	  "items": { "type": "string", "minLength": 1, "maxLength": 512 },
	  "description": "Project-relative glob patterns using '/' that only narrow the effective filters. A pattern matches the whole relative path: '*' and '?' stay inside one path segment, '**/' spans any depth ('**/*.cs' is every C# file, 'src/**' a subtree), '{a,b}' lists alternatives. Matching is case-sensitive on every platform; '!' negation and '[...]' classes are rejected."
	}
	""";

	private const string ExcludeProperty = """
	"exclude_patterns": {
	  "type": "array",
	  "maxItems": 256,
	  "items": { "type": "string", "minLength": 1, "maxLength": 512 },
	  "description": "Project-relative glob patterns using '/' that remove further paths; same syntax as include_patterns."
	}
	""";

	// Published only when the server was started with --allow-agent-exclusions. Content redaction
	// toggles are never part of this vocabulary; the enum is the shared exclusion catalog.
	private static string ExclusionsPropertyFragment()
	{
		var tokens = string.Join(
			", ",
			ProjectSelectionTokens.Exclusions.Select(static token => $"\"{token}\""));
		return $$"""
		,
		    "exclusions": {
		      "type": "array",
		      "maxItems": {{ProjectSelectionTokens.Exclusions.Count}},
		      "uniqueItems": true,
		      "items": { "type": "string", "enum": [{{tokens}}] },
		      "description": "Full desired set of built-in exclusion toggles. An empty array turns every toggle off (widest scan); omit the parameter to keep the server baseline — analyze echoes the effective set. Overrides the server baseline and any profile exclusions for this call. Tokens match case-insensitively; duplicates are rejected. hidden-* follow the platform hidden attribute; on Unix-like systems dot-named entries belong to the dot-* toggles."
		    }
		""";
	}

	private const string PathsProperty = """
	"paths": {
	  "type": "array",
	  "maxItems": 256,
	  "items": { "type": "string", "minLength": 1, "maxLength": 4096 },
	  "description": "Existing project-relative files or directories that narrow the selection."
	}
	""";

	private const string ProfileProperty = """
	"profile": {
	  "type": "string",
	  "minLength": 1,
	  "description": "Selection profile. 'standard' uses the desktop set of all eight exclusion toggles with gitignore and is stricter than the server default. 'local' uses the profile saved by the desktop app for this project and listed by list_projects.profiles. Otherwise use a portable profile JSON path inside the project root."
	}
	""";

	private const string DetailProperty = """
	"detail": {
	  "type": "string",
	  "enum": ["full", "compact", "signatures"],
	  "default": "full",
	  "description": "Collapse code to signatures or strip comments/blank lines to fit large projects into a budget; unsupported languages are returned unchanged."
	}
	""";

	private const string TrackedOnlyProperty = """
	"tracked_only": {
	  "description": "Restrict results to files tracked by Git; accepts a boolean or the string 'true' or 'false'.",
	  "default": false,
	  "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ]
	}
	""";

	private const string MaximumTokensProperty = """
	"max_tokens": {
	  "description": "Maximum estimated content tokens to include; accepts an integer or numeric string. Document structure is outside this budget.",
	  "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string GitScopeProperty = """
	"git_scope": {
	  "description": "Further restrict selected paths to staged files, all current changes (including untracked files), or files changed between two Git refs. This selects paths only; file content is always read from the current working tree.",
	  "maxLength": 4096,
	  "oneOf": [
	    { "type": "string", "enum": ["staged", "changes"] },
	    { "type": "string", "pattern": "^diff:(?!.*\\.\\.\\.)(?!.*\\.\\..*\\.\\.)[^\\s-]\\S*\\.\\.[^\\s-]\\S*$" }
	  ]
	}
	""";

	private const string TopFilesProperty = """
	"top_files": {
	  "description": "Number of largest text files to return by estimated tokens; default 10; integer or numeric string.",
	  "default": 10,
	  "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 1000 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string MaxFileBytesProperty = """
	"max_file_bytes": {
	  "description": "Exclude otherwise selected files strictly larger than this byte count; integer or numeric string.",
	  "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string TreeFormatProperty = """
	"format": {
	  "type": "string",
	  "enum": ["markdown", "text", "json", "xml"],
	  "default": "markdown",
	  "description": "Tree representation. Markdown is the compact default; text uses drawing characters, while JSON and XML are structured."
	}
	""";

	private static readonly string ListProjectsInput = EmptyInput;

	private static string GetTreeInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaxFileBytesProperty}},
	    "max_depth": {
	      "description": "Maximum tree depth from 0 to 1000; accepts an integer or numeric string.",
	      "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 1000 }, { "type": "string", "pattern": "^[0-9]+$" } ]
	    },
	    {{TreeFormatProperty}}
	  },
	  "additionalProperties": false
	}
	""";

	private static string AnalyzeInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{ProfileProperty}},
	    {{DetailProperty}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{TopFilesProperty}},
	    {{MaxFileBytesProperty}}
	  },
	  "additionalProperties": false
	}
	""";

	private static string PackContextInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{ProfileProperty}},
	    {{DetailProperty}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaximumTokensProperty}},
	    {{MaxFileBytesProperty}},
	    "view": { "type": "string", "enum": ["tree", "content", "tree-content"], "default": "tree-content", "description": "Choose whether the pack contains only the tree, only selected file content, or both." },
	    "format": { "type": "string", "enum": ["text", "markdown", "json", "xml"], "default": "markdown", "description": "Pack representation. Markdown is the readable default; text is plain human-readable output, while JSON and XML are structured machine-readable forms." }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string ReadPackInput = """
	{
	  "type": "object",
	  "properties": {
	    "pack_id": { "type": "string", "minLength": 1, "description": "Session-scoped id returned by pack_context." },
	    "start_line": { "description": "First 1-based line; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last 1-based line, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "required": ["pack_id"],
	  "additionalProperties": false
	}
	""";

	private static string SearchInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}},
	    "pattern": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "A .NET regular expression evaluated with a 2-second timeout after secrets are replaced with DEVPROJEX_REDACTED[<category>#<n>] placeholders." },
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaxFileBytesProperty}},
	    "context_lines": { "description": "Context lines from 0 to 20; default 2; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 20 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "ignore_case": { "description": "Case-insensitive matching; accepts a boolean or the string 'true' or 'false'.", "default": true, "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ] },
	    "max_results": { "description": "Maximum matches from 1 to 200; default 50; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 200 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "required": ["pattern"],
	  "additionalProperties": false
	}
	""";

	private static string GetFileInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    "path": { "type": "string", "minLength": 1, "description": "Existing file path inside the effective project selection. Markdown-escaped names copied from the default get_tree format are accepted ('\\_'-style ASCII punctuation); use get_tree with format=text to copy unescaped names." },
	    "start_line": { "description": "First 1-based line; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last 1-based line, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "required": ["path"],
	  "additionalProperties": false
	}
	""";

	private const string ListProjectsOutput = """
	{
	  "type": "object",
	  "properties": {
	    "projects": {
	      "type": "array",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string" },
	          "name": { "type": "string" },
	          "type": { "type": "string", "enum": ["git-repository", "local-folder"] }
	        },
	        "required": ["path", "name", "type"],
	        "additionalProperties": false
	      }
	    },
	    "profiles": {
	      "type": "array",
	      "items": {
	        "type": "object",
	        "properties": {
	          "project": { "type": "string" },
	          "name": { "type": "string" }
	        },
	        "required": ["project", "name"],
	        "additionalProperties": false
	      }
	    },
	    "baseline": {
	      "type": "object",
	      "description": "The selection baseline every call starts from unless it names a profile: the Git filtering mode, the active exclusion toggles, and whether calls may pass their own exclusions.",
	      "properties": {
	        "git": { "type": "string", "enum": ["none", "gitignore", "tracked"] },
	        "exclusions": { "type": "array", "items": { "type": "string" } },
	        "agentExclusions": { "type": "boolean" }
	      },
	      "required": ["git", "exclusions", "agentExclusions"],
	      "additionalProperties": false
	    }
	  },
	  "required": ["projects", "profiles", "baseline"],
	  "additionalProperties": false
	}
	""";

	private const string AnalyzeOutput = """
	{
	  "type": "object",
	  "properties": {
	    "files": { "type": "integer", "description": "Number of files in the effective selection." },
	    "characters": { "type": "integer", "description": "Rendered characters, including estimates for uninspected text files." },
	    "tokens": { "type": "integer", "description": "Estimated tokens for the same character total." },
	    "detail": { "type": "string", "enum": ["full", "compact", "signatures"], "description": "Effective content-detail level used for measurement." },
	    "exclusions": { "type": "array", "items": { "type": "string" }, "description": "Effective exclusion tokens for this call, in catalog order; the same tokens the mcp --exclude flag and the optional exclusions parameter use." },
	    "topFiles": {
	      "type": "array",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string", "description": "Project-relative file path." },
	          "tokens": { "type": "integer", "description": "Estimated tokens for this file." },
	          "uninspected": { "type": "boolean", "description": "True when bounded secret inspection could not read this file and its metrics are estimated." }
	        },
	        "required": ["path", "tokens"],
	        "additionalProperties": false
	      }
	    }
	  },
	  "required": ["files", "characters", "tokens", "detail", "exclusions", "topFiles"],
	  "additionalProperties": false
	}
	""";

}
