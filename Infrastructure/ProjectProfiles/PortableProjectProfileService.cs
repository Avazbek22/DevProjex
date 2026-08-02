using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using DevProjex.Application.Context;
using DevProjex.Application.Services;
using DevProjex.Infrastructure.Persistence;

namespace DevProjex.Infrastructure.ProjectProfiles;

public sealed record PortableProfileValidationResult(
	bool IsValid,
	ProjectSelectionSpec? Selection,
	IReadOnlyList<string> Errors);

public sealed class PortableProjectProfileException(string code, string message, Exception? innerException = null)
	: Exception(message, innerException)
{
	public string Code { get; } = code;
}

public sealed class PortableProjectProfileService
{
	public const int CurrentSchemaVersion = 1;
	public const string DocumentKind = "devprojex-profile";

	private static readonly JsonSerializerOptions ReadOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = false
	};

	private static readonly JsonSerializerOptions WriteOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	public async Task<ProjectSelectionSpec> LoadAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		var fullPath = NormalizeProfilePath(path);
		try
		{
			await using var stream = new FileStream(
				fullPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				16 * 1024,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			var document = await JsonSerializer
				.DeserializeAsync<PortableProfileDocument>(stream, ReadOptions, cancellationToken)
				.ConfigureAwait(false);
			return ValidateAndConvert(document, fullPath);
		}
		catch (PortableProjectProfileException)
		{
			throw;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is
			       IOException or
			       UnauthorizedAccessException or
			       JsonException or
			       NotSupportedException)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"The portable profile could not be read.",
				exception);
		}
	}

	public async Task<string> SaveAsync(
		string sourceRoot,
		string path,
		ProjectSelectionSpec selection,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var document = ToDocument(selection);
		try
		{
			var requestedPath = NormalizeProfilePath(path);
			_ = ExactFileOutputDestinationPolicy.Resolve(
				sourceRoot,
				requestedPath,
				overwrite);
			return await AtomicFileOutput.WriteAsync(
					requestedPath,
					overwrite,
					(stream, token) =>
						JsonSerializer.SerializeAsync(
							stream,
							document,
							WriteOptions,
							token),
					cancellationToken,
					candidate => ExactFileOutputDestinationPolicy.Resolve(
						sourceRoot,
						candidate,
						overwrite))
				.ConfigureAwait(false);
		}
		catch (PortableProjectProfileException)
		{
			throw;
		}
		catch (AtomicFileOutputConflictException exception)
		{
			throw new PortableProjectProfileException(
				"DPX-PROFILE-DESTINATION-EXISTS",
				"The portable profile destination already exists.",
				exception);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-WRITE-FAILED",
				"The portable profile could not be written.",
				exception);
		}
	}

	public async Task<PortableProfileValidationResult> ValidateAsync(
		string path,
		CancellationToken cancellationToken = default)
	{
		try
		{
			var selection = await LoadAsync(path, cancellationToken).ConfigureAwait(false);
			return new PortableProfileValidationResult(true, selection, []);
		}
		catch (PortableProjectProfileException exception)
		{
			return new PortableProfileValidationResult(false, null, [exception.Message]);
		}
	}

	private static ProjectSelectionSpec ValidateAndConvert(
		PortableProfileDocument? document,
		string fullPath)
	{
		if (document is null ||
		    document.SchemaVersion != CurrentSchemaVersion ||
		    (document.Kind is not null &&
		     !string.Equals(document.Kind, DocumentKind, StringComparison.Ordinal)) ||
		    document.Selection is null)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Portable profile schema is missing or unsupported.");
		}

		if (!ProjectSelectionTokens.TryParseGitMode(document.Selection.GitMode, out var gitMode))
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Portable profile contains an unknown Git filtering mode.");
		}

		var exclusions = new HashSet<ProjectExclusion>();
		foreach (var value in document.Selection.Exclusions ?? [])
		{
			if (!ProjectSelectionTokens.TryParseExclusion(value, out var exclusion))
			{
				throw new PortableProjectProfileException(
					"DPX-CLI-PROFILE-INVALID",
					"Portable profile contains an unknown exclusion.");
			}

			exclusions.Add(exclusion);
		}

		IReadOnlyCollection<string> selectedPaths;
		try
		{
			selectedPaths = NormalizeSelectedPaths(document.Selection.SelectedPaths);
		}
		catch (ProjectContextValidationException exception)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Portable profile contains an unsafe selected path.",
				exception);
		}

		return new ProjectSelectionSpec(
			Roots: NormalizeNullableValues(document.Selection.Roots, PathComparer.Default),
			Extensions: NormalizeNullableValues(document.Selection.Extensions, StringComparer.OrdinalIgnoreCase),
			SelectedPaths: selectedPaths,
			GitMode: gitMode,
			Exclusions: exclusions.OrderBy(static exclusion => exclusion).ToArray(),
			ProfileSource: new ProjectProfileReference(ProjectProfileSourceKind.Portable, fullPath));
	}

	private static PortableProfileDocument ToDocument(ProjectSelectionSpec selection)
	{
		if (selection.GitMode is null || selection.Exclusions is null)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Only a fully resolved selection can be saved as a portable profile.");
		}

		return new PortableProfileDocument
		{
			SchemaVersion = CurrentSchemaVersion,
			Kind = DocumentKind,
			Selection = new PortableSelectionDocument
			{
				Roots = selection.Roots?.OrderBy(static value => value, PathComparer.Default).ToArray(),
				Extensions = selection.Extensions?.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray(),
				SelectedPaths = (selection.SelectedPaths ?? []).OrderBy(static value => value, PathComparer.Default).ToArray(),
				GitMode = ProjectSelectionTokens.ToToken(selection.GitMode.Value),
				Exclusions = selection.Exclusions
					.Select(ProjectSelectionTokens.ToToken)
					.OrderBy(static value => value, StringComparer.Ordinal)
					.ToArray()
			}
		};
	}

	private static string NormalizeProfilePath(string path)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Portable profile path is required.");
		}

		try
		{
			return Path.GetFullPath(path);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			throw new PortableProjectProfileException(
				"DPX-CLI-PROFILE-INVALID",
				"Portable profile path is invalid.",
				exception);
		}
	}

	private static IReadOnlyCollection<string>? NormalizeNullableValues(
		IReadOnlyCollection<string>? values,
		StringComparer comparer) =>
		values is null ? null : NormalizeValues(values, comparer);

	private static IReadOnlyCollection<string> NormalizeValues(
		IReadOnlyCollection<string>? values,
		StringComparer comparer) =>
		(values ?? [])
		.Where(static value => !string.IsNullOrWhiteSpace(value))
		.Select(static value => value.Trim())
		.Distinct(comparer)
		.OrderBy(static value => value, comparer)
		.ToArray();

	private static IReadOnlyCollection<string> NormalizeSelectedPaths(
		IReadOnlyCollection<string>? values) =>
		(values ?? [])
		.Where(static value => !string.IsNullOrWhiteSpace(value))
		.Select(ProjectSelectionPath.NormalizeRelative)
		.Where(static value => value.Length > 0)
		.Distinct(PathComparer.Default)
		.OrderBy(static value => value, PathComparer.Default)
		.ToArray();

	private sealed class PortableProfileDocument
	{
		public int SchemaVersion { get; set; }
		public string? Kind { get; set; }
		public PortableSelectionDocument? Selection { get; set; }

		[JsonExtensionData]
		public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
	}

	private sealed class PortableSelectionDocument
	{
		public IReadOnlyList<string>? Roots { get; set; }
		public IReadOnlyList<string>? Extensions { get; set; }
		public IReadOnlyList<string> SelectedPaths { get; set; } = [];
		public string? GitMode { get; set; }
		public IReadOnlyList<string> Exclusions { get; set; } = [];

		[JsonExtensionData]
		public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
	}
}
