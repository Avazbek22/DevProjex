using DevProjex.Application.DesktopControl;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class MainWindowArchitectureContractTests
{
	public static TheoryData<string, Type> CoordinatorFields =>
		new()
		{
			{
				"_searchFilterController",
				typeof(SearchFilterInteractionController)
			},
			{
				"_workspacePresentation",
				typeof(WorkspacePresentationController)
			},
			{
				"_previewSurfaceController",
				typeof(PreviewSurfaceController)
			},
			{
				"_previewWorkspaceController",
				typeof(PreviewWorkspaceController)
			},
			{
				"_startupInteractions",
				typeof(StartupInteractionController)
			},
			{
				"_memoryCleanup",
				typeof(MemoryCleanupCoordinator)
			},
			{
				"_treeViewport",
				typeof(TreeViewportController)
			},
			{
				"_appearanceSettings",
				typeof(AppearanceSettingsController)
			}
		};

	[Theory]
	[MemberData(nameof(CoordinatorFields))]
	public void MainWindow_CoordinatorReferencesAreReadonlyAndTyped(
		string fieldName,
		Type expectedType)
	{
		var field = typeof(MainWindow).GetField(
			fieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.NotNull(field);
		Assert.Equal(expectedType, field!.FieldType);
		Assert.True(field.IsInitOnly);
	}

	[Theory]
	[InlineData("_previewSelectionMetricsCts")]
	[InlineData("_previewSelectionMetricsDebounceTimer")]
	[InlineData("_previewModeSwitchCts")]
	[InlineData("_previewMemoryCleanupCts")]
	[InlineData("_searchMemoryCleanupCts")]
	[InlineData("_backgroundMemoryCleanupCts")]
	[InlineData("_themePresetSession")]
	[InlineData("_currentThemeVariant")]
	[InlineData("_currentEffectMode")]
	public void MainWindow_DoesNotReacquireSubsystemState(
		string legacyFieldName)
	{
		var field = typeof(MainWindow).GetField(
			legacyFieldName,
			BindingFlags.Instance | BindingFlags.NonPublic);

		Assert.Null(field);
	}

	[Theory]
	[MemberData(nameof(CoordinatorFields))]
	public void UiCoordinators_DoNotDependOnConcreteMainWindow(
		string _,
		Type coordinatorType)
	{
		var fields = coordinatorType.GetFields(
			BindingFlags.Instance |
			BindingFlags.Public |
			BindingFlags.NonPublic);
		var constructorParameters = coordinatorType
			.GetConstructors(
				BindingFlags.Instance |
				BindingFlags.Public |
				BindingFlags.NonPublic)
			.SelectMany(static constructor =>
				constructor.GetParameters());

		Assert.DoesNotContain(
			fields,
			static field => field.FieldType == typeof(MainWindow));
		Assert.DoesNotContain(
			constructorParameters,
			static parameter =>
				parameter.ParameterType == typeof(MainWindow));
	}

	[Theory]
	[InlineData(TreeTextFormat.Ascii, ExportFormat.Ascii)]
	[InlineData(TreeTextFormat.Json, ExportFormat.Json)]
	[InlineData(TreeTextFormat.Xml, ExportFormat.Xml)]
	[InlineData(TreeTextFormat.Markdown, ExportFormat.Markdown)]
	public void StartupInteractionController_MapsEveryTreeFormat(
		TreeTextFormat source,
		ExportFormat expected)
	{
		Assert.Equal(
			expected,
			StartupInteractionController.MapTreeFormat(source));
	}

	[Theory]
	[InlineData(DesktopPreviewView.Tree, PreviewContentMode.Tree)]
	[InlineData(
		DesktopPreviewView.Content,
		PreviewContentMode.Content)]
	[InlineData(
		DesktopPreviewView.TreeContent,
		PreviewContentMode.TreeAndContent)]
	public void StartupInteractionController_MapsEveryPreviewMode(
		DesktopPreviewView source,
		PreviewContentMode expected)
	{
		Assert.Equal(
			expected,
			StartupInteractionController.MapPreviewMode(source));
	}

	[Fact]
	public void DesktopStatus_UsesTheSharedGitModeTokenFormatter()
	{
		var source = File.ReadAllText(Path.Combine(
			FindRepositoryRoot(),
			"Apps",
			"Avalonia",
			"MainWindow.DesktopControl.cs"));
		var methodStart = source.IndexOf(
			"private DesktopInteractionResult SuccessState()",
			StringComparison.Ordinal);
		var methodEnd = source.IndexOf(
			"private ContextDiagnostic? GetDesktopGitReadinessDiagnostic",
			methodStart,
			StringComparison.Ordinal);
		Assert.True(methodStart >= 0 && methodEnd > methodStart);
		var method = source[methodStart..methodEnd];

		Assert.Contains("GitScopeSelection.ToToken(", method, StringComparison.Ordinal);
		Assert.Contains("AppliedGitDiffRange", method, StringComparison.Ordinal);
		Assert.DoesNotContain("AppliedGitReadiness.Mode switch", method, StringComparison.Ordinal);
	}

	private static string FindRepositoryRoot()
	{
		var current = new DirectoryInfo(AppContext.BaseDirectory);
		while (current is not null)
		{
			if (File.Exists(Path.Combine(current.FullName, "DevProjex.sln")))
			{
				return current.FullName;
			}

			current = current.Parent;
		}

		throw new DirectoryNotFoundException("Repository root was not found.");
	}
}
