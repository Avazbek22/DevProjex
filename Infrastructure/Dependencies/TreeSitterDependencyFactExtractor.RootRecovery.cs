using DevProjex.Application.Dependencies;
using TreeSitter;

namespace DevProjex.Infrastructure.Dependencies;

public sealed partial class TreeSitterDependencyFactExtractor
{
	private const int MaximumRootRecoveryAttempts = 8;

	private Tree ParseWithSyntaxRecovery(Parser parser, LanguageId language,
		string source, string relativePath, CancellationToken cancellationToken,
		out IReadOnlyList<SyntaxDamageRegion> omittedRegions)
	{
		var original = ParseWithConditionalProjection(parser, language, source, relativePath,
			cancellationToken, out omittedRegions);
		return RecoverDamagedRoot(parser, original, source, cancellationToken, ref omittedRegions);
	}

	private Tree RecoverDamagedRoot(
		Parser parser,
		Tree original,
		string source,
		CancellationToken cancellationToken,
		ref IReadOnlyList<SyntaxDamageRegion> omittedRegions)
	{
		char[]? projected = null;
		try
		{
			for (var attempt = 0; attempt < MaximumRootRecoveryAttempts && original.RootNode.HasError; attempt++)
			{
				if (!TryFindDamagedRootConstruction(original.RootNode, cancellationToken, out var region))
					break;
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
		out SyntaxDamageRegion region)
	{
		region = default;
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
					return true;
				}
			}
		}
		return false;
	}

	private static IEnumerable<Node> SyntaxTokens(Node root, CancellationToken cancellationToken)
	{
		var pending = new Stack<Node>();
		pending.Push(root);
		var visited = 0;
		while (pending.TryPop(out var node))
		{
			if ((visited++ & 255) == 0)
				cancellationToken.ThrowIfCancellationRequested();
			// Interpolation closers belong to a valid literal, not to its surrounding construction.
			if (!node.HasError && (node.Type.Contains("string", StringComparison.Ordinal) ||
				node.Type.Contains("literal", StringComparison.Ordinal)))
				continue;
			var children = node.Children;
			if (!children.Any())
				yield return node;
			else
				foreach (var child in children.Reverse()) pending.Push(child);
		}
	}
}
