using DevProjex.Application.Dependencies;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class TreeSitterDependencyFactExtractor
{
	private const int MaximumRootRecoveryAttempts = 8;

	private Tree ParseWithSyntaxRecovery(Parser parser, LanguageId language,
		string source, string relativePath, CancellationToken cancellationToken,
		out IReadOnlyList<SyntaxDamageRegion> omittedRegions,
		out IReadOnlyList<DependencySyntaxCapture> recoveredHeaders)
	{
		var original = ParseWithConditionalProjection(parser, language, source, relativePath,
			cancellationToken, out omittedRegions);
		List<DependencySyntaxCapture>? headers = null;
		var recovered = RecoverDamagedRoot(parser, original, source, language, cancellationToken, ref omittedRegions, ref headers);
		recoveredHeaders = headers ?? [];
		return recovered;
	}

	private Tree RecoverDamagedRoot(
		Parser parser,
		Tree original,
		string source,
		LanguageId language,
		CancellationToken cancellationToken,
		ref IReadOnlyList<SyntaxDamageRegion> omittedRegions,
		ref List<DependencySyntaxCapture>? recoveredHeaders)
	{
		char[]? projected = null;
		try
		{
			for (var attempt = 0; attempt < MaximumRootRecoveryAttempts && original.RootNode.HasError; attempt++)
			{
				if (!TryFindDamagedRootConstruction(original.RootNode, cancellationToken, out var region, out var construction))
					break;
				if (language is LanguageId.TypeScript or LanguageId.Tsx &&
					RecoverCallableInterfaceHeader(construction!, region) is { } header)
					(recoveredHeaders ??= []).Add(header);
				if (projected is null)
				{
					projected = source.ToCharArray();
					foreach (var omitted in omittedRegions)
						for (var index = omitted.StartIndex; index < omitted.EndIndex; index++)
							if (projected[index] is not ('\r' or '\n')) projected[index] = ' ';
				}
				for (var index = region.StartIndex; index < region.EndIndex; index++)
					if (projected[index] is not ('\r' or '\n')) projected[index] = ' ';
				var replacement = ParseTree(parser, new string(projected));
				original.Dispose();
				original = replacement;
				omittedRegions = [.. omittedRegions, region];
			}
			return original;
		}
		catch
		{
			original.Dispose();
			throw;
		}
	}

	private static bool TryFindDamagedRootConstruction(
		Node root,
		CancellationToken cancellationToken,
		out SyntaxDamageRegion region,
		out Node? recoveredConstruction)
	{
		region = default;
		recoveredConstruction = null;
		foreach (var defect in SyntaxTokens(root, cancellationToken))
		{
			if (!defect.IsMissing || defect.Type is not ("}" or ")" or "]"))
				continue;
			var construction = defect;
			while (construction.Parent is { } parent && parent != root)
				construction = parent;
			if (construction == defect)
				continue;

			// A missing closer can prematurely end a construction and turn the whole document
			// into ERROR. Only a matching real syntax token can establish a safe recovery boundary.
			var openingType = defect.Type switch { "}" => "{", ")" => "(", _ => "[" };
			var openings = new Stack<Node>();
			foreach (var token in SyntaxTokens(construction, cancellationToken))
			{
				if (token == defect)
					break;
				if (token.IsMissing)
					continue;
				if (token.Type == openingType)
					openings.Push(token);
				else if (token.Type == defect.Type && openings.Count > 0)
					openings.Pop();
			}
			if (!openings.TryPeek(out var opening))
				continue;
			var depth = 0;
			foreach (var token in SyntaxTokens(root, cancellationToken))
			{
				if (token.IsMissing || token.StartIndex < opening.StartIndex)
					continue;
				if (token.Type == openingType)
					depth++;
				else if (token.Type == defect.Type && --depth == 0)
				{
					if (token.EndIndex <= construction.EndIndex)
						break;
					region = new SyntaxDamageRegion(
						checked((int)construction.StartIndex), checked((int)token.EndIndex),
						checked((int)construction.StartPosition.Row + 1), checked((int)token.EndPosition.Row + 1));
					recoveredConstruction = construction;
					return true;
				}
			}
		}
		return false;
	}

	private static DependencySyntaxCapture? RecoverCallableInterfaceHeader(Node construction, SyntaxDamageRegion region)
	{
		var declaration = construction.Type == "export_statement"
			? construction.GetChildForField("declaration")
			: construction;
		if (declaration?.Type != "interface_declaration" ||
			declaration.GetChildForField("name") is not { HasError: false, IsMissing: false } name ||
			declaration.GetChildForField("body") is not { } body)
			return null;
		var hasCallSignature = false;
		foreach (var child in declaration.NamedChildren)
			if (child != body && (child.HasError || child.IsMissing)) return null;
		foreach (var child in body.NamedChildren)
			if (child.Type == "call_signature" && !child.HasError) hasCallSignature = true;
		if (!hasCallSignature) return null;

		// Only the proven header survives; damaged signature bodies still contribute no references.
		var capture = TryCreateCapture("declaration.interface", declaration, new NodeTextMaterializationCounter());
		return capture is null ? null : capture with { EndIndex = region.EndIndex, EndLine = region.EndLine };
	}

	private static IReadOnlyList<NavigationDeclaration> AddRecoveredNavigation(
		IReadOnlyList<NavigationDeclaration> navigation, IReadOnlyList<DependencySyntaxCapture> headers, string fingerprint) =>
		headers.Count == 0 ? navigation : navigation.Concat(headers.Select(header => new NavigationDeclaration(
			header.CapturedName!, NavigationSymbolKind.Type, null, header.Line, header.EndLine, fingerprint)
		{
			StartIndex = header.StartIndex,
			EndIndex = header.EndIndex
		})).OrderBy(static declaration => declaration.StartLine)
		.ThenBy(static declaration => declaration.EndLine)
		.ThenBy(static declaration => declaration.Name, StringComparer.Ordinal).ToArray();

	private static IEnumerable<Node> SyntaxTokens(Node root, CancellationToken cancellationToken)
	{
		using var cursor = root.Walk();
		var visited = 0;
		while (true)
		{
			if ((visited++ & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			var node = cursor.CurrentNode;
			if (node.IsMissing || node.Type is "{" or "}" or "(" or ")" or "[" or "]")
				yield return node;
			// Valid subtrees balance their own delimiters, including literal interpolation.
			// A cursor avoids repeated indexed child lookups on a wide ERROR node.
			if ((node.HasError || node.IsError) && cursor.GotoFirstChild())
				continue;
			while (!cursor.GotoNextSibling())
				if (!cursor.GotoParent())
					yield break;
		}
	}
}
