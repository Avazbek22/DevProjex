namespace DevProjex.Application.Presentation;

public sealed record FileContentClassificationDescriptor(
	FileContentClassification Id,
	string LabelKey);

public static class FileContentClassificationCatalog
{
	public static IReadOnlyList<FileContentClassificationDescriptor> All { get; } =
	[
		new(FileContentClassification.Text, "Content.Classification.Text"),
		new(FileContentClassification.Binary, "Content.Classification.Binary"),
		new(FileContentClassification.TooLarge, "Content.Classification.TooLarge"),
		new(FileContentClassification.Unreadable, "Content.Classification.Unreadable"),
		new(FileContentClassification.AccessDenied, "Content.Classification.AccessDenied"),
		new(FileContentClassification.Missing, "Content.Classification.Missing"),
		new(
			FileContentClassification.UnsupportedEncoding,
			"Content.Classification.UnsupportedEncoding")
	];

	public static FileContentClassificationDescriptor Get(
		FileContentClassification classification) =>
		All.Single(descriptor => descriptor.Id == classification);
}
