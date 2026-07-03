namespace DevProjex.Kernel;

/// <summary>
/// Decides whether an already discovered file extension participates in effective tree/count scans.
/// This lets the scanner apply UI selection semantics in the first filesystem pass.
/// </summary>
public interface IExtensionInclusionPolicy
{
	bool AllowsExtension(string extension);

	bool AllowsExtension(ReadOnlySpan<char> extension);
}
