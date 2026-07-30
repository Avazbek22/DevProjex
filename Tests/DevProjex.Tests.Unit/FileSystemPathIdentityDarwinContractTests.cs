using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using DevProjex.Application.Services;

namespace DevProjex.Tests.Unit;

public sealed class FileSystemPathIdentityDarwinContractTests
{
	[Theory]
	[InlineData(true, Architecture.Arm64, true)]
	[InlineData(true, Architecture.X64, false)]
	[InlineData(false, Architecture.Arm64, false)]
	[InlineData(false, Architecture.X64, false)]
	public void DarwinArm64VarArgFcntlSelectionUsesProcessAbi(
		bool isMacOs,
		Architecture processArchitecture,
		bool expected)
	{
		var method = GetPathIdentityType().GetMethod(
			"RequiresDarwinArm64VarArgFcntl",
			BindingFlags.NonPublic | BindingFlags.Static);

		Assert.NotNull(method);
		Assert.Equal(
			expected,
			method.Invoke(null, [isMacOs, processArchitecture]));
	}

	[Fact]
	public void DarwinArm64FcntlShimPlacesPathBufferInVarArgStackPosition()
	{
		var method = GetPathIdentityType().GetMethod(
			"DarwinFcntlArm64",
			BindingFlags.NonPublic | BindingFlags.Static);

		Assert.NotNull(method);
		var parameters = method.GetParameters();
		Assert.Equal(9, parameters.Length);
		Assert.All(
			parameters.Skip(2).Take(6),
			parameter => Assert.Equal(typeof(nint), parameter.ParameterType));
		Assert.Equal(typeof(IntPtr), parameters[^1].ParameterType);

		var import = method.GetCustomAttribute<DllImportAttribute>();
		Assert.NotNull(import);
		Assert.Equal("fcntl", import.EntryPoint);
		Assert.True(import.SetLastError);
	}

	[Fact]
	public void DarwinGetAttrListSignatureMatchesNativeWidths()
	{
		var pathIdentityType = GetPathIdentityType();
		var method = pathIdentityType.GetMethod(
			"GetAttrList",
			BindingFlags.NonPublic | BindingFlags.Static);
		var attributeListType = pathIdentityType.GetNestedType(
			"DarwinAttributeList",
			BindingFlags.NonPublic);

		Assert.NotNull(method);
		Assert.NotNull(attributeListType);
		Assert.Equal(24, Marshal.SizeOf(attributeListType));
		var parameters = method.GetParameters();
		Assert.Equal(typeof(nuint), parameters[3].ParameterType);
		Assert.Equal(typeof(uint), parameters[4].ParameterType);
	}

	[Fact]
	public void DarwinPathBufferDecodeRejectsMissingInBoundsTerminator()
	{
		var bufferLength = GetDarwinPathBufferLength();
		var buffer = Enumerable.Repeat((byte)'x', bufferLength).ToArray();

		var (success, canonicalPath) = DecodeDarwinPathBuffer(buffer);

		Assert.False(success);
		Assert.Empty(canonicalPath);
	}

	[Fact]
	public void DarwinPathBufferLengthMatchesMaxPathLength()
	{
		Assert.Equal(1024, GetDarwinPathBufferLength());
	}

	[Fact]
	public void DarwinPathBufferDecodePreservesUnicodeBeforeFirstTerminator()
	{
		const string expected = "/private/tmp/Проект/資料/e\u0301";
		var encodedPath = Encoding.UTF8.GetBytes(expected);
		var buffer = new byte[GetDarwinPathBufferLength()];
		encodedPath.CopyTo(buffer, 0);
		buffer[encodedPath.Length + 1] = 0xff;

		var (success, canonicalPath) = DecodeDarwinPathBuffer(buffer);

		Assert.True(success);
		Assert.Equal(expected, canonicalPath);
	}

	[Fact]
	public void DarwinLocationReadUsesAbiDispatchAndBoundedManagedBuffer()
	{
		var source = ReadRepositoryFile(
			"Application",
			"Services",
			"FileSystemPathIdentity.cs");
		var methodStart = source.IndexOf(
			"private static bool TryReadDarwinLocation",
			StringComparison.Ordinal);
		var methodEnd = source.IndexOf(
			"private static bool IsPathInsideOrdinal",
			methodStart,
			StringComparison.Ordinal);

		Assert.True(methodStart >= 0 && methodEnd > methodStart);
		var method = source[methodStart..methodEnd];
		Assert.Contains("new byte[DarwinPathBufferLength]", method, StringComparison.Ordinal);
		Assert.Contains("ReadDarwinPath(", method, StringComparison.Ordinal);
		Assert.Contains("TryDecodeDarwinPathBuffer(", method, StringComparison.Ordinal);
		Assert.DoesNotContain("Marshal.AllocHGlobal", method, StringComparison.Ordinal);
		Assert.DoesNotContain("Marshal.PtrToStringUTF8", method, StringComparison.Ordinal);
	}

	private static (bool Success, string CanonicalPath) DecodeDarwinPathBuffer(
		byte[] buffer)
	{
		var method = GetPathIdentityType().GetMethod(
			"TryDecodeDarwinPathBuffer",
			BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(method);
		object?[] arguments = [buffer, null];

		var success = Assert.IsType<bool>(method.Invoke(null, arguments));
		var canonicalPath = Assert.IsType<string>(arguments[1]);
		return (success, canonicalPath);
	}

	private static int GetDarwinPathBufferLength()
	{
		var field = GetPathIdentityType().GetField(
			"DarwinPathBufferLength",
			BindingFlags.NonPublic | BindingFlags.Static);

		Assert.NotNull(field);
		return Assert.IsType<int>(field.GetRawConstantValue());
	}

	private static Type GetPathIdentityType() =>
		typeof(ProjectCopyExportService).Assembly.GetType(
			"DevProjex.Application.Services.FileSystemPathIdentity",
			throwOnError: true)!;

	private static string ReadRepositoryFile(params string[] parts) =>
		File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (!string.IsNullOrWhiteSpace(directory))
		{
			if (File.Exists(Path.Combine(directory, "DevProjex.sln")))
				return directory;

			directory = Directory.GetParent(directory)?.FullName;
		}

		throw new InvalidOperationException("Repository root not found.");
	}
}
