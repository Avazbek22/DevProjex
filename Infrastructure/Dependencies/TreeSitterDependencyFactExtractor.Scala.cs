using DevProjex.Application.Dependencies;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class TreeSitterDependencyFactExtractor
{
	private static Node ScalaNavigationEnd(Node node)
	{
		var body = node.GetChildForField("body") ?? node.NamedChildren.LastOrDefault(static child => child.Type == "template_body");
		if (body is null || body.Type != "indented_block" &&
			(body.Type != "template_body" || body.Children.FirstOrDefault()?.Type != ":")) return node;
		var last = body.NamedChildren.LastOrDefault(child =>
			child.Type is not ("comment" or "block_comment") || child.StartPosition.Column > node.StartPosition.Column);
		return last is null ? node : ScalaNavigationEnd(last);
	}

	private static string ReadScalaNamespace(Node node)
	{
		var parts = new Dictionary<int, string>();
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
		{
			if (parent.Type == "package_clause" && parent.GetChildForField("name") is { } owner)
				parts[checked((int)parent.StartIndex)] = owner.Text;
			foreach (var sibling in parent.NamedChildren)
			{
				if (sibling.StartIndex >= node.StartIndex) break;
				if (sibling.Type == "package_clause" && sibling.GetChildForField("body") is null &&
					!sibling.NamedChildren.Any(static child => child.Type == "template_body") && sibling.GetChildForField("name") is { } name)
					parts[checked((int)sibling.StartIndex)] = name.Text;
			}
		}
		return string.Join('.', parts.OrderBy(static part => part.Key).Select(static part => part.Value));
	}

	private static DependencySyntaxCapture? CreateScalaCapture(string captureName, Node node, NodeTextMaterializationCounter materialization)
	{
		if (captureName == "import.scala_import")
		{
			var text = materialization.Read(node);
			var scope = node.Parent ?? node;
			return CreateCapture(captureName, node, text, null, 0, false, false,
				ReadScalaNamespace(node), ReadScalaImport(node, materialization), evidence: OneLineEvidence(text)) with
			{
				ScopeStartIndex = checked((int)node.EndIndex),
				ScopeEndIndex = checked((int)scope.EndIndex)
			};
		}
		if (captureName == "context.scala_parameter")
		{
			var owner = node.Parent?.Parent ?? node;
			var text = materialization.Read(node);
			return CreateCapture(captureName, node, text, text, 0, false, false, evidence: text) with
			{
				StartIndex = checked((int)owner.StartIndex),
				EndIndex = checked((int)owner.EndIndex)
			};
		}
		if (captureName == "context.scala_value")
		{
			var binding = node.GetChildForField("name") ?? node.GetChildForField("pattern") ?? node.NamedChildren.FirstOrDefault();
			var bindingName = binding?.Type == "identifier" ? materialization.Read(binding) : "$unknown-binding";
			var scope = node.Parent ?? node;
			while (scope.Parent is not null && scope.Type is not ("template_body" or "block" or "indented_block" or "compilation_unit" or "class_definition" or "function_definition"))
				scope = scope.Parent;
			return CreateCapture(captureName, scope, bindingName, bindingName, 0, false, false, evidence: bindingName);
		}
		if (captureName == "reference.scala_type" && node.Parent?.Type == "stable_type_identifier") return null;
		var nameNode = captureName.StartsWith("declaration.", StringComparison.Ordinal)
			? node.GetChildForField("name") ?? (node.Type == "val_definition" ? node.NamedChildren.FirstOrDefault(static child => child.Type == "identifier") : null)
			: node;
		if (nameNode is null) return null;
		var name = materialization.Read(nameNode);
		var namespaceName = ReadScalaNamespace(node);
		var owners = ReadNavigationOwners(node, LanguageId.Scala);
		var local = false;
		for (var parent = node.Parent; parent is not null; parent = parent.Parent)
			if (parent.Type == "function_definition") local = true;
		return CreateCapture(captureName, node, name, name, 0, local, false,
			owners.Count == 0 ? null : string.Join('.', owners), capturedNameStartIndex: checked((int)nameNode.StartIndex), evidence: name) with
		{
			LexicalNamespace = namespaceName
		};
	}

	private static DependencyImportSyntax ReadScalaImport(Node node, NodeTextMaterializationCounter materialization)
	{
		if (node.Children.Any(static child => child.Type == ",")) return new(string.Empty, 0, [], false);
		var children = node.NamedChildren.ToArray();
		var prefix = new List<string>();
		var bindings = new List<DependencyImportBinding>();
		foreach (var child in children)
		{
			if (child.Type == "identifier") prefix.Add(materialization.Read(child).Trim('`'));
			else if (child.Type == "namespace_selectors")
			{
				foreach (var selector in child.NamedChildren)
				{
					if (selector.Type == "identifier") bindings.Add(new(materialization.Read(selector).Trim('`'), null));
					else if (selector.Type == "namespace_wildcard") bindings.Add(new("*", null, true));
					else if (selector.Type is "arrow_renamed_identifier" or "as_renamed_identifier")
					{
						var names = selector.NamedChildren.Select(materialization.Read).Select(static name => name.Trim('`')).ToArray();
						if (names.Length == 2) bindings.Add(new(names[0], names[1]));
					}
				}
			}
			else if (child.Type == "namespace_wildcard") bindings.Add(new("*", null, true));
			else return new(string.Empty, 0, [], false);
		}
		if (bindings.Count == 0 && prefix.Count > 0)
		{
			bindings.Add(new(prefix[^1], null));
			prefix.RemoveAt(prefix.Count - 1);
		}
		return new(string.Join('.', prefix), 0, bindings, bindings.Count > 0);
	}
}
