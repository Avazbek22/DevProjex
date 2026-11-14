using System.Security.AccessControl;
using System.Security.Principal;

namespace DevProjex.Tests.Terminal;

public sealed partial class CliInputContractProcessTests
{
	[Fact]
	public void WindowsAclDenialKeepsProjectDiagnosticsAndUsageExitCodes()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Skip("Windows ACL denial is covered here; Unix modes are covered by UnreadableProjectRootIsAUsageError.");
			return;
		}
		using var workspace = new TemporaryDirectory();
		var project = CreateProject(workspace);
		var directory = new DirectoryInfo(project);
		var original = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
		var restricted = new DirectorySecurity();
		restricted.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
		using var identity = WindowsIdentity.GetCurrent();
		Assert.NotNull(identity.User);
		restricted.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.ListDirectory,
			InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
		try
		{
			directory.SetAccessControl(restricted);
			Assert.False(CanEnumerate(project));
			foreach (var command in new[] { new[] { "tree" }, new[] { "analyze" }, new[] { "export", "context" } })
			{
				var result = RunCli(workspace, [.. command, project, "--git-mode", "none", "--format", "json"]);
				Assert.True(result.ExitCode == CommandLineExitCodes.UsageError, string.Join(' ', command) + ": " + result.StandardError);
				if (command[0] == "analyze")
				{
					using var document = JsonDocument.Parse(result.StandardOutput);
					Assert.Equal("devprojex-analysis", document.RootElement.GetProperty("kind").GetString());
					Assert.Equal(0, document.RootElement.GetProperty("inventory").GetProperty("files").GetInt32());
					Assert.Contains(document.RootElement.GetProperty("diagnostics").EnumerateArray(), diagnostic =>
						diagnostic.GetProperty("code").GetString() is "DPX-PROJECT-ROOT-ACCESS-DENIED" or "DPX-PROJECT-NOT-FOUND");
				}
				else
					Assert.Empty(result.StandardOutput);
				Assert.Matches("DPX-PROJECT-(ROOT-ACCESS-DENIED|NOT-FOUND)", result.StandardError);
			}
		}
		finally
		{
			var restored = new DirectorySecurity();
			restored.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
			directory.SetAccessControl(restored);
		}
		Assert.True(CanEnumerate(project));
	}
}
