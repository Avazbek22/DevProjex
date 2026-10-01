using System.Runtime.InteropServices;

namespace DevProjex.Tests.Unit;

public sealed class FileSystemHandleIdentityTests
{
	[Fact]
	public void OpenedHandleIdentityIsStableAndDistinguishesAnotherFile()
	{
		using var workspace = new TemporaryDirectory();
		var originalPath = Path.Combine(workspace.Path, "original.pack");
		var otherPath = Path.Combine(workspace.Path, "other.pack");
		File.WriteAllText(originalPath, "original");
		File.WriteAllText(otherPath, "original");
		using var original = File.OpenRead(originalPath);
		using var reopened = File.OpenRead(originalPath);
		using var other = File.OpenRead(otherPath);

		Assert.True(FileSystemPathIdentity.TryReadHandle(original.SafeFileHandle, out var createdIdentity));
		Assert.True(FileSystemPathIdentity.TryReadHandle(reopened.SafeFileHandle, out var reopenedIdentity));
		Assert.True(FileSystemPathIdentity.TryReadHandle(other.SafeFileHandle, out var otherIdentity));
		Assert.Equal(createdIdentity, reopenedIdentity);
		Assert.NotEqual(createdIdentity, otherIdentity);
	}

	[Fact]
	public void ClosedHandleIdentityIsUnavailable()
	{
		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "closed.pack");
		File.WriteAllText(path, "content");
		var stream = File.OpenRead(path);
		stream.Dispose();

		Assert.False(FileSystemPathIdentity.TryReadHandle(stream.SafeFileHandle, out _));
	}

	[Fact]
	public void UnixHandleIdentitySurvivesPathRemoval()
	{
		if (OperatingSystem.IsWindows())
			Assert.Skip("This contract exercises Unix file descriptors after unlink.");

		using var workspace = new TemporaryDirectory();
		var path = Path.Combine(workspace.Path, "removed.pack");
		File.WriteAllText(path, "content");
		using var stream = File.OpenRead(path);
		Assert.True(FileSystemPathIdentity.TryReadHandle(stream.SafeFileHandle, out var original));
		File.Delete(path);

		Assert.True(FileSystemPathIdentity.TryReadHandle(stream.SafeFileHandle, out var unlinked));
		Assert.Equal(original, unlinked);
	}

	[Fact]
	public void NativeHandleIdentityInteropMatchesExpectedLayouts()
	{
		var type = typeof(FileSystemPathIdentity);
		var windowsInformation = type.GetNestedType(
			"WindowsFileIdInformation",
			BindingFlags.NonPublic);
		Assert.NotNull(windowsInformation);
		Assert.Equal(24, Marshal.SizeOf(windowsInformation));
		Assert.Equal(0, Marshal.OffsetOf(windowsInformation, "VolumeSerialNumber").ToInt32());
		Assert.Equal(8, Marshal.OffsetOf(windowsInformation, "FileIdLow").ToInt32());
		Assert.Equal(16, Marshal.OffsetOf(windowsInformation, "FileIdHigh").ToInt32());

		var darwinMethod = type.GetMethod("FGetAttrList", BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(darwinMethod);
		var parameters = darwinMethod.GetParameters();
		Assert.Equal(typeof(int), parameters[0].ParameterType);
		Assert.Equal(typeof(nuint), parameters[3].ParameterType);
		Assert.Equal(typeof(nuint), parameters[4].ParameterType);
		Assert.Equal("fgetattrlist", darwinMethod.GetCustomAttribute<DllImportAttribute>()?.EntryPoint);
		Assert.Equal(
			(uint)0x200,
			type.GetField("DarwinReturnRealDevice", BindingFlags.NonPublic | BindingFlags.Static)?
				.GetRawConstantValue());
	}
}
