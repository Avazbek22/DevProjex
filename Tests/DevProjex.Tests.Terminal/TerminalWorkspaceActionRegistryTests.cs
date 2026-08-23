namespace DevProjex.Tests.Terminal;

public sealed class TerminalWorkspaceActionRegistryTests
{
	[Fact]
	public void EveryCatalogCommandHasExactlyOneExecutableHandler()
	{
		var executed = new List<string>();
		var registry = new TerminalWorkspaceActionRegistry(
			[],
			TerminalWorkspaceCommandCatalog.All.Select(definition =>
				new TerminalWorkspaceCommandAction(
					definition,
					IsAvailable: static () => true,
					Execute: command =>
					{
						executed.Add(command.Definition.Id);
						return TerminalWorkspaceCommandExecutionResult.Success();
					})));
		var parser = new TerminalWorkspaceCommandParser();
		var context = new TerminalWorkspaceCommandParseContext([".cs"]);

		foreach (var definition in TerminalWorkspaceCommandCatalog.All)
		{
			var parsed = parser.Parse(definition.Example, context);
			Assert.True(parsed.IsSuccess);
			Assert.Equal(
				TerminalWorkspaceCommandExecutionStatus.Success,
				registry.Execute(parsed.Command!).Status);
		}

		Assert.Equal(
			TerminalWorkspaceCommandCatalog.All.Select(static definition => definition.Id),
			executed);
	}

	[Fact]
	public void UnavailableActionDoesNotInvokeItsHandler()
	{
		var invoked = false;
		var target = TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Set);
		var registry = CreateRegistry(target, () => false, _ =>
		{
			invoked = true;
			return TerminalWorkspaceCommandExecutionResult.Success();
		});
		var command = new TerminalWorkspaceCommand(target, Target: "hide-secrets", Enabled: true);

		var result = registry.Execute(command);

		Assert.Equal(TerminalWorkspaceCommandExecutionStatus.Unavailable, result.Status);
		Assert.False(invoked);
	}

	[Theory]
	[InlineData(TerminalWorkspaceCommandVerb.Branch)]
	[InlineData(TerminalWorkspaceCommandVerb.Update)]
	internal void GitCloneOnlyActionReturnsItsContextSpecificUnavailableReason(
		TerminalWorkspaceCommandVerb verb)
	{
		var target = TerminalWorkspaceCommandCatalog.Get(verb);
		var registry = new TerminalWorkspaceActionRegistry(
			[],
			TerminalWorkspaceCommandCatalog.All.Select(definition =>
				new TerminalWorkspaceCommandAction(
					definition,
					definition == target ? static () => false : static () => true,
					static _ => TerminalWorkspaceCommandExecutionResult.Success(),
					definition == target ? static () => "Git clone required" : null)));

		var result = registry.Execute(new TerminalWorkspaceCommand(target));

		Assert.Equal(TerminalWorkspaceCommandExecutionStatus.Unavailable, result.Status);
		Assert.Equal("Git clone required", result.Message);
	}

	[Fact]
	public void RegistryRejectsAMissingCommandHandler()
	{
		var commands = TerminalWorkspaceCommandCatalog.All
			.Skip(1)
			.Select(static definition => new TerminalWorkspaceCommandAction(
				definition,
				static () => true,
				static _ => TerminalWorkspaceCommandExecutionResult.Success()));

		var exception = Assert.Throws<ArgumentException>(() =>
			new TerminalWorkspaceActionRegistry([], commands));

		Assert.Contains("workspace.command.set", exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void RegistryRejectsPaletteItemsWithoutStableUniqueIds()
	{
		var item = new TerminalPaletteItem(
			"duplicate",
			"category",
			"title",
			"description",
			string.Empty,
			null,
			null,
			null,
			static () => true,
			static () => { });

		Assert.Throws<ArgumentException>(() =>
			new TerminalWorkspaceActionRegistry([item, item], CreateDefaultCommands()));
	}

	[Fact]
	public void RegistryRejectsPaletteSyntaxWithoutARegisteredCommandBinding()
	{
		var item = CreatePaletteItem(
			"workspace.palette.invalid",
			"set <option> <on|off>",
			"workspace.command.missing",
			static () => true,
			static () => { });

		var exception = Assert.Throws<ArgumentException>(() =>
			new TerminalWorkspaceActionRegistry([item], CreateDefaultCommands()));

		Assert.Contains(item.Id, exception.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PaletteCommandBindingUsesTheCommandAvailabilityGateAndHandler()
	{
		var paletteInvoked = false;
		var commandAvailable = false;
		var definition = TerminalWorkspaceCommandCatalog.Get(TerminalWorkspaceCommandVerb.Set);
		var item = CreatePaletteItem(
			"workspace.palette.settings",
			definition.Syntax,
			definition.Id,
			static () => true,
			() => paletteInvoked = true);
		var commands = TerminalWorkspaceCommandCatalog.All.Select(candidate =>
			new TerminalWorkspaceCommandAction(
				candidate,
				candidate == definition ? () => commandAvailable : static () => true,
				static _ => TerminalWorkspaceCommandExecutionResult.Success()));
		var registry = new TerminalWorkspaceActionRegistry([item], commands);

		Assert.Equal(
			TerminalWorkspaceCommandExecutionStatus.Unavailable,
			registry.Execute(item).Status);
		Assert.False(paletteInvoked);

		commandAvailable = true;
		Assert.Equal(
			TerminalWorkspaceCommandExecutionStatus.Success,
			registry.Execute(item).Status);
		Assert.True(paletteInvoked);
	}

	[Fact]
	public void EveryBoundPaletteEntryRoundTripsThroughItsCommandDefinition()
	{
		var parser = new TerminalWorkspaceCommandParser();
		var context = new TerminalWorkspaceCommandParseContext([".cs"]);

		foreach (var definition in TerminalWorkspaceCommandCatalog.All)
		{
			var item = CreatePaletteItem(
				$"workspace.palette.{definition.Token}",
				definition.Syntax,
				definition.Id,
				static () => true,
				static () => { });
			var parsed = parser.Parse(definition.Example, context);

			Assert.True(parsed.IsSuccess, definition.Syntax);
			Assert.Equal(item.CommandId, parsed.Command!.Definition.Id);
		}
	}

	private static TerminalWorkspaceActionRegistry CreateRegistry(
		TerminalWorkspaceCommandDefinition target,
		Func<bool> isAvailable,
		Func<TerminalWorkspaceCommand, TerminalWorkspaceCommandExecutionResult> execute) =>
		new(
			[],
			TerminalWorkspaceCommandCatalog.All.Select(definition =>
				new TerminalWorkspaceCommandAction(
					definition,
					definition == target ? isAvailable : static () => true,
					definition == target
						? execute
						: static _ => TerminalWorkspaceCommandExecutionResult.Success())));

	private static IEnumerable<TerminalWorkspaceCommandAction> CreateDefaultCommands() =>
		TerminalWorkspaceCommandCatalog.All.Select(static definition =>
			new TerminalWorkspaceCommandAction(
				definition,
				static () => true,
				static _ => TerminalWorkspaceCommandExecutionResult.Success()));

	private static TerminalPaletteItem CreatePaletteItem(
		string id,
		string? syntax,
		string? commandId,
		Func<bool> isAvailable,
		Action execute) =>
		new(
			id,
			"category",
			"title",
			"description",
			string.Empty,
			null,
			syntax,
			commandId,
			isAvailable,
			execute);
}
