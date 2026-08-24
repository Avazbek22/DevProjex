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
			Create(target, nameof(DevProjexMcpTools.GetTree), "get_tree", "Get project tree", GetTreeInput, TruncationOutput),
			Create(target, nameof(DevProjexMcpTools.Analyze), "analyze", "Analyze project", AnalyzeInput, AnalyzeOutput),
			Create(target, nameof(DevProjexMcpTools.PackContext), "pack_context", "Pack project context", PackContextInput, PackOutput, largeResult: true),
			Create(target, nameof(DevProjexMcpTools.ReadPack), "read_pack", "Read context pack", ReadPackInput, PageOutput, largeResult: true),
			Create(target, nameof(DevProjexMcpTools.SearchProject), "search_project", "Search project", SearchInput, SearchOutput),
			Create(target, nameof(DevProjexMcpTools.GetFile), "get_file", "Get project file", GetFileInput, FileOutput)
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
		string outputSchema,
		bool largeResult = false)
	{
		var method = typeof(DevProjexMcpTools).GetMethod(
			methodName,
			BindingFlags.Instance | BindingFlags.Public) ??
		             throw new MissingMethodException(typeof(DevProjexMcpTools).FullName, methodName);
		var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
		var tool = McpServerTool.Create(
			method,
			target,
			new McpServerToolCreateOptions
			{
				Name = name,
				Title = title,
				Description = description,
				ReadOnly = true,
				Destructive = false,
				Idempotent = true,
				OpenWorld = false,
				UseStructuredContent = true,
				OutputSchema = ParseSchema(outputSchema)
			});
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

	private static readonly string ListProjectsInput = EmptyInput;

	private static readonly string GetTreeInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    "max_depth": {
	      "description": "Maximum tree depth from 0 to 1000; accepts an integer or numeric string.",
	      "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 1000 }, { "type": "string", "pattern": "^[0-9]+$" } ]
	    }
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string SelectionInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{ProfileProperty}}
	  },
	  "additionalProperties": false
	}
	""";

	private static readonly string AnalyzeInput = SelectionInput;

	private static readonly string PackContextInput = $$"""
	{
	  "type": "object",
	  "properties": {
	    {{ProjectProperty}},
	    {{PathsProperty}},
	    {{IncludeProperty}},
	    {{ExcludeProperty}},
	    {{ProfileProperty}},
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
	    "context_lines": { "description": "Context lines from 0 to 20; default 2; integer or numeric string.", "oneOf": [ { "type": "integer", "minimum": 0, "maximum": 20 }, { "type": "string", "pattern": "^[0-9]+$" } ] },
	    "ignore_case": { "type": "boolean", "default": true },
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

	private const string TruncationOutput = """
	{
	  "type": "object",
	  "properties": {
	    "lines": { "type": "integer" },
	    "totalLines": { "type": "integer" },
	    "truncated": { "type": "boolean" }
	  },
	  "required": ["lines", "totalLines", "truncated"],
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
	  "required": ["files", "characters", "tokens", "topFiles"],
	  "additionalProperties": false
	}
	""";

	private const string PackOutput = """
	{
	  "type": "object",
	  "properties": {
	    "files": { "type": "integer" },
	    "characters": { "type": "integer" },
	    "lines": { "type": "integer" },
	    "stored": { "type": "boolean" },
	    "packId": { "type": ["string", "null"] }
	  },
	  "required": ["files", "characters", "lines", "stored", "packId"],
	  "additionalProperties": false
	}
	""";

	private const string PageOutput = """
	{
	  "type": "object",
	  "properties": {
	    "startLine": { "type": "integer" },
	    "endLine": { "type": "integer" },
	    "totalLines": { "type": "integer" },
	    "truncated": { "type": "boolean" }
	  },
	  "required": ["startLine", "endLine", "totalLines", "truncated"],
	  "additionalProperties": false
	}
	""";

	private const string SearchOutput = """
	{
	  "type": "object",
	  "properties": {
	    "matches": { "type": "integer" },
	    "shown": { "type": "integer" },
	    "truncated": { "type": "boolean" }
	  },
	  "required": ["matches", "shown", "truncated"],
	  "additionalProperties": false
	}
	""";

	private const string FileOutput = """
	{
	  "type": "object",
	  "properties": {
	    "path": { "type": "string" },
	    "startLine": { "type": "integer" },
	    "endLine": { "type": "integer" },
	    "totalLines": { "type": "integer" },
	    "truncated": { "type": "boolean" }
	  },
	  "required": ["path", "startLine", "endLine", "totalLines", "truncated"],
	  "additionalProperties": false
	}
	""";
}
