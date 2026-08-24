using System.Collections;
using System.Reflection;

namespace DevProjex.Mcp;

internal sealed class DevProjexMcpToolCatalog : IReadOnlyList<McpServerTool>
{
	private const string MaximumResultSizeKey = "anthropic/maxResultSizeChars";
	private readonly IReadOnlyList<McpServerTool> _tools;

	public DevProjexMcpToolCatalog(DevProjexMcpTools target)
	{
		ArgumentNullException.ThrowIfNull(target);
		_tools =
		[
			Create(target, nameof(DevProjexMcpTools.ListProjects), "list_projects", "List projects", ListProjectsInput, ListProjectsOutput),
			Create(target, nameof(DevProjexMcpTools.GetTree), "get_tree", "Get project tree", GetTreeInput),
			Create(target, nameof(DevProjexMcpTools.Analyze), "analyze", "Analyze project", AnalyzeInput, AnalyzeOutput),
			Create(target, nameof(DevProjexMcpTools.PackContext), "pack_context", "Pack project context", PackContextInput, largeResult: true),
			Create(target, nameof(DevProjexMcpTools.ReadPack), "read_pack", "Read context pack", ReadPackInput, largeResult: true),
			Create(target, nameof(DevProjexMcpTools.SearchProject), "search_project", "Search project", SearchInput),
			Create(target, nameof(DevProjexMcpTools.GetFile), "get_file", "Get project file", GetFileInput)
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
		bool largeResult = false)
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
			Idempotent = true,
			OpenWorld = false
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

	private static JsonElement ParseSchema(string json) => JsonDocument.Parse(json).RootElement.Clone();

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
	  "description": "Absolute root path returned by list_projects. Optional only when one root is configured."
	}
	""";

	private const string IncludeProperty = """
	"include_patterns": {
	  "type": "array",
	  "items": { "type": "string" },
	  "description": "Project-relative glob patterns using '/'. They only narrow built-in and gitignore filtering."
	}
	""";

	private const string ExcludeProperty = """
	"exclude_patterns": {
	  "type": "array",
	  "items": { "type": "string" },
	  "description": "Project-relative glob patterns using '/' to exclude additional paths."
	}
	""";

	private const string PathsProperty = """
	"paths": {
	  "type": "array",
	  "items": { "type": "string" },
	  "description": "Existing project-relative files or directories that narrow the selection."
	}
	""";

	private const string ProfileProperty = """
	"profile": {
	  "type": "string",
	  "description": "Selection profile: 'standard', 'local', or a portable profile JSON path inside the project root."
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

	private static readonly string ListProjectsInput = EmptyInput;

	private static readonly string GetTreeInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{TrackedOnlyProperty}},
	    "max_depth": {
	      "description": "Maximum tree depth from 0 to 1000; accepts an integer or numeric string.",
	      "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 1000 }, { "type": "string", "pattern": "^[0-9]+$" } ]
	    }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string AnalyzeInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{ProfileProperty}},
	    {{DetailProperty}},
	    {{TrackedOnlyProperty}}
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string PackContextInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{ProfileProperty}},
	    {{DetailProperty}},
	    {{TrackedOnlyProperty}},
	    "view": { "type": "string", "enum": ["tree", "content", "tree-content"], "default": "tree-content" },
	    "format": { "type": "string", "enum": ["text", "markdown", "json", "xml"], "default": "markdown" }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string ReadPackInput = """
	{
	  "type": "object",
	  "properties": {
	    "pack_id": { "type": "string", "description": "Session-scoped id returned by pack_context." },
	    "start_line": { "description": "First 1-based line; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "end_line": { "description": "Last 1-based line, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^[0-9]+$" } ] }
	  },
	  "required": ["pack_id"],
	  "additionalProperties": false
	}
	""";

	private static readonly string SearchInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    "pattern": { "type": "string", "description": "A .NET regular expression evaluated against redacted text with a 2-second timeout." },
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{TrackedOnlyProperty}},
	    "context_lines": { "description": "Context lines from 0 to 20; default 2; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 20 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "ignore_case": { "description": "Case-insensitive matching; accepts a boolean or the string 'true' or 'false'.", "default": true, "oneOf": [ { "type": "boolean" }, { "type": "string", "enum": ["true", "false"] } ] },
	    "max_results": { "description": "Maximum matches from 1 to 200; default 50; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1, "maximum": 200 }, { "type": "string", "pattern": "^[0-9]+$" } ] }
	  },
	  "required": ["pattern"],
	  "additionalProperties": false
	}
	""";

	private static readonly string GetFileInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    "path": { "type": "string", "description": "Existing file path inside the effective project selection." },
	    "start_line": { "description": "First 1-based line; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "end_line": { "description": "Last 1-based line, inclusive; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 1 }, { "type": "string", "pattern": "^[0-9]+$" } ] }
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
	    }
	  },
	  "required": ["projects", "profiles"],
	  "additionalProperties": false
	}
	""";

	private const string AnalyzeOutput = """
	{
	  "type": "object",
	  "properties": {
	    "files": { "type": "integer" },
	    "characters": { "type": "integer" },
	    "tokens": { "type": "integer" },
	    "detail": { "type": "string", "enum": ["full", "compact", "signatures"] },
	    "topFiles": {
	      "type": "array",
	      "items": {
	        "type": "object",
	        "properties": {
	          "path": { "type": "string" },
	          "tokens": { "type": "integer" }
	        },
	        "required": ["path", "tokens"],
	        "additionalProperties": false
	      }
	    }
	  },
	  "required": ["files", "characters", "tokens", "detail", "topFiles"],
	  "additionalProperties": false
	}
	""";

}
