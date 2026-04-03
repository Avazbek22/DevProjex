namespace DevProjex.Avalonia.Coordinators;

[Flags]
internal enum IgnoreOptionRefreshImpact
{
    None = 0,
    FileVisibility = 1,
    RootStructure = 2
}
