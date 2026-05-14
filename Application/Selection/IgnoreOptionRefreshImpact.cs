namespace DevProjex.Application.Selection;

[Flags]
public enum IgnoreOptionRefreshImpact
{
    None = 0,
    FileVisibility = 1,
    RootStructure = 2
}
