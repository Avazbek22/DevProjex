namespace DevProjex.Avalonia.Coordinators;

public readonly record struct ProjectProfileLoadSnapshot(
	ProjectProfileLookupStatus Status,
	ProjectSelectionProfile? Profile,
	PersistentSecretMarksSnapshot? PersistentMarks)
{
	public bool HasProfile => Status == ProjectProfileLookupStatus.Found && Profile is not null;
}
