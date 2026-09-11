namespace DevProjex.Mcp;

internal enum McpDetailLevel
{
	Full,
	Compact,
	Signatures
}

internal sealed record McpDetailResolution(
	McpDetailLevel Level,
	CodeTransformKinds Kinds)
{
	public string Token => McpDetailPolicy.ToToken(Level);
}

internal static class McpDetailPolicy
{
	public static McpDetailLevel Parse(string? token) => token switch
	{
		null or "full" => McpDetailLevel.Full,
		"compact" => McpDetailLevel.Compact,
		"signatures" => McpDetailLevel.Signatures,
		_ => throw new McpToolException(
			McpErrorCodes.InvalidArguments,
			$"{McpErrorCodes.InvalidArguments}: invalid detail '{token}'. Valid values: full, compact, signatures.")
	};

	public static McpDetailResolution Resolve(
		ProjectSelectionSpec selection,
		McpDetailLevel requested)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var profileKinds = CodeTransformIdentity.Resolve(
			selection.CompressCode == true,
			selection.StripComments == true,
			selection.StripBlankLines == true);
		var effectiveKinds = profileKinds | ResolveRequestedKinds(requested);
		return new McpDetailResolution(ResolveEffectiveLevel(effectiveKinds), effectiveKinds);
	}

	public static ProjectSelectionSpec Apply(
		ProjectSelectionSpec selection,
		McpDetailResolution resolution)
	{
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(resolution);
		return selection with
		{
			CompressCode = resolution.Kinds.HasFlag(CodeTransformKinds.Bodies),
			StripComments = resolution.Kinds.HasFlag(CodeTransformKinds.Comments),
			StripBlankLines = resolution.Kinds.HasFlag(CodeTransformKinds.BlankLines)
		};
	}

	public static string ToToken(McpDetailLevel level) => level switch
	{
		McpDetailLevel.Full => "full",
		McpDetailLevel.Compact => "compact",
		McpDetailLevel.Signatures => "signatures",
		_ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
	};

	/// <summary>
	/// The transformations a level asks for on its own, before the profile's own kinds are unioned
	/// in. Shared with the per-file overrides so one level token cannot mean two different things.
	/// </summary>
	public static CodeTransformKinds RequestedKinds(McpDetailLevel level) => ResolveRequestedKinds(level);

	private static CodeTransformKinds ResolveRequestedKinds(McpDetailLevel level) => level switch
	{
		McpDetailLevel.Full => CodeTransformIdentity.Resolve(false, false, false),
		McpDetailLevel.Compact => CodeTransformIdentity.Resolve(false, true, true),
		McpDetailLevel.Signatures => CodeTransformIdentity.Resolve(true, true, true),
		_ => throw new ArgumentOutOfRangeException(nameof(level), level, null)
	};

	// One tier mapping in the product: the reporting types own it, and this reuses it rather than
	// keeping a second copy that can drift.
	private static McpDetailLevel ResolveEffectiveLevel(CodeTransformKinds kinds) =>
		ContentDetailLevelTokens.FromKinds(kinds) switch
		{
			ContentDetailLevel.Signatures => McpDetailLevel.Signatures,
			ContentDetailLevel.Compact => McpDetailLevel.Compact,
			_ => McpDetailLevel.Full
		};
}
