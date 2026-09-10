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
			Create(target, nameof(DevProjexMcpTools.RelatedFiles), "related_files", "Find related files", RelatedFilesInput(agentExclusions), largeResult: true, idempotent: false, openWorld: allowRemote),
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
	  "description": "Unique project name or absolute path returned by list_projects, or a Git URL when the server allows remote sources. Optional only when one local root is configured."
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
	  "description": "Existing project-relative files or directories that narrow the selection. Values are literal paths: *, ?, {, and [ are ordinary filename characters here, not glob syntax."
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
	  "description": "Content detail: full keeps text, compact strips comments and blank lines, signatures keeps code signatures where supported; unsupported languages remain unchanged."
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
	  "description": "Maximum estimated content tokens admitted by the greedy file pass; accepts an integer or numeric string. Document structure and the budget report are outside this content budget. All token figures use a character heuristic, not a tokenizer.",
	  "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string RankProperty = """
	"rank": {
	  "type": "string",
	  "enum": ["importance"],
	  "description": "Ranking mode; the only value is importance. It orders the effective selection by importance-v1, controls greedy admission with max_tokens, and otherwise controls document order. Omit it to preserve ordinary order and avoid dependency or Git-history work."
	}
	""";

	private const string FocusProperty = """
	"focus": {
	  "description": "One selected path or an array of 1..16 selected paths that seed focus-v1 ordering. Requires rank=importance. Seeds are considered first; graph hops order the remaining effective selection without widening it.",
	  "oneOf": [
	    { "type": "string", "minLength": 1, "maxLength": 4096 },
	    { "type": "array", "minItems": 1, "maxItems": 16, "items": { "type": "string", "minLength": 1, "maxLength": 4096 } }
	  ]
	}
	""";

	private const string GitScopeProperty = """
	"git_scope": {
	  "description": "Git path scope: staged, changes (including untracked files), or diff:<ref>..<ref>. It only narrows selected paths; content always comes from the current working tree.",
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
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaxFileBytesProperty}},
	    "max_depth": {
	      "description": "Maximum tree depth from 0 to 1000, counted in levels below the project root and never below a paths entry: 0 returns the root alone, 1 adds its direct children, and a file inside src/router needs 3. Omit it to let a large tree pick the deepest complete depth that fits. Accepts an integer or numeric string.",
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
	    {{RankProperty}},
	    {{FocusProperty}},
	    {{MaximumTokensProperty}},
	    {{MaxFileBytesProperty}},
	    "view": { "type": "string", "enum": ["tree", "content", "tree-content"], "default": "tree-content", "description": "Pack view: tree includes structure only, content includes files only, tree-content includes both." },
	    "format": { "type": "string", "enum": ["text", "markdown", "json", "xml"], "default": "markdown", "description": "Pack format: markdown or text for readable output; json or xml for structured output." }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string ReadPackInput = """
	{
	  "type": "object",
	  "properties": {
	    "pack_id": { "type": "string", "minLength": 1, "description": "Session-scoped id returned by pack_context or related_files." },
	    "start_line": { "description": "First 1-based line of the returned text after replacements; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last 1-based line of the returned text after replacements, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "start_column": { "description": "First 1-based Unicode character within start_line; use the continuation value returned for a long line.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
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
	    "pattern": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "A .NET regular expression, limited to 4,096 characters and a 2-second evaluation timeout, applied after redaction. It is matched against file content only and never against file names or paths; use get_tree with include_patterns to find files by name. Text inserted by redaction never matches." },
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaxFileBytesProperty}},
	    "context_lines": { "description": "Lines before and after each match, 0..20, default 2; overlapping windows are merged. Accepts an integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 20 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "ignore_case": { "description": "Case-insensitive matching; accepts a boolean or the string 'true' or 'false'.", "default": true, "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ] },
	    "max_results": { "description": "Maximum displayed matching lines, 1..200, default 50; all selected text is still scanned so additional matches are counted. Accepts an integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 200 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
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
	    {{BranchProperty}},
	    {{ProfileProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    "path": { "type": "string", "minLength": 1, "description": "Single-file form. Exactly one of path or requests is required; supplying both is rejected before file access. The path must name an existing file inside the effective project selection. Markdown-escaped names copied from the default get_tree format are accepted ('\\_'-style ASCII punctuation); use get_tree with format=text to copy unescaped names." },
	    "requests": {
	      "type": "array",
	      "minItems": 1,
	      "maxItems": 8,
	      "description": "Batch form: up to eight file requests and sixteen ranges total. Exactly one of requests or path is required; supplying both is rejected before file access. Single-file range arguments cannot be combined with requests.",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "Existing file path inside the effective project selection." },
	          "ranges": {
	            "type": "array",
	            "minItems": 1,
	            "maxItems": 16,
	            "description": "Inclusive transformed-text line ranges requested for this file.",
	            "items": {
	              "type": "object",
	              "properties": {
	                "start_line": { "description": "First 1-based transformed-text line, inclusive.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	                "end_line": { "description": "Last 1-based transformed-text line, inclusive.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	              },
	              "required": ["start_line", "end_line"],
	              "additionalProperties": false
	            }
	          }
	        },
	        "required": ["path", "ranges"],
	        "additionalProperties": false
	      }
	    },
	    "start_line": { "description": "First 1-based line of the returned text after replacements; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last 1-based line of the returned text after replacements, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "start_column": { "description": "First 1-based Unicode character within start_line; use the continuation value returned for a long line.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "additionalProperties": false
	}
	""";

	private static string RelatedFilesInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{BranchProperty}},
	    "path": {
	      "description": "One seed path, or up to 16 seed paths, inside the effective project selection.",
	      "oneOf": [
	        { "type": "string", "minLength": 1, "maxLength": 4096 },
	        { "type": "array", "minItems": 1, "maxItems": 16, "items": { "type": "string", "minLength": 1, "maxLength": 4096 } }
	      ]
	    },
	    "direction": { "type": "string", "enum": ["dependencies", "dependents", "both"], "default": "both", "description": "Static relationship direction: dependencies are files the seed references, dependents are files that reference the seed, both returns both sections." },
	    {{IncludeProperty}},
	    {{ExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{ProfileProperty}},
	    {{TrackedOnlyProperty}},
	    {{GitScopeProperty}},
	    {{MaxFileBytesProperty}}
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
	      "description": "Local roots available to project tools.",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string", "description": "Absolute value accepted by the project parameter." },
	          "name": { "type": "string", "description": "Display name derived from the root." },
	          "type": { "type": "string", "enum": ["git-repository", "local-folder"], "description": "Detected local root kind." }
	        },
	        "required": ["path", "name", "type"],
	        "additionalProperties": false
	      }
	    },
	    "profiles": {
	      "type": "array",
	      "description": "Saved local profiles available for listed roots.",
	      "items": {
	        "type": "object",
	        "properties": {
	          "project": { "type": "string", "description": "Root path that owns the profile." },
	          "name": { "type": "string", "description": "Profile token accepted by selection tools." }
	        },
	        "required": ["project", "name"],
	        "additionalProperties": false
	      }
	    },
	    "profilesStatus": { "type": "string", "enum": ["available", "unavailable"], "description": "Whether the single bounded profile-catalog read succeeded." },
	    "baseline": {
	      "type": "object",
	      "description": "The server-wide selection, protection, and remote-source policy active for this session.",
	      "properties": {
	        "git": { "type": "string", "enum": ["none", "gitignore", "tracked"], "description": "Server Git filtering mode." },
	        "exclusions": { "type": "array", "items": { "type": "string" }, "description": "Server exclusion tokens in catalog order." },
	        "agentExclusions": { "type": "boolean", "description": "Whether calls may replace the server exclusion set." },
	        "protection": {
	          "type": "object",
	          "description": "Content-protection policy enforced by every project tool.",
	          "properties": {
	            "secrets": { "type": "string", "const": "always", "description": "Mandatory secret-redaction state." },
	            "privateData": { "type": "string", "enum": ["enabled", "disabled"], "description": "Server-startup private-data redaction state." }
	          },
	          "required": ["secrets", "privateData"],
	          "additionalProperties": false
	        },
	        "remote": {
	          "type": "object",
	          "description": "Remote Git acquisition policy for this server.",
	          "properties": {
	            "enabled": { "type": "boolean", "description": "Whether remote Git sources are enabled." },
	            "hosts": { "type": "array", "items": { "type": "string" }, "description": "Exact allowed hosts; empty means unrestricted when remote sources are enabled." }
	          },
	          "required": ["enabled", "hosts"],
	          "additionalProperties": false
	        }
	      },
	      "required": ["git", "exclusions", "agentExclusions", "protection", "remote"],
	      "additionalProperties": false
	    }
	  },
	  "required": ["projects", "profiles", "profilesStatus", "baseline"],
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
	    "contentMetrics": {
	      "type": "object",
	      "description": "Content-only metrics split between inspected transformed text and size-based estimates; no root or file headings are counted.",
	      "properties": {
	        "measured": {
	          "type": "object",
	          "properties": {
	            "files": { "type": "integer", "description": "Number of files measured from inspected transformed text." },
	            "lines": { "type": "integer", "description": "Lines in measured transformed file bodies." },
	            "characters": { "type": "integer", "description": "Normalized characters in measured transformed file bodies." },
	            "tokens": { "type": "integer", "description": "Estimated tokens for measured transformed file bodies." }
	          },
	          "required": ["files", "lines", "characters", "tokens"],
	          "additionalProperties": false
	        },
	        "estimated": {
	          "type": "object",
	          "properties": {
	            "files": { "type": "integer", "description": "Number of text files represented only by size-based estimates." },
	            "characters": { "type": "integer", "description": "Estimated normalized characters for those files." },
	            "tokens": { "type": "integer", "description": "Estimated tokens for those files." }
	          },
	          "required": ["files", "characters", "tokens"],
	          "additionalProperties": false
	        }
	      },
	      "required": ["measured", "estimated"],
	      "additionalProperties": false
	    },
	    "documentMetrics": {
	      "type": "object",
	      "description": "Metrics for the canonical pack_context content/text document, including its root and relative-path headings.",
	      "properties": {
	        "view": { "type": "string", "const": "content", "description": "Pack view used for document measurement." },
	        "format": { "type": "string", "const": "text", "description": "Pack format used for document measurement." },
	        "lines": { "type": "integer", "description": "Rendered document lines." },
	        "characters": { "type": "integer", "description": "Rendered normalized document characters." },
	        "tokens": { "type": "integer", "description": "Estimated tokens for the rendered document." },
	        "estimated": { "type": "boolean", "description": "True when one or more file bodies use size-based estimates or lack text metrics." }
	      },
	      "required": ["view", "format", "lines", "characters", "tokens", "estimated"],
	      "additionalProperties": false
	    },
	    "exclusions": { "type": "array", "items": { "type": "string" }, "description": "Effective exclusion tokens for this call, in catalog order; the same tokens the mcp --exclude flag and the optional exclusions parameter use." },
	    "compressionUnavailable": {
	      "type": "object",
	      "description": "Present when requested code compression could not load all required grammars and content was left complete.",
	      "properties": {
	        "reason": { "type": "string", "description": "Reason the grammar delivery or language load failed; the text representation remains untrusted data." },
	        "languages": { "type": "array", "items": { "type": "string" }, "description": "Language ids whose grammar failed; empty when the entire delivery source is unavailable." }
	      },
	      "required": ["reason", "languages"],
	      "additionalProperties": false
	    },
	    "protection": {
	      "type": "object",
	      "description": "Content-protection policy applied to this analysis.",
	      "properties": {
	        "secrets": { "type": "string", "const": "always", "description": "Mandatory secret-redaction state." },
	        "privateData": { "type": "string", "enum": ["enabled", "disabled"], "description": "Server-startup private-data redaction state." }
	      },
	      "required": ["secrets", "privateData"],
	      "additionalProperties": false
	    },
	    "remote": {
	      "type": "object",
	      "description": "Pinned remote checkout identity; absent for local projects.",
	      "properties": {
	        "commit": { "type": "string", "description": "Commit SHA selected by the repository-cache session." },
	        "branch": { "type": "string", "description": "Repository-controlled requested or resolved branch, kept inside the untrusted structured payload." }
	      },
	      "required": ["commit", "branch"],
	      "additionalProperties": false
	    },
	    "topFiles": {
	      "type": "array",
	      "description": "Largest selected text files ordered by estimated tokens, then path.",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string", "description": "Project-relative file path." },
	          "tokens": { "type": "integer", "description": "Estimated tokens for this file." },
	          "estimated": { "type": "boolean", "description": "True when this entry uses size-based metrics instead of inspected transformed content." },
	          "uninspected": { "type": "boolean", "description": "True when bounded secret inspection could not read this file and its metrics are estimated." }
	        },
	        "required": ["path", "tokens", "estimated"],
	        "additionalProperties": false
	      }
	    },
	    "topFilesTruncated": { "type": "boolean", "description": "True when the aggregate top-files character budget omitted remaining entries." },
	    "topFilesRemaining": { "type": "integer", "minimum": 0, "description": "Number of requested top-file entries omitted by the aggregate character budget." }
	  },
	  "required": ["files", "characters", "tokens", "detail", "contentMetrics", "documentMetrics", "exclusions", "protection", "topFiles", "topFilesTruncated", "topFilesRemaining"],
	  "additionalProperties": false
	}
	""";

}
