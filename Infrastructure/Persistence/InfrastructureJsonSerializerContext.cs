using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.ThemePresets;

namespace DevProjex.Infrastructure.Persistence;

[JsonSerializable(typeof(ProjectProfileDb))]
[JsonSerializable(typeof(RecentProjectsDb))]
[JsonSerializable(typeof(UserSettingsDb))]
[JsonSerializable(typeof(ThemeSettingsDocument))]
internal sealed partial class InfrastructureJsonSerializerContext : JsonSerializerContext;
