using DevProjex.Application.Secrets;

namespace DevProjex.Application.Services;

public static class OutputRootPathPresentation
{
	public const string LocalUserRuleId = "local-user";
	public const string LocalUserPlaceholder = "[local-user-1]";

	public static string Resolve(
		string rootPath,
		ExportPathPresentation? pathPresentation,
		ContentTransformationContext? transformationContext) =>
		ResolvePath(
			ResolveDisplayRootPath(rootPath, pathPresentation?.DisplayRootPath),
			CaptureRedactionDecision(transformationContext)).Text;

	public static string Resolve(
		string rootPath,
		ExportPathPresentation? pathPresentation,
		OutputPathRedactionDecision? redactionDecision) =>
		ResolvePath(
			ResolveDisplayRootPath(rootPath, pathPresentation?.DisplayRootPath),
			redactionDecision).Text;

	public static OutputPathPresentationResult ResolveWithRedaction(
		string rootPath,
		ExportPathPresentation? pathPresentation,
		OutputPathRedactionDecision? redactionDecision) =>
		ResolvePath(
			ResolveDisplayRootPath(rootPath, pathPresentation?.DisplayRootPath),
			redactionDecision);

	public static OutputPathRedactionDecision? CaptureRedactionDecision(
		ContentTransformationContext? transformationContext)
	{
		var redaction = transformationContext?.Redaction;
		if (redaction is null ||
		    !redaction.Features.HasFlag(SecretRedactionFeatures.PrivateData))
		{
			return null;
		}

		string normalizedProjectRoot;
		try
		{
			normalizedProjectRoot = Path.GetFullPath(redaction.ProjectRoot);
		}
		catch
		{
			normalizedProjectRoot = redaction.ProjectRoot;
		}

		var occurrenceId = SecretRedactionSession.HashValue(
			$"{normalizedProjectRoot}\ngenerated-output-path\n{LocalUserRuleId}".AsSpan());
		return new OutputPathRedactionDecision(
			occurrenceId,
			redaction.Session.IsKeptAsIs(occurrenceId));
	}

	public static OutputPathPresentationResult ResolvePath(
		string path,
		OutputPathRedactionDecision? redactionDecision)
	{
		if (redactionDecision is null ||
		    string.IsNullOrEmpty(path) ||
		    !TryFindLocalUserSegment(path.AsSpan(), out var start, out var sourceLength))
		{
			return new OutputPathPresentationResult(path);
		}

		if (redactionDecision.Keep)
		{
			return new OutputPathPresentationResult(
				path,
				redactionDecision.OccurrenceId,
				start,
				sourceLength,
				sourceLength,
				SecretPreviewSpanState.KeptAsIs);
		}

		var masked = string.Concat(
			path.AsSpan(0, start),
			LocalUserPlaceholder,
			path.AsSpan(start + sourceLength));
		return new OutputPathPresentationResult(
			masked,
			redactionDecision.OccurrenceId,
			start,
			LocalUserPlaceholder.Length,
			sourceLength,
			SecretPreviewSpanState.Redacted);
	}

	private static string ResolveDisplayRootPath(string rootPath, string? displayRootPath) =>
		string.IsNullOrWhiteSpace(displayRootPath) ? rootPath : displayRootPath;

	public static string Resolve(
		string rootPath,
		string? displayRootPath,
		bool hidePrivateData) =>
		ResolvePath(
			ResolveDisplayRootPath(rootPath, displayRootPath),
			hidePrivateData ? new OutputPathRedactionDecision(string.Empty, Keep: false) : null).Text;

	public static string MaskLocalUserSegment(string path)
	{
		if (string.IsNullOrEmpty(path) || !TryFindLocalUserSegment(path.AsSpan(), out var start, out var length))
			return path;

		return string.Concat(path.AsSpan(0, start), LocalUserPlaceholder, path.AsSpan(start + length));
	}

	internal static bool TryFindLocalUserSegment(
		ReadOnlySpan<char> path,
		out int start,
		out int length)
	{
		start = 0;
		length = 0;
		if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]))
		{
			var position = 3;
			if (!path[position..].StartsWith("Users", StringComparison.OrdinalIgnoreCase))
				return false;
			position += "Users".Length;
			if (position >= path.Length || !IsSeparator(path[position]))
				return false;
			start = position + 1;
		}
		else if (path.StartsWith("/home/", StringComparison.Ordinal))
		{
			start = "/home/".Length;
		}
		else if (path.StartsWith("/Users/", StringComparison.Ordinal))
		{
			start = "/Users/".Length;
		}
		else
		{
			return false;
		}

		var end = start;
		while (end < path.Length && !IsSeparator(path[end]))
			end++;
		var segment = path[start..end];
		if (end == start || segment.SequenceEqual(".") || segment.SequenceEqual(".."))
			return false;

		length = end - start;
		return true;
	}

	private static bool IsSeparator(char value) => value is '/' or '\\';
}

public sealed record OutputPathRedactionDecision(string OccurrenceId, bool Keep);

public readonly record struct OutputPathPresentationResult(
	string Text,
	string? OccurrenceId = null,
	int SegmentStart = 0,
	int SegmentLength = 0,
	int SourceLength = 0,
	SecretPreviewSpanState State = SecretPreviewSpanState.Redacted)
{
	public bool HasRedaction => !string.IsNullOrWhiteSpace(OccurrenceId) && SegmentLength > 0;
}
