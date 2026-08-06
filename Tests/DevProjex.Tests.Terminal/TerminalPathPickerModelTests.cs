namespace DevProjex.Tests.Terminal;

public sealed class TerminalPathPickerModelTests
{
	[Fact]
	public void DirectoryPickerShowsFoldersOnlyAndSelectsCurrentFolder()
	{
		using var workspace = new TemporaryDirectory();
		var child = workspace.CreateDirectory("child");
		workspace.WriteFile("settings.json", "{}");
		workspace.WriteFile("notes.txt", "text");

		var model = new TerminalPathPickerModel(
			TerminalPathPickerMode.Directory,
			workspace.Path);

		Assert.Equal(Path.GetFullPath(workspace.Path), model.CurrentDirectory);
		Assert.Contains(model.Entries, entry =>
			entry.IsDirectory &&
			PathComparer.Default.Equals(entry.Path, child));
		Assert.DoesNotContain(model.Entries, static entry => !entry.IsDirectory);
		Assert.Equal(Path.GetFullPath(workspace.Path), model.SelectCurrentDirectory());
	}

	[Fact]
	public void SettingsPickerShowsJsonFilesAndNavigatesWithoutSelectingDirectory()
	{
		using var workspace = new TemporaryDirectory();
		var child = workspace.CreateDirectory("child");
		var settings = workspace.WriteFile("settings.JSON", "{}");
		workspace.WriteFile("notes.txt", "text");
		var model = new TerminalPathPickerModel(
			TerminalPathPickerMode.JsonFile,
			workspace.Path);

		var fileIndex = model.Entries
			.Select((entry, index) => (entry, index))
			.Single(pair => PathComparer.Default.Equals(pair.entry.Path, settings))
			.index;
		Assert.Equal(settings, model.SelectEntry(fileIndex));
		Assert.DoesNotContain(model.Entries, entry => entry.Name == "notes.txt");

		var directoryIndex = model.Entries
			.Select((entry, index) => (entry, index))
			.Single(pair => PathComparer.Default.Equals(pair.entry.Path, child))
			.index;
		Assert.True(model.TryOpenEntry(directoryIndex, out var selected));
		Assert.Null(selected);
		Assert.Equal(child, model.CurrentDirectory);
	}

	[Fact]
	public void PickerBoundsHugeDirectoriesWithoutDroppingParentNavigation()
	{
		using var workspace = new TemporaryDirectory();
		for (var index = 0; index < 1_010; index++)
			workspace.WriteFile($"entry-{index:D4}.json", "{}");

		var model = new TerminalPathPickerModel(
			TerminalPathPickerMode.JsonFile,
			workspace.Path);

		Assert.True(model.IsTruncated);
		Assert.Equal(1_001, model.Entries.Count);
		Assert.True(model.Entries[0].IsParent);
		Assert.Equal(1_000, model.Entries.Count(static entry => !entry.IsParent));
	}

	[Fact]
	public void MissingInitialPathFallsBackToCurrentDirectory()
	{
		using var workspace = new TemporaryDirectory();
		var expectedCurrentDirectory = Directory.GetCurrentDirectory();
		var model = new TerminalPathPickerModel(
			TerminalPathPickerMode.Directory,
			Path.Combine(workspace.Path, "missing"));

		Assert.Equal(expectedCurrentDirectory, model.CurrentDirectory);
		Assert.Equal(TerminalPathPickerError.None, model.Error);
	}
}
