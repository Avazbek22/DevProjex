namespace DevProjex.Infrastructure.Git;

public sealed record GitBranchListResult(
	IReadOnlyList<GitBranch> Branches,
	bool IsIncomplete);

internal sealed class GitBranchLineCollector(
	int maximumRecords,
	int maximumRecordCharacters,
	int maximumCharacters)
{
	private readonly List<string> _lines = new(Math.Min(maximumRecords, 256));
	private int _characters;

	public IReadOnlyList<string> Lines => _lines;
	public bool IsIncomplete { get; private set; }

	public void Add(GitProcessLineFrame frame)
	{
		if (frame.ExceededLimit || frame.Text.Length > maximumRecordCharacters)
		{
			IsIncomplete = true;
			return;
		}
		if (string.IsNullOrWhiteSpace(frame.Text))
			return;
		if (_lines.Count >= maximumRecords || frame.Text.Length > maximumCharacters - _characters)
		{
			IsIncomplete = true;
			return;
		}

		_lines.Add(frame.Text);
		_characters += frame.Text.Length;
	}
}

internal sealed record GitBranchCommandResult(
	int ExitCode,
	IReadOnlyList<string> Lines,
	bool IsIncomplete,
	string Error)
{
	public static GitBranchCommandResult Failed(string error) => new(-1, [], true, error);
}
