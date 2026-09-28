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
	public void BoundedOrderingRetainsOnlyTheRequestedBestEntriesInStableOrder()
	{
		const int maximumCount = 1_001;
		var source = Enumerable.Range(0, 25_000)
			.Select(index =>
			{
				var name = $"entry-{25_000 - index:D5}";
				return new TerminalPathPickerEntry(
					$"/synthetic/{name}",
					name,
					IsDirectory: index % 3 == 0,
					IsParent: false);
			})
			.ToArray();
		var expected = source
			.OrderByDescending(static entry => entry.IsDirectory)
			.ThenBy(static entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
			.Take(maximumCount)
			.Select(static entry => entry.Path)
			.ToArray();

		var actual = TerminalPathPickerModel.TakeOrderedEntries(source, maximumCount);

		Assert.InRange(actual.Count, 0, maximumCount);
		Assert.Equal(expected, actual.Select(static entry => entry.Path));
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

	[Theory]
	[InlineData(TerminalPathPickerMode.Directory)]
	[InlineData(TerminalPathPickerMode.JsonFile)]
	internal void MissingTypedPathNeverFallsBackToTheCurrentOrSelectedEntry(
		TerminalPathPickerMode mode)
	{
		using var workspace = new TemporaryDirectory();
		workspace.WriteFile("settings.json", "{}");
		var model = new TerminalPathPickerModel(mode, workspace.Path);
		var fallbackIndex = mode == TerminalPathPickerMode.JsonFile
			? model.Entries
				.Select((entry, index) => (entry, index))
				.First(pair => !pair.entry.IsDirectory)
				.index
			: 0;

		var selection = model.ResolveSelection(
			Path.Combine(workspace.Path, "missing"),
			fallbackIndex);

		Assert.True(selection.InvalidTypedPath);
		Assert.Null(selection.Path);
	}

	[Fact]
	public void SettingsPickerBrowsesATypedFolderInsteadOfReportingItMissing()
	{
		using var workspace = new TemporaryDirectory();
		var child = workspace.CreateDirectory("child");
		var model = new TerminalPathPickerModel(TerminalPathPickerMode.JsonFile, workspace.Path);

		var selection = model.ResolveSelection(child + Path.DirectorySeparatorChar, selectedIndex: 0);

		Assert.False(selection.InvalidTypedPath);
		Assert.Null(selection.Path);
		Assert.Equal(child, selection.DirectoryToBrowse);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SettingsPickerOpensTheHighlightedEntryWhileThePathFieldShowsTheCurrentFolder(
		bool trailingSeparator)
	{
		using var workspace = new TemporaryDirectory();
		workspace.CreateDirectory("child");
		var settings = workspace.WriteFile("settings.json", "{}");
		var model = new TerminalPathPickerModel(TerminalPathPickerMode.JsonFile, workspace.Path);
		var settingsIndex = IndexOf(model, settings);
		var folderIndex = model.Entries
			.Select((entry, index) => (entry, index))
			.First(pair => pair.entry is { IsDirectory: true, IsParent: false })
			.index;
		var typed = trailingSeparator
			? model.CurrentDirectory + Path.DirectorySeparatorChar
			: model.CurrentDirectory;

		var fileSelection = model.ResolveSelection(typed, settingsIndex);
		var folderSelection = model.ResolveSelection(typed, folderIndex);

		Assert.Equal(new TerminalPathPickerSelection(settings, InvalidTypedPath: false), fileSelection);
		Assert.Equal(new TerminalPathPickerSelection(null, InvalidTypedPath: false), folderSelection);
	}

	[Fact]
	public void FolderPickerStillSelectsATypedFolder()
	{
		using var workspace = new TemporaryDirectory();
		var child = workspace.CreateDirectory("child");
		var model = new TerminalPathPickerModel(TerminalPathPickerMode.Directory, workspace.Path);

		var selection = model.ResolveSelection(child, selectedIndex: 0);

		Assert.Equal(new TerminalPathPickerSelection(child, InvalidTypedPath: false), selection);
	}

	private static int IndexOf(TerminalPathPickerModel model, string path) =>
		model.Entries
			.Select((entry, index) => (entry, index))
			.Single(pair => PathComparer.Default.Equals(pair.entry.Path, path))
			.index;
}
