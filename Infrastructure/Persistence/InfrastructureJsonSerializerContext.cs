using DevProjex.Infrastructure.ProjectProfiles;
using DevProjex.Infrastructure.RecentProjects;
using DevProjex.Infrastructure.ThemePresets;
using DevProjex.Infrastructure.Updates;

namespace DevProjex.Infrastructure.Persistence;

[JsonSerializable(typeof(ProjectProfileDb))]
[JsonSerializable(typeof(RecentProjectsDb))]
[JsonSerializable(typeof(UserSettingsDb))]
[JsonSerializable(typeof(UpdateCheckSettings))]
[JsonSerializable(typeof(ThemeSettingsDocument))]
[JsonSerializable(typeof(GitHubLatestReleaseResponse))]
internal sealed partial class InfrastructureJsonSerializerContext : JsonSerializerContext;
