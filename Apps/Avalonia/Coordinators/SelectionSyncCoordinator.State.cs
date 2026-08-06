namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
    private sealed record LiveRefreshInput(
        SelectionRefreshContext Context);

    internal sealed class AppliedSelectionState(
        string projectPath,
        HashSet<string> selectedExtensions,
        HashSet<IgnoreOptionId> selectedIgnoreOptions)
    {
        public static AppliedSelectionState Capture(string projectPath, MainWindowViewModel viewModel)
        {
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var option in viewModel.Extensions)
            {
                if (option.IsChecked)
                    extensions.Add(option.Name);
            }

            var ignoreOptions = new HashSet<IgnoreOptionId>();
            foreach (var option in viewModel.IgnoreOptions)
            {
                if (option.IsChecked)
                    ignoreOptions.Add(option.Id);
            }

            return new AppliedSelectionState(projectPath, extensions, ignoreOptions);
        }

        public bool Matches(string? currentProjectPath, MainWindowViewModel viewModel)
        {
            if (string.IsNullOrWhiteSpace(currentProjectPath) ||
                !PathComparer.Default.Equals(projectPath, currentProjectPath))
            {
                return false;
            }

            return MatchesSelectedExtensions(viewModel.Extensions) &&
                   MatchesSelectedIgnoreOptions(viewModel.IgnoreOptions);
        }

        private bool MatchesSelectedExtensions(IReadOnlyCollection<SelectionOptionViewModel> options)
        {
            var selectedCount = 0;
            foreach (var option in options)
            {
                if (!option.IsChecked)
                    continue;

                selectedCount++;
                if (!selectedExtensions.Contains(option.Name))
                    return false;
            }

            return selectedCount == selectedExtensions.Count;
        }

        private bool MatchesSelectedIgnoreOptions(IReadOnlyCollection<IgnoreOptionViewModel> options)
        {
            var selectedCount = 0;
            foreach (var option in options)
            {
                if (!option.IsChecked)
                    continue;

                selectedCount++;
                if (!selectedIgnoreOptions.Contains(option.Id))
                    return false;
            }

            return selectedCount == selectedIgnoreOptions.Count;
        }
    }
}
