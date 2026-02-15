using DevProjex.Kernel.Models;

namespace DevProjex.Kernel.Abstractions;

public interface ITreeBuilder
{
	TreeBuildResult Build(string rootPath, TreeFilterOptions options, CancellationToken cancellationToken = default);
}
