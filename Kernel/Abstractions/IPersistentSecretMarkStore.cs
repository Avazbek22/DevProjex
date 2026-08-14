namespace DevProjex.Kernel.Abstractions;

public interface IPersistentSecretMarkStore
{
	ValueTask<PersistentSecretMarksLoadResult> LoadMarksAsync(
		string localProjectPath,
		CancellationToken cancellationToken = default);

	ValueTask<PersistentSecretMarkWriteResult> AddMarkAsync(
		string localProjectPath,
		MarkedSecretProfileEntry mark,
		CancellationToken cancellationToken = default);

	ValueTask<PersistentSecretMarkWriteResult> RemoveMarkAsync(
		string localProjectPath,
		PersistentSecretMarkId markId,
		CancellationToken cancellationToken = default);

	ValueTask<PersistentSecretMarkWriteResult> ApplyMarkDeltaAsync(
		string localProjectPath,
		PersistentSecretMarkDelta delta,
		CancellationToken cancellationToken = default);
}
