namespace DevProjex.Application.Context;

/// <summary>
/// Turns a resolved selection plus an optional call-level detail request into one transformation
/// policy. Every surface that builds a transformation context goes through here, so the MCP tools,
/// the document writer and the command line cannot disagree about a file's effective kinds - which
/// is what makes the admitted set of a measurement and the admitted set of a pack comparable.
/// </summary>
public static class ContentDetailSelection
{
	/// <summary>The transformations the resolved selection already carries.</summary>
	public static CodeTransformKinds ResolveSelectionKinds(ProjectSelectionSpec selection)
	{
		ArgumentNullException.ThrowIfNull(selection);
		return CodeTransformIdentity.Resolve(
			selection.CompressCode == true,
			selection.StripComments == true,
			selection.StripBlankLines == true);
	}

	/// <summary>
	/// The policy for one operation, or null when a single detail level applies to every file.
	/// A null result keeps callers on their existing single-kinds path, byte for byte.
	/// </summary>
	public static ContentDetailPolicy? Resolve(
		ProjectSelectionSpec selection,
		CodeTransformKinds requestedKinds = CodeTransformKinds.None)
	{
		ArgumentNullException.ThrowIfNull(selection);
		if (selection.ContentDetailOverrides is not { Count: > 0 } overrides)
			return null;

		var selectionKinds = ResolveSelectionKinds(selection);
		// A selection that never crossed the resolver has no separate record of what the profile asked
		// for; its own kinds are the closest honest answer. The share is also clamped to what the
		// selection resolved to: an override must never be able to ADD a transformation that no
		// unmatched file receives, which is the inverse of the rule it exists to protect.
		var profileKinds = (selection.ProfileContentKinds ?? selectionKinds) & selectionKinds;
		return new ContentDetailPolicy(
			profileKinds,
			selectionKinds | requestedKinds,
			overrides);
	}

	/// <summary>
	/// Every kind the operation can request, which is what gates creating a compression context.
	/// Gating on the default level instead would silently skip compression whenever the default is
	/// <c>full</c> and only overrides ask for a reduction - the ordinary shape of a mixed call.
	/// </summary>
	public static CodeTransformKinds ResolveContextKinds(
		ProjectSelectionSpec selection,
		CodeTransformKinds requestedKinds = CodeTransformKinds.None)
	{
		var policy = Resolve(selection, requestedKinds);
		return policy is null
			? ResolveSelectionKinds(selection) | requestedKinds
			: policy.UnionKinds;
	}
}
