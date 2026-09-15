using System.Collections;
using System.Reflection;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpToolCatalog : IReadOnlyList<McpServerTool>
{
	private const string MaximumResultSizeKey = "anthropic/maxResultSizeChars";
	private const string SearchHintKey = "anthropic/searchHint";
	private readonly IReadOnlyList<McpServerTool> _tools;

	public DevProjexMcpToolCatalog(DevProjexMcpTools target, bool allowRemote, bool agentExclusions = false,
		McpToolSet toolSet = McpToolSet.Full)
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
		if (toolSet == McpToolSet.Reduced)
			_tools = _tools.Where(tool => tool.ProtocolTool.Name is not ("analyze" or "pack_context")).ToArray();
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
		if (outputSchema is not null && name is not ("list_projects" or "analyze"))
		{
			options.UseStructuredContent = true;
			options.OutputSchema = ParseSchema(outputSchema);
		}
		var tool = McpServerTool.Create(method, target, options);
		tool.ProtocolTool.InputSchema = ParseSchema(inputSchema);
		tool.ProtocolTool.Meta = new System.Text.Json.Nodes.JsonObject
		{
			[SearchHintKey] = SearchHint(name)
		};
		if (largeResult)
			tool.ProtocolTool.Meta[MaximumResultSizeKey] = 200_000;
		return tool;
	}

	private static string SearchHint(string name) => name switch
	{
		"list_projects" => "discover configured projects and policies",
		"get_tree" => "inspect project structure by path and pattern",
		"analyze" => "measure selected project content before packaging",
		"pack_context" => "package selected project files into context",
		"read_pack" => "continue reading a stored project result",
		"search_project" => "search file contents with a regular expression",
		"related_files" => "trace static dependencies around known files",
		"get_file" => "read project files by path, range, or symbol",
		_ => throw new ArgumentOutOfRangeException(nameof(name), name, null)
	};

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
		      "description": "Full exclusion set for this call: [] disables every toggle for the widest scan, while omission keeps the server baseline; analyze echoes the result. Overrides profile exclusions. Tokens ignore case and reject duplicates; hidden-* uses platform attributes, while Unix dot names use dot-*."
		    }
		""";
	}

	private const string TopFilesProperty = """
	"top_files": {
	  "description": "Returns this many largest text files by estimated tokens.",
	  "default": 10,
	  "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 1000 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string TreeFormatProperty = """
	"format": {
	  "type": "string",
	  "enum": ["markdown", "text", "json", "xml"],
	  "default": "markdown",
	  "description": "Selects the tree representation; markdown is the compact default."
	}
	""";

	private const string CompactProjectProperty = """
	"project": {
	  "type": "string",
	  "description": "Project name, listed absolute path, or allowed remote Git URL; omit only with one local root."
	}
	""";

	private const string CompactBranchProperty = """
	"branch": {
	  "type": "string",
	  "minLength": 1,
	  "description": "Remote Git branch; invalid for local projects."
	}
	""";

	private const string CompactIncludeProperty = """
	"include_patterns": {
	  "description": "Narrows with project-relative '/' globs: '*' and '?' stay in one segment, '**/' spans depths, and '{a,b}' alternates; matching is case-sensitive, while '!' and '[...]' are rejected.",
	  "oneOf": [
	    { "type": "string", "minLength": 1, "maxLength": 512 },
	    { "type": "array", "maxItems": 256, "items": { "type": "string", "minLength": 1, "maxLength": 512 } }
	  ]
	}
	""";

	private const string CompactExcludeProperty = """
	"exclude_patterns": {
	  "description": "Removes paths with the same project-relative glob syntax as include_patterns.",
	  "oneOf": [
	    { "type": "string", "minLength": 1, "maxLength": 512 },
	    { "type": "array", "maxItems": 256, "items": { "type": "string", "minLength": 1, "maxLength": 512 } }
	  ]
	}
	""";

	private const string CompactPathsProperty = """
	"paths": {
	  "description": "Narrows selection to literal paths for existing project-relative files or directories; glob characters are ordinary.",
	  "oneOf": [
	    { "type": "string", "minLength": 1, "maxLength": 4096 },
	    { "type": "array", "maxItems": 256, "items": { "type": "string", "minLength": 1, "maxLength": 4096 } }
	  ]
	}
	""";

	private const string CompactProfileProperty = """
	"profile": {
	  "type": "string",
	  "minLength": 1,
	  "description": "Selection profile: standard uses all eight exclusion toggles plus gitignore; local uses the desktop profile listed by list_projects.profiles; otherwise give a portable profile path inside the project."
	}
	""";

	private const string CompactDetailProperty = """
	"detail": {
	  "type": "string",
	  "enum": ["full", "compact", "signatures"],
	  "default": "full",
	  "description": "Content transform: full keeps text, compact removes comments and blank lines, and signatures keeps supported code signatures; other languages remain unchanged."
	}
	""";

	private const string CompactDetailByPatternProperty = """
	"detail_by_pattern": {
	  "type": "array",
	  "maxItems": 16,
	  "items": {
	    "type": "object",
	    "properties": {
	      "patterns": {
	        "type": "array",
	        "minItems": 1,
	        "maxItems": 32,
	        "items": { "type": "string", "minLength": 1, "maxLength": 512 },
	        "description": "Uses the include_patterns glob syntax."
	      },
	      "detail": { "type": "string", "enum": ["full", "compact", "signatures"] }
	    },
	    "required": ["patterns", "detail"],
	    "additionalProperties": false
	  },
	  "description": "Overrides detail by file in array order; the last match wins, never widens selection, and unmatched or invalid entries are reported."
	}
	""";

	private const string CompactTrackedOnlyProperty = """
	"tracked_only": {
	  "description": "Restricts results to Git-tracked files; accepts a boolean or its string form.",
	  "default": false,
	  "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ]
	}
	""";

	private const string CompactMaximumTokensProperty = """
	"max_tokens": {
	  "description": "Sets greedy content admission; document structure and its report are outside this heuristic token budget.",
	  "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private const string CompactRankProperty = """
	"rank": {
	  "type": "string",
	  "enum": ["importance"],
	  "description": "Orders the selection with importance-v1 and controls max_tokens admission; omission keeps ordinary order and avoids dependency or Git-history work."
	}
	""";

	private const string CompactFocusProperty = """
	"focus": {
	  "description": "Seeds focus-v1 order from 1..16 selected paths; requires rank=importance and never widens selection.",
	  "oneOf": [
	    { "type": "string", "minLength": 1, "maxLength": 4096 },
	    { "type": "array", "minItems": 1, "maxItems": 16, "items": { "type": "string", "minLength": 1, "maxLength": 4096 } }
	  ]
	}
	""";

	private const string CompactGitScopeProperty = """
	"git_scope": {
	  "description": "Narrows to staged, changed (including untracked files), or diff:<ref>..<ref> paths; content stays from the current working tree.",
	  "maxLength": 4096,
	  "oneOf": [
	    { "type": "string", "enum": ["staged", "changes"] },
	    { "type": "string", "pattern": "^diff:(?!.*\\.\\.\\.)(?!.*\\.\\..*\\.\\.)[^\\s-]\\S*\\.\\.[^\\s-]\\S*$" }
	  ]
	}
	""";

	private const string CompactMaxFileBytesProperty = """
	"max_file_bytes": {
	  "description": "Excludes selected files larger than this byte count; accepts an integer or numeric string.",
	  "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ]
	}
	""";

	private static string CompactExpandRelatedProperty =>
		$$"""
	"expand_related": {
	  "description": "Adds resolved neighbours of seed files without widening filters or Git scope; stops at {{McpRelatedExpansion.MaximumExpandedFiles}} files and reports the limit.",
	  "type": "object",
	  "properties": {
	    "seeds": { "type": "array", "minItems": 1, "maxItems": {{McpRelatedExpansion.MaximumSeeds}}, "items": { "type": "string" }, "description": "Selected project-relative seed files; directories and globs are invalid." },
	    "hops": { "type": "integer", "minimum": {{McpRelatedExpansion.MinimumHops}}, "maximum": {{McpRelatedExpansion.MaximumHops}}, "default": {{McpRelatedExpansion.MinimumHops}}, "description": "Number of edges to follow from each seed." },
	    "direction": { "type": "string", "enum": ["dependencies", "dependents", "both"], "default": "both", "description": "Traversal direction, matching related_files." }
	  },
	  "required": ["seeds"],
	  "additionalProperties": false
	}
	""";

	private static readonly string ListProjectsInput = EmptyInput;

	private static string GetTreeInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    {{CompactPathsProperty}},
	    {{CompactIncludeProperty}},
	    {{CompactExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{CompactTrackedOnlyProperty}},
	    {{CompactGitScopeProperty}},
	    {{CompactMaxFileBytesProperty}},
	    "max_depth": {
	      "description": "Limits levels below the project root without descending below a paths entry; omit it for the deepest complete tree that fits.",
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
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    {{CompactPathsProperty}},
	    {{CompactIncludeProperty}},
	    {{CompactExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{CompactProfileProperty}},
	    {{CompactDetailProperty}},
	    {{CompactDetailByPatternProperty}},
	    {{CompactTrackedOnlyProperty}},
	    {{CompactGitScopeProperty}},
	    {{TopFilesProperty}},
	    {{CompactMaxFileBytesProperty}},
	    {{CompactMaximumTokensProperty}},
	    {{CompactRankProperty}},
	    {{CompactFocusProperty}}
	  },
	  "additionalProperties": false
	}
	""";

	private static string PackContextInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    {{CompactPathsProperty}},
	    {{CompactIncludeProperty}},
	    {{CompactExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{CompactProfileProperty}},
	    {{CompactDetailProperty}},
	    {{CompactDetailByPatternProperty}},
	    {{CompactTrackedOnlyProperty}},
	    {{CompactGitScopeProperty}},
	    {{CompactRankProperty}},
	    {{CompactFocusProperty}},
	    {{CompactMaximumTokensProperty}},
	    {{CompactMaxFileBytesProperty}},
	    {{CompactExpandRelatedProperty}},
	    "view": { "type": "string", "enum": ["tree", "content", "tree-content"], "default": "tree-content", "description": "Chooses tree, file content, or both for the pack." },
	    "format": { "type": "string", "enum": ["text", "markdown", "json", "xml"], "default": "markdown", "description": "Chooses readable markdown/text or structured JSON/XML output." }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string ReadPackInput = """
	{
	  "type": "object",
	  "properties": {
	    "pack_id": { "type": "string", "minLength": 1, "description": "Session id returned by pack_context, search_project, or related_files." },
	    "start_line": { "description": "First 1-based returned-text line; accepts integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last inclusive 1-based returned-text line; accepts integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "start_column": { "description": "First 1-based Unicode character on start_line; use a returned continuation value.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "required": ["pack_id"],
	  "additionalProperties": false
	}
	""";

	private static string SearchInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    "pattern": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "Required .NET regex with a 2-second timeout over redacted file content, never paths; use get_tree include_patterns for names. Text inserted by redaction never matches." },
	    {{CompactPathsProperty}},
	    {{CompactIncludeProperty}},
	    {{CompactExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{CompactTrackedOnlyProperty}},
	    {{CompactGitScopeProperty}},
	    {{CompactMaxFileBytesProperty}},
	    "context_lines": { "description": "Context lines per match; overlapping windows merge.", "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 20 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "ignore_case": { "description": "Enables case-insensitive matching; accepts a boolean or its string form.", "default": true, "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ] },
	    "max_results": { "description": "Limits displayed match lines while scanning continues; the boundary reports encountered, retained, and written counts.", "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 200 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	  },
	  "required": ["pattern"],
	  "additionalProperties": false
	}
	""";

	private static string GetFileInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    {{CompactProfileProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    "path": { "type": "string", "minLength": 1, "description": "Single-file form. Exactly one of path or requests is required. Names an existing selected file; Markdown escapes from the default get_tree format are accepted, while format=text supplies literal names." },
	    "requests": {
	      "type": "array",
	      "minItems": 1,
	      "maxItems": 8,
	      "description": "Batch form. Exactly one of requests or path is required. Reads up to eight files and sixteen selections; a path reads the whole file, while ranges or symbol narrow it. Single-file ranges cannot accompany requests.",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string", "minLength": 1, "maxLength": 4096, "description": "Existing selected project file." },
	          "ranges": {
	            "type": "array",
	            "minItems": 1,
	            "maxItems": 16,
	            "description": "Inclusive transformed-text line ranges for this file.",
	            "items": {
	              "type": "object",
	              "properties": {
	                "start_line": { "description": "First 1-based transformed-text line.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	                "end_line": { "description": "Last inclusive 1-based transformed-text line.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] }
	              },
	              "required": ["start_line", "end_line"],
	              "additionalProperties": false
	            }
	          },
	          "symbol": { "type": "string", "minLength": 1, "maxLength": 512, "description": "Reads this named declaration instead of ranges; the two forms cannot combine." }
	        },
	        "required": ["path"],
	        "additionalProperties": false
	      }
	    },
	    "start_line": { "description": "First 1-based returned-text line; accepts integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "end_line": { "description": "Last inclusive 1-based returned-text line; accepts integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "start_column": { "description": "First 1-based Unicode character on start_line; use a returned continuation value.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^0*[1-9][0-9]*$" } ] },
	    "symbol": { "type": "string", "minLength": 1, "maxLength": 512, "description": "Reads a qualified, or file-unique simple, declaration with path instead of a range; search_project supplies names. Cannot combine with line or column fields; missing, unavailable, or non-unique declarations return DPX-MCP-INVALID-ARGUMENTS." }
	  },
	  "additionalProperties": false
	}
	""";

	private static string RelatedFilesInput(bool agentExclusions) => $$"""
	{
	  "type": "object",
	  "properties": {
	    {{CompactProjectProperty}},
	    {{CompactBranchProperty}},
	    "path": {
	      "description": "Seed file, or up to 16 seed files, within the effective selection.",
	      "oneOf": [
	        { "type": "string", "minLength": 1, "maxLength": 4096 },
	        { "type": "array", "minItems": 1, "maxItems": 16, "items": { "type": "string", "minLength": 1, "maxLength": 4096 } }
	      ]
	    },
	    "direction": { "type": "string", "enum": ["dependencies", "dependents", "both"], "default": "both", "description": "Chooses outward dependencies, inward dependents, or both." },
	    {{CompactIncludeProperty}},
	    {{CompactExcludeProperty}}{{(agentExclusions ? ExclusionsPropertyFragment() : "")}},
	    {{CompactProfileProperty}},
	    {{CompactTrackedOnlyProperty}},
	    {{CompactGitScopeProperty}},
	    {{CompactMaxFileBytesProperty}}
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
	    "files": { "type": "integer", "description": "Files in the effective selection." },
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
	            "files": { "type": "integer" },
	            "lines": { "type": "integer" },
	            "characters": { "type": "integer", "description": "Normalized characters." },
	            "tokens": { "type": "integer", "description": "Estimated tokens." }
	          },
	          "required": ["files", "lines", "characters", "tokens"],
	          "additionalProperties": false
	        },
	        "estimated": {
	          "type": "object",
	          "properties": {
	            "files": { "type": "integer", "description": "Text files with size-based estimates only." },
	            "characters": { "type": "integer", "description": "Normalized characters." },
	            "tokens": { "type": "integer" }
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
	        "view": { "type": "string", "const": "content" },
	        "format": { "type": "string", "const": "text" },
	        "lines": { "type": "integer" },
	        "characters": { "type": "integer", "description": "Normalized characters." },
	        "tokens": { "type": "integer", "description": "Estimated tokens." },
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
	        "privateData": { "type": "string", "enum": ["enabled", "disabled"], "description": "Server-startup redaction state." }
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
	          "tokens": { "type": "integer", "description": "Estimated tokens." },
	          "estimated": { "type": "boolean", "description": "True when this entry uses size-based metrics instead of inspected transformed content." },
	          "uninspected": { "type": "boolean", "description": "True when bounded secret inspection could not read this file and its metrics are estimated." }
	        },
	        "required": ["path", "tokens", "estimated"],
	        "additionalProperties": false
	      }
	    },
	    "topFilesTruncated": { "type": "boolean", "description": "True when the aggregate top-files character budget omitted remaining entries." },
	    "topFilesRemaining": { "type": "integer", "minimum": 0, "description": "Requested top-file entries omitted by the aggregate character budget." },
	    "admission": {
	      "type": "object",
	      "description": "Files a max_tokens budget would admit, from the same greedy first-fit pass pack_context uses, so the admitted set matches for one snapshot, configuration, filters and effective transforms. Produces no content. Paths are project-relative; token counts are the transformed file at its effective detail; priority is the position in the admission order, only with rank.",
	      "properties": {
	        "budget": { "type": "integer", "minimum": 1, "description": "Estimated content tokens requested." },
	        "includedFileCount": { "type": "integer", "minimum": 0 },
	        "skippedFileCount": { "type": "integer", "minimum": 0, "description": "A skipped file never stops later, smaller ones from being admitted." },
	        "includedEstimatedTokens": { "type": "integer", "minimum": 0 },
	        "skippedEstimatedTokens": { "type": "integer", "minimum": 0 },
	        "includedFiles": {
	          "type": "array",
	          "description": "Prefix of the admission order; at most 1000 entries and a character budget.",
	          "items": {
	            "type": "object",
	            "properties": {
	              "path": { "type": "string" },
	              "tokens": { "type": "integer", "minimum": 0 },
	              "priority": { "type": "integer", "minimum": 1 },
	              "hop": { "type": "integer", "minimum": 0, "description": "Minimum undirected graph hop from a focus seed; only with focus." }
	            },
	            "required": ["path", "tokens"],
	            "additionalProperties": false
	          }
	        },
	        "includedFilesTruncated": { "type": "boolean" },
	        "additionalIncludedFileCount": { "type": "integer", "minimum": 0 },
	        "includedOrderDigest": { "type": "string", "description": "Hash of the complete ordered admitted path list, so equality with a pack is checkable without listing it. Compare only across calls with the same rank and focus." },
	        "skippedFiles": {
	          "type": "array",
	          "description": "The 25 largest skipped files plus, with rank, the 10 highest-priority ones.",
	          "items": {
	            "type": "object",
	            "properties": {
	              "path": { "type": "string" },
	              "tokens": { "type": "integer", "minimum": 0 },
	              "priority": { "type": "integer", "minimum": 1 },
	              "remainingTokens": { "type": "integer", "minimum": 0, "description": "Budget still free when this file was considered." },
	              "detail": { "type": "string", "enum": ["full", "compact", "signatures"], "description": "Level resolved for this file, only with detail_by_pattern; resolved, not a guarantee a transformation applied." }
	            },
	            "required": ["path", "tokens", "remainingTokens"],
	            "additionalProperties": false
	          }
	        },
	        "additionalSkippedFileCount": { "type": "integer", "minimum": 0 },
	        "detail": { "type": "string", "enum": ["full", "compact", "signatures"], "description": "Effective default detail level the admission was measured at." }
	      },
	      "required": ["budget", "includedFileCount", "skippedFileCount", "includedEstimatedTokens", "skippedEstimatedTokens", "includedFiles", "includedFilesTruncated", "additionalIncludedFileCount", "includedOrderDigest", "skippedFiles", "additionalSkippedFileCount", "detail"],
	      "additionalProperties": false
	    }
	  },
	  "required": ["files", "characters", "tokens", "detail", "contentMetrics", "documentMetrics", "exclusions", "protection", "topFiles", "topFilesTruncated", "topFilesRemaining"],
	  "additionalProperties": false
	}
	""";

}
