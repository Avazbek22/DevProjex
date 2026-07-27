using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using DevProjex.Application.Context;
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

	public async Task SaveAsync(
		string path,
		ProjectSelectionSpec selection,
		bool overwrite,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(selection);
		var fullPath = NormalizeProfilePath(path);
		var document = ToDocument(selection);
		var directory = Path.GetDirectoryName(fullPath);
		if (!string.IsNullOrWhiteSpace(directory))
			Directory.CreateDirectory(directory);
		if (!overwrite && File.Exists(fullPath))
		{
			throw new PortableProjectProfileException(
				"DPX-PROFILE-DESTINATION-EXISTS",
				"The portable profile destination already exists.");
		}

		var tempPath = Path.Combine(
			directory ?? Directory.GetCurrentDirectory(),
			$".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await using (var stream = new FileStream(
				             tempPath,
				             FileMode.CreateNew,
				             FileAccess.Write,
				             FileShare.None,
				             16 * 1024,
				             FileOptions.Asynchronous | FileOptions.SequentialScan))
			{
				await JsonSerializer.SerializeAsync(stream, document, WriteOptions, cancellationToken)
					.ConfigureAwait(false);
				await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
			}

			File.Move(tempPath, fullPath, overwrite);
		}
		catch (PortableProjectProfileException)
		{
			throw;
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
		finally
		{
			TryDelete(tempPath);
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

	private static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// A unique sibling temp file is harmless if the platform keeps a transient handle.
		}
	}

	private sealed class PortableProfileDocument
	{
		public int SchemaVersion { get; set; }
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
