using System.Reflection;
using System.Runtime.InteropServices;
using DevProjex.Terminal.CommandLine;

namespace DevProjex.Tests.Terminal;

public sealed class InvocationEnvironmentTests
{
	[Theory]
	[InlineData(true, Architecture.Arm64, true)]
	[InlineData(true, Architecture.X64, false)]
	[InlineData(false, Architecture.Arm64, false)]
	[InlineData(false, Architecture.X64, false)]
	public void DarwinArm64VarArgIoctlSelectionIsPlatformSpecific(
		bool isMacOs,
		Architecture architecture,
		bool expected)
	{
		Assert.Equal(
			expected,
			InvocationEnvironment.RequiresDarwinArm64VarArgIoctl(isMacOs, architecture));
	}

	[Fact]
	public void DarwinArm64IoctlShimExhaustsArgumentRegistersBeforeOutputPointer()
	{
		var method = typeof(InvocationEnvironment).GetMethod(
			"ioctlDarwinArm64",
			BindingFlags.NonPublic | BindingFlags.Static);

		Assert.NotNull(method);
		Assert.Equal(9, method.GetParameters().Length);
		Assert.True(method.GetParameters()[^1].ParameterType.IsByRef);

		var import = method.GetCustomAttribute<DllImportAttribute>();
		Assert.NotNull(import);
		Assert.Equal("ioctl", import.EntryPoint);
		Assert.True(import.SetLastError);
	}
}
