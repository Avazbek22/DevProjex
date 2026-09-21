using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Kernel.Models;

internal static class ProfileReadBenchmark
{
	private const int LookupCount = 1_000;

	public static void Run(string[] arguments)
	{
		var repetitions = ReadRepetitions(arguments);
		Console.WriteLine("document_bytes,lookups,median_ms,min_ms,max_ms,spread_ms,document_parses");
		foreach (var size in new[] { 10 * 1024, 1024 * 1024, 4 * 1024 * 1024 - 128 })
		{
			var fixture = CreateFixture(size);
			try
			{
				var samples = Enumerable.Range(0, repetitions)
					.Select(_ => Measure(fixture))
					.ToArray();
				var elapsed = samples.Select(static sample => sample.ElapsedMilliseconds).Order().ToArray();
				var parses = samples.Select(static sample => sample.DocumentParses).Distinct().Single();
				Console.WriteLine(string.Join(',',
					size.ToString(CultureInfo.InvariantCulture),
					LookupCount.ToString(CultureInfo.InvariantCulture),
					Format(elapsed[elapsed.Length / 2]),
					Format(elapsed[0]),
					Format(elapsed[^1]),
					Format(elapsed[^1] - elapsed[0]),
					parses.ToString(CultureInfo.InvariantCulture)));
			}
			finally
			{
				Directory.Delete(fixture.Root, recursive: true);
			}
		}
	}

	private static ProfileReadSample Measure(ProfileReadFixture fixture)
	{
		var store = new ProjectProfileStore(() => fixture.DataRoot);
		var timer = Stopwatch.StartNew();
		for (var iteration = 0; iteration < LookupCount; iteration++)
		{
			var result = store.LookupProfile(fixture.ProjectRoot, TimeSpan.FromSeconds(5));
			if (result.Status != ProjectProfileLookupStatus.Found ||
				result.Profile?.SelectedPaths?.Single() != "src")
			{
				throw new InvalidOperationException("Profile lookup changed its result.");
			}
		}
		timer.Stop();
		var parseProperty = typeof(ProjectProfileStore).GetProperty(
			"DocumentParseCount",
			BindingFlags.Instance | BindingFlags.NonPublic);
		var parseCount = parseProperty?.GetValue(store) is long measured
			? measured
			: LookupCount * 2L;
		return new ProfileReadSample(timer.Elapsed.TotalMilliseconds, parseCount);
	}

	private static ProfileReadFixture CreateFixture(int targetBytes)
	{
		var root = Path.Combine(
			Path.GetTempPath(),
			"devprojex-profile-read-benchmark",
			Guid.NewGuid().ToString("N"));
		var data = Path.Combine(root, "data");
		var project = Path.Combine(root, "project");
		Directory.CreateDirectory(data);
		Directory.CreateDirectory(project);
		var store = new ProjectProfileStore(() => data);
		if (!store.TrySaveProfile(project, new ProjectSelectionProfile([], [], [], SelectedPaths: ["src"])))
			throw new InvalidOperationException("Could not create the profile fixture.");
		var primary = store.GetPath();
		var payload = File.ReadAllText(primary).TrimEnd();
		if (!payload.EndsWith('}'))
			throw new InvalidOperationException("Unexpected profile fixture shape.");
		var prefix = payload[..^1] + ",\n  \"benchmarkPadding\": \"";
		const string suffix = "\"\n}";
		var paddingLength = targetBytes - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
		if (paddingLength < 0)
			throw new InvalidOperationException("Target document is smaller than the profile fixture.");
		var expanded = prefix + new string('x', paddingLength) + suffix;
		if (Encoding.UTF8.GetByteCount(expanded) != targetBytes)
			throw new InvalidOperationException("Profile fixture size is not exact.");
		File.WriteAllText(primary, expanded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		File.WriteAllText(primary + ".bak", expanded, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		return new ProfileReadFixture(root, data, project);
	}

	private static int ReadRepetitions(string[] arguments)
	{
		if (arguments.Length == 0)
			return 5;
		if (arguments.Length != 2 || arguments[0] != "--repetitions")
			throw new ArgumentException("Only --repetitions is supported.");
		var repetitions = int.Parse(arguments[1], CultureInfo.InvariantCulture);
		return repetitions >= 3 ? repetitions : throw new ArgumentOutOfRangeException(nameof(arguments));
	}

	private static string Format(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

	private sealed record ProfileReadFixture(string Root, string DataRoot, string ProjectRoot);
	private readonly record struct ProfileReadSample(double ElapsedMilliseconds, long DocumentParses);
}
