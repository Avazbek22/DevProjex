namespace DevProjex.Avalonia.Coordinators;

public sealed partial class SelectionSyncCoordinator
{
    private sealed record LiveRefreshInput(
        SelectionRefreshContext Context);

    internal sealed class AppliedSelectionState(
        string projectPath,
        HashSet<string> selectedExtensions,
        HashSet<IgnoreOptionId> selectedIgnoreOptions,
        IReadOnlyDictionary<string, bool> extensionOptionStates,
        IReadOnlyDictionary<IgnoreOptionId, bool> ignoreOptionStates)
    {
        public static AppliedSelectionState Capture(
            string projectPath,
            MainWindowViewModel viewModel,
            IReadOnlyDictionary<string, bool>? cachedExtensionStates,
            IReadOnlyDictionary<IgnoreOptionId, bool>? cachedIgnoreOptionStates)
        {
            var extensionStates = cachedExtensionStates is null
                ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(cachedExtensionStates, StringComparer.OrdinalIgnoreCase);
            foreach (var option in viewModel.Extensions)
                extensionStates[option.Name] = option.IsChecked;

            var ignoreStates = cachedIgnoreOptionStates is null
                ? new Dictionary<IgnoreOptionId, bool>()
                : new Dictionary<IgnoreOptionId, bool>(cachedIgnoreOptionStates);
            foreach (var option in viewModel.IgnoreOptions)
                ignoreStates[option.Id] = option.IsChecked;

            var extensions = viewModel.Extensions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ignoreOptions = viewModel.IgnoreOptions
                .Where(static option => option.IsChecked)
                .Select(static option => option.Id)
                .ToHashSet();

            return new AppliedSelectionState(
                projectPath,
                extensions,
                ignoreOptions,
                extensionStates,
                ignoreStates);
        }

        public AppliedSelectionPersistenceSnapshot CreatePersistenceSnapshot() => new(
            extensionOptionStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            ignoreOptionStates
                .Where(static pair => pair.Value)
                .Select(static pair => pair.Key)
                .ToHashSet(),
            extensionOptionStates,
            ignoreOptionStates);

        public bool IsForProject(string? currentProjectPath) =>
            !string.IsNullOrWhiteSpace(currentProjectPath) &&
            PathComparer.Default.Equals(projectPath, currentProjectPath);

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

        public bool MatchesExceptIgnoreOption(
            string? currentProjectPath,
            MainWindowViewModel viewModel,
            IReadOnlyDictionary<string, bool>? currentExtensionStates,
            IReadOnlyDictionary<IgnoreOptionId, bool>? currentIgnoreStates,
            IgnoreOptionId ignoredOption)
            => MatchesExceptIgnoreOptions(
                currentProjectPath,
                viewModel,
                currentExtensionStates,
                currentIgnoreStates,
                [ignoredOption]);

        public bool MatchesExceptIgnoreOptions(
            string? currentProjectPath,
            MainWindowViewModel viewModel,
            IReadOnlyDictionary<string, bool>? currentExtensionStates,
            IReadOnlyDictionary<IgnoreOptionId, bool>? currentIgnoreStates,
            IReadOnlyCollection<IgnoreOptionId> ignoredOptions)
        {
            if (string.IsNullOrWhiteSpace(currentProjectPath) ||
                !PathComparer.Default.Equals(projectPath, currentProjectPath))
            {
                return false;
            }

            return MatchesSelectedExtensions(viewModel.Extensions) &&
                   MatchesSelectedIgnoreOptionsExcept(viewModel.IgnoreOptions, ignoredOptions) &&
                   DictionaryStatesMatch(extensionOptionStates, currentExtensionStates) &&
                   DictionaryStatesMatchExcept(ignoreOptionStates, currentIgnoreStates, ignoredOptions);
        }

        public bool MatchesExceptContentTransformations(
            string? currentProjectPath,
            MainWindowViewModel viewModel,
            IReadOnlyDictionary<string, bool>? currentExtensionStates,
            IReadOnlyDictionary<IgnoreOptionId, bool>? currentIgnoreStates)
        {
            if (string.IsNullOrWhiteSpace(currentProjectPath) ||
                !PathComparer.Default.Equals(projectPath, currentProjectPath))
            {
                return false;
            }

            return MatchesSelectedExtensions(viewModel.Extensions) &&
                   MatchesSelectedIgnoreOptionsExcept(
                       viewModel.IgnoreOptions,
                       ProjectPresentationCatalog.ContentTransformationOptionIds) &&
                   DictionaryStatesMatch(extensionOptionStates, currentExtensionStates) &&
                   DictionaryStatesMatchExcept(
                       ignoreOptionStates,
                       currentIgnoreStates,
                       ProjectPresentationCatalog.ContentTransformationOptionIds);
        }

        public bool HasDifferentIgnoreOption(
            IReadOnlyCollection<IgnoreOptionViewModel> options,
            IgnoreOptionId optionId)
        {
            var current = options.FirstOrDefault(option => option.Id == optionId)?.IsChecked == true;
            return current != selectedIgnoreOptions.Contains(optionId);
        }

        public AppliedSelectionState WithIgnoreOption(IgnoreOptionId optionId, bool isChecked)
        {
            var selected = new HashSet<IgnoreOptionId>(selectedIgnoreOptions);
            if (isChecked)
                selected.Add(optionId);
            else
                selected.Remove(optionId);

            var states = new Dictionary<IgnoreOptionId, bool>(ignoreOptionStates)
            {
                [optionId] = isChecked
            };
            return new AppliedSelectionState(
                projectPath,
                new HashSet<string>(selectedExtensions, StringComparer.OrdinalIgnoreCase),
                selected,
                extensionOptionStates,
                states);
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

        private bool MatchesSelectedIgnoreOptionsExcept(
            IReadOnlyCollection<IgnoreOptionViewModel> options,
            IgnoreOptionId ignoredOption)
            => MatchesSelectedIgnoreOptionsExcept(options, new[] { ignoredOption });

        private bool MatchesSelectedIgnoreOptionsExcept(
            IReadOnlyCollection<IgnoreOptionViewModel> options,
            IReadOnlyCollection<IgnoreOptionId> ignoredOptions)
        {
            var selectedCount = 0;
            foreach (var option in options)
            {
                if (!option.IsChecked || ignoredOptions.Contains(option.Id))
                    continue;

                selectedCount++;
                if (!selectedIgnoreOptions.Contains(option.Id))
                    return false;
            }

            var expectedCount = selectedIgnoreOptions.Count(option => !ignoredOptions.Contains(option));

            return selectedCount == expectedCount;
        }

        private static bool DictionaryStatesMatch<TKey>(
            IReadOnlyDictionary<TKey, bool> appliedStates,
            IReadOnlyDictionary<TKey, bool>? currentStates)
            where TKey : notnull
        {
            if (currentStates is null || appliedStates.Count != currentStates.Count)
                return false;

            foreach (var (key, appliedValue) in appliedStates)
            {
                if (!currentStates.TryGetValue(key, out var currentValue) ||
                    currentValue != appliedValue)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool DictionaryStatesMatchExcept<TKey>(
            IReadOnlyDictionary<TKey, bool> appliedStates,
            IReadOnlyDictionary<TKey, bool>? currentStates,
            IReadOnlyCollection<TKey> ignoredKeys)
            where TKey : notnull
        {
            if (currentStates is null)
                return false;

            var appliedCount = appliedStates.Count(pair => !ignoredKeys.Contains(pair.Key));
            var currentCount = currentStates.Count(pair => !ignoredKeys.Contains(pair.Key));
            if (appliedCount != currentCount)
                return false;

            foreach (var (key, appliedValue) in appliedStates)
            {
                if (ignoredKeys.Contains(key))
                    continue;
                if (!currentStates.TryGetValue(key, out var currentValue) ||
                    currentValue != appliedValue)
                {
                    return false;
                }
            }

            return true;
        }
    }

    internal sealed record AppliedSelectionPersistenceSnapshot(
        IReadOnlySet<string> SelectedExtensions,
        IReadOnlySet<IgnoreOptionId> SelectedIgnoreOptions,
        IReadOnlyDictionary<string, bool> ExtensionOptionStates,
        IReadOnlyDictionary<IgnoreOptionId, bool> IgnoreOptionStates);
}
