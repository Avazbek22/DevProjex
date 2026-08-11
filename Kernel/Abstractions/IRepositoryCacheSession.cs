namespace DevProjex.Kernel.Abstractions;

/// <summary>
/// Pins one immutable cache checkout for the lifetime of a consuming session.
/// </summary>
public interface IRepositoryCacheSession : IDisposable
{
	string RepositoryPath { get; }
	string RepositoryUrl { get; }
	string? Branch { get; }
	RepositoryCacheContentKind ContentKind { get; }
}
