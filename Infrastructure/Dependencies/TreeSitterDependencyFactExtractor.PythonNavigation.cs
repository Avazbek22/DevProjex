using DevProjex.Application.Dependencies;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class TreeSitterDependencyFactExtractor
{
	private static void AddPythonModuleAssignments(List<NavigationDeclaration> declarations, Node root,
		string fingerprint, CancellationToken cancellationToken, SyntaxDamageAnalysis? syntaxDamage)
	{
		var bindings = new Dictionary<string, Node>(StringComparer.Ordinal);
		foreach (var statement in PythonNamedChildren(root))
			VisitStatement(statement, true);
		if (bindings.Count == 0) return;
		declarations.RemoveAll(declaration => declaration.Owner is null && bindings.ContainsKey(declaration.Name));
		foreach (var (name, assignment) in bindings)
			declarations.Add(new NavigationDeclaration(name, NavigationSymbolKind.Field, null,
				checked((int)assignment.StartPosition.Row + 1), checked((int)assignment.EndPosition.Row + 1), fingerprint)
			{
				StartIndex = checked((int)assignment.StartIndex),
				EndIndex = checked((int)assignment.EndIndex)
			});
		return;

		void VisitStatement(Node statement, bool unconditional)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (syntaxDamage is not null && IsCaptureAffectedBySyntaxDamage(statement, root, syntaxDamage)) return;
			switch (statement.Type)
			{
				case "expression_statement":
					foreach (var child in PythonNamedChildren(statement))
						if (child.Type == "assignment") VisitStatement(child, unconditional);
					break;
				case "assignment":
					if (statement.HasError) break;
					for (var assignment = statement; assignment?.Type == "assignment";
						 assignment = assignment.GetChildForField("right"))
					{
						if (assignment.GetChildForField("right") is null) continue;
						foreach (var name in PythonBindingNames(assignment.GetChildForField("left")))
							if (unconditional) bindings[name] = statement;
							else bindings.Remove(name);
					}
					break;
				case "delete_statement":
					foreach (var target in PythonNamedChildren(statement))
						foreach (var name in PythonBindingNames(target)) bindings.Remove(name);
					break;
				case "decorated_definition":
					if (statement.GetChildForField("definition") is { } definition) VisitStatement(definition, unconditional);
					break;
				case "function_definition":
				case "class_definition":
					if (statement.GetChildForField("name") is { } declaredName) bindings.Remove(declaredName.Text);
					break;
				case "import_statement":
				case "import_from_statement":
					foreach (var imported in statement.GetChildrenForField("name"))
					{
						var alias = imported.GetChildForField("alias");
						var name = alias?.Text ?? imported.Text.Split('.')[0];
						bindings.Remove(name);
					}
					if (PythonNamedChildren(statement).Any(child => child.Type == "wildcard_import")) bindings.Clear();
					break;
				case "for_statement":
					foreach (var name in PythonBindingNames(statement.GetChildForField("left"))) bindings.Remove(name);
					VisitConditionalBody(statement);
					break;
				case "if_statement":
				case "try_statement":
				case "while_statement":
				case "with_statement":
				case "match_statement":
				case "elif_clause":
				case "else_clause":
				case "except_clause":
				case "finally_clause":
				case "case_clause":
				case "block":
					VisitConditionalBody(statement);
					break;
			}
		}

		void VisitConditionalBody(Node statement)
		{
			foreach (var child in PythonNamedChildren(statement)) VisitStatement(child, false);
		}
	}

	private static IEnumerable<string> PythonBindingNames(Node? target)
	{
		if (target is null) yield break;
		if (target.Type == "identifier")
		{
			yield return target.Text;
			yield break;
		}
		// Deleting an item or attribute mutates its owner; it does not unbind that owner's name.
		if (target.Type is not ("pattern_list" or "tuple_pattern" or "list_pattern" or "list_splat_pattern" or
			"expression_list" or "tuple" or "list" or "parenthesized_expression")) yield break;
		foreach (var child in PythonNamedChildren(target))
			foreach (var name in PythonBindingNames(child)) yield return name;
	}

	private static IEnumerable<Node> PythonNamedChildren(Node parent)
	{
		using var cursor = parent.Walk();
		if (!cursor.GotoFirstChild()) yield break;
		do
		{
			var child = cursor.CurrentNode;
			if (child.IsNamed) yield return child;
		} while (cursor.GotoNextSibling());
	}
}
