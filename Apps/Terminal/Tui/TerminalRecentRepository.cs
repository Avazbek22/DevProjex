using DevProjex.Terminal.Execution;
using DevProjex.Terminal.Rendering;

namespace DevProjex.Terminal.Tui;

internal sealed record TerminalRecentRepository(
	string Url,
	DateTimeOffset OpenedUtc,
	CachedRepository Cache)
{
	public string Name => Cache.RepositoryName;
	public string SafeDisplayUrl => TerminalTextEscaping.EscapeSingleLine(
		RepositoryUrlUtility.ToSafeDisplay(Url));
}

internal sealed class TerminalRecentRepositoryRow(TerminalRecentRepository repository)
{
	public TerminalRecentRepository Repository { get; } = repository;
	public bool IsSelected { get; set; }

	public override string ToString()
	{
		var state = Repository.Cache.State switch
		{
			RepositoryCacheState.Ready => "[+]",
			RepositoryCacheState.Damaged => "[!]",
			_ => "[-]"
		};
		return $"{(IsSelected ? ">" : " ")} {state} " +
		       TerminalTextEscaping.EscapeSingleLine(Repository.Name);
	}
}

internal enum TerminalRecentRepositoryDecisionKind
{
	Back,
	Open,
	Remove
}

internal sealed record TerminalRecentRepositoryDecision(
	TerminalRecentRepositoryDecisionKind Kind,
	TerminalRecentRepository? Repository = null);
