using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace DevProjex.Application.Services;

internal readonly record struct FileSystemPathLocation(
	string NamespaceId,
	string CanonicalPath);

internal readonly record struct FileSystemPathIdentity(
	ulong Device,
	ulong Node)
{
	private const uint WindowsFileReadAttributes = 0x80;
	private const uint WindowsFileShareAll = 0x7;
	private const uint WindowsOpenExisting = 3;
	private const uint WindowsBackupSemantics = 0x02000000;
	private const uint WindowsVolumeNameNt = 0x2;
	private const int LinuxCurrentWorkingDirectory = -100;
	private const int LinuxOpenCloseOnExec = 0x00080000;
	private const int LinuxOpenPath = 0x00200000;
	private const uint LinuxBasicStats = 0x000007ff;
	private const uint LinuxStatxMountId = 0x00001000;
	private const ushort DarwinAttributeBitmapCount = 5;
	private const uint DarwinCommonDeviceId = 0x00000002;
	private const uint DarwinCommonFileId = 0x02000000;
	private const int DarwinOpenReadOnly = 0;
	private const int DarwinOpenCloseOnExec = 0x01000000;
	private const int DarwinGetPathWithoutFirmlink = 102;
	private const int DarwinPathBufferLength = 1024;

	public static bool TryRead(string path, out FileSystemPathIdentity identity)
	{
		if (OperatingSystem.IsWindows())
			return TryReadWindows(path, out identity);
		if (OperatingSystem.IsLinux())
			return TryReadLinux(path, out identity);
		if (OperatingSystem.IsMacOS())
			return TryReadDarwin(path, out identity);

		identity = default;
		return false;
	}

	public static bool TryReadLocation(
		string path,
		out FileSystemPathLocation location)
	{
		if (OperatingSystem.IsWindows())
			return TryReadWindowsLocation(path, out location);
		if (OperatingSystem.IsLinux())
			return TryReadLinuxLocation(path, out location);
		if (OperatingSystem.IsMacOS())
			return TryReadDarwinLocation(path, out location);

		location = default;
		return false;
	}

	public static bool TryEnumerateMountPointsInside(
		string path,
		out IReadOnlyList<string> mountPoints)
	{
		if (!OperatingSystem.IsLinux())
		{
			mountPoints = [];
			return true;
		}

		try
		{
			if (!TryResolveLinuxNamespacePath(
				    path,
				    out var normalizedPath,
				    out _))
			{
				mountPoints = [];
				return false;
			}
			if (!TryReadLinuxMounts(out var mounts))
			{
				mountPoints = [];
				return false;
			}

			if (!TryReadLinux(
				    normalizedPath,
				    out var sourceIdentity))
			{
				mountPoints = [];
				return false;
			}

			var sourceSegments = SplitLinuxPath(normalizedPath);
			var protectedMountPoints = new List<string>();
			foreach (var mount in mounts)
			{
				var mountSegments = SplitLinuxPath(mount.MountPoint);
				if (mountSegments.Length <= sourceSegments.Length ||
				    !HasEquivalentLinuxPrefix(
					    mountSegments,
					    sourceSegments))
				{
					continue;
				}

				var sourcePrefix = sourceSegments.Length == 0
					? "/"
					: "/" + string.Join(
						'/',
						mountSegments.Take(sourceSegments.Length));
				if (!TryReadLinux(
					    sourcePrefix,
					    out var prefixIdentity))
				{
					mountPoints = [];
					return false;
				}
				if (prefixIdentity == sourceIdentity)
					protectedMountPoints.Add(mount.MountPoint);
			}

			mountPoints = protectedMountPoints
				.Distinct(StringComparer.Ordinal)
				.ToArray();
			return true;
		}
		catch (Exception exception) when (exception is
			       FileNotFoundException or
			       DirectoryNotFoundException or
			       UnauthorizedAccessException or
			       IOException or
			       NotSupportedException)
		{
			mountPoints = [];
			return false;
		}
	}

	private static bool TryReadWindows(
		string path,
		out FileSystemPathIdentity identity)
	{
		using var handle = CreateFile(
			path,
			WindowsFileReadAttributes,
			WindowsFileShareAll,
			IntPtr.Zero,
			WindowsOpenExisting,
			WindowsBackupSemantics,
			IntPtr.Zero);
		if (handle.IsInvalid ||
		    !GetFileInformationByHandle(handle, out var information))
		{
			identity = default;
			return false;
		}

		identity = new FileSystemPathIdentity(
			information.VolumeSerialNumber,
			((ulong)information.FileIndexHigh << 32) |
			information.FileIndexLow);
		return true;
	}

	private static bool TryReadWindowsLocation(
		string path,
		out FileSystemPathLocation location)
	{
		using var handle = CreateFile(
			path,
			WindowsFileReadAttributes,
			WindowsFileShareAll,
			IntPtr.Zero,
			WindowsOpenExisting,
			WindowsBackupSemantics,
			IntPtr.Zero);
		if (handle.IsInvalid)
		{
			location = default;
			return false;
		}

		var buffer = new StringBuilder(512);
		var length = GetFinalPathNameByHandle(
			handle,
			buffer,
			(uint)buffer.Capacity,
			WindowsVolumeNameNt);
		if (length >= buffer.Capacity)
		{
			buffer.EnsureCapacity(checked((int)length + 1));
			length = GetFinalPathNameByHandle(
				handle,
				buffer,
				(uint)buffer.Capacity,
				WindowsVolumeNameNt);
		}
		if (length == 0 || length >= buffer.Capacity)
		{
			location = default;
			return false;
		}

		location = new FileSystemPathLocation(
			"windows-nt",
			buffer.ToString());
		return true;
	}

	private static bool TryReadLinux(
		string path,
		out FileSystemPathIdentity identity)
	{
		try
		{
			if (Statx(
				    LinuxCurrentWorkingDirectory,
				    path,
				    flags: 0,
				    LinuxBasicStats,
				    out var status) != 0 ||
			    (status.Mask & LinuxStatxInode) == 0)
			{
				identity = default;
				return false;
			}

			identity = new FileSystemPathIdentity(
				((ulong)status.DeviceMajor << 32) |
				status.DeviceMinor,
				status.Inode);
			return true;
		}
		catch (Exception exception) when (exception is
			       DllNotFoundException or
			       EntryPointNotFoundException)
		{
			identity = default;
			return false;
		}
	}

	private static bool TryReadLinuxLocation(
		string path,
		out FileSystemPathLocation location)
	{
		try
		{
			if (!TryResolveLinuxNamespacePath(
				    path,
				    out var normalizedPath,
				    out var descriptorMountId))
			{
				location = default;
				return false;
			}
			if (Statx(
				    LinuxCurrentWorkingDirectory,
				    normalizedPath,
				    flags: 0,
				    LinuxBasicStats | LinuxStatxMountId,
				    out var status) != 0 ||
			    !TryReadLinuxMounts(out var mounts))
			{
				location = default;
				return false;
			}

			var device = $"{status.DeviceMajor}:{status.DeviceMinor}";
			if ((status.Mask & LinuxStatxMountId) != 0 &&
			    status.MountId != 0 &&
			    status.MountId != descriptorMountId)
			{
				location = default;
				return false;
			}

			LinuxMount? selectedMount = null;
			foreach (var mount in mounts)
			{
				if (mount.Id != descriptorMountId ||
				    !mount.Device.Equals(device, StringComparison.Ordinal) ||
				    !IsPathInsideOrdinal(normalizedPath, mount.MountPoint))
				{
					continue;
				}

				selectedMount = mount;
				break;
			}

			if (selectedMount is null)
			{
				location = default;
				return false;
			}

			var relativePath = Path.GetRelativePath(
				selectedMount.Value.MountPoint,
				normalizedPath);
			var internalPath = relativePath == "."
				? selectedMount.Value.Root
				: Path.GetFullPath(
					Path.Combine(
						"/",
						selectedMount.Value.Root.TrimStart('/'),
						relativePath));
			location = new FileSystemPathLocation(
				$"linux:{selectedMount.Value.Device}",
				internalPath);
			return true;
		}
		catch (Exception exception) when (exception is
			       FileNotFoundException or
			       DirectoryNotFoundException or
			       UnauthorizedAccessException or
			       IOException or
			       NotSupportedException)
		{
			location = default;
			return false;
		}
	}

	private static bool TryResolveLinuxNamespacePath(
		string path,
		out string namespacePath,
		out ulong mountId)
	{
		var fileDescriptor = -1;
		try
		{
			fileDescriptor = LinuxOpen(
				path,
				LinuxOpenPath |
				LinuxOpenCloseOnExec);
			if (fileDescriptor < 0)
			{
				namespacePath = string.Empty;
				mountId = 0;
				return false;
			}

			var descriptorLink = new FileInfo($"/proc/self/fd/{fileDescriptor}");
			var linkTarget = descriptorLink.LinkTarget;
			if (string.IsNullOrWhiteSpace(linkTarget) ||
			    !Path.IsPathFullyQualified(linkTarget))
			{
				namespacePath = string.Empty;
				mountId = 0;
				return false;
			}

			namespacePath = PathUtility.Normalize(linkTarget);
			mountId = 0;
			foreach (var line in File.ReadLines(
				         $"/proc/self/fdinfo/{fileDescriptor}"))
			{
				if (!line.StartsWith("mnt_id:", StringComparison.Ordinal))
					continue;

				var value = line["mnt_id:".Length..].Trim();
				if (ulong.TryParse(
					    value,
					    System.Globalization.NumberStyles.None,
					    System.Globalization.CultureInfo.InvariantCulture,
					    out mountId))
				{
					break;
				}
			}

			return mountId != 0;
		}
		catch (Exception exception) when (exception is
			       FileNotFoundException or
			       DirectoryNotFoundException or
			       UnauthorizedAccessException or
			       IOException or
			       NotSupportedException)
		{
			namespacePath = string.Empty;
			mountId = 0;
			return false;
		}
		finally
		{
			if (fileDescriptor >= 0)
				_ = LinuxClose(fileDescriptor);
		}
	}

	private static string[] SplitLinuxPath(string path) =>
		path.Split('/', StringSplitOptions.RemoveEmptyEntries);

	private static bool HasEquivalentLinuxPrefix(
		IReadOnlyList<string> candidateSegments,
		IReadOnlyList<string> sourceSegments)
	{
		for (var index = 0; index < sourceSegments.Count; index++)
		{
			if (!candidateSegments[index]
				    .Normalize(NormalizationForm.FormC)
				    .Equals(
					    sourceSegments[index].Normalize(NormalizationForm.FormC),
					    StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return true;
	}

	private static bool TryReadLinuxMounts(out IReadOnlyList<LinuxMount> mounts)
	{
		var parsedMounts = new List<LinuxMount>();
		foreach (var line in File.ReadLines("/proc/self/mountinfo"))
		{
			var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length < 6 ||
			    !ulong.TryParse(
				    fields[0],
				    System.Globalization.NumberStyles.None,
				    System.Globalization.CultureInfo.InvariantCulture,
				    out var mountId))
			{
				mounts = [];
				return false;
			}

			parsedMounts.Add(new LinuxMount(
				mountId,
				fields[2],
				DecodeLinuxMountPath(fields[3]),
				DecodeLinuxMountPath(fields[4])));
		}

		mounts = parsedMounts;
		return true;
	}

	private static bool TryReadDarwin(
		string path,
		out FileSystemPathIdentity identity)
	{
		var attributes = new DarwinAttributeList
		{
			BitmapCount = DarwinAttributeBitmapCount,
			CommonAttributes = DarwinCommonDeviceId | DarwinCommonFileId
		};
		var buffer = Marshal.AllocHGlobal(24);
		try
		{
			if (GetAttrList(
				    path,
				    ref attributes,
				    buffer,
				    24,
				    options: 0) != 0)
			{
				identity = default;
				return false;
			}

			var returnedBytes = Marshal.ReadInt32(buffer);
			if (returnedBytes < 16)
			{
				identity = default;
				return false;
			}

			identity = new FileSystemPathIdentity(
				unchecked((uint)Marshal.ReadInt32(buffer, 4)),
				unchecked((ulong)Marshal.ReadInt64(buffer, 8)));
			return true;
		}
		catch (Exception exception) when (exception is
			       DllNotFoundException or
			       EntryPointNotFoundException)
		{
			identity = default;
			return false;
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	private static bool TryReadDarwinLocation(
		string path,
		out FileSystemPathLocation location)
	{
		var fileDescriptor = -1;
		try
		{
			fileDescriptor = DarwinOpen(
				path,
				DarwinOpenReadOnly |
				DarwinOpenCloseOnExec);
			if (fileDescriptor < 0)
			{
				location = default;
				return false;
			}

			var pathBuffer = new byte[DarwinPathBufferLength];
			var pathBufferHandle = GCHandle.Alloc(
				pathBuffer,
				GCHandleType.Pinned);
			int readResult;
			try
			{
				readResult = ReadDarwinPath(
					fileDescriptor,
					pathBufferHandle.AddrOfPinnedObject());
			}
			finally
			{
				pathBufferHandle.Free();
			}

			if (readResult != 0 ||
			    !TryDecodeDarwinPathBuffer(
				    pathBuffer,
				    out var canonicalPath))
			{
				location = default;
				return false;
			}

			location = new FileSystemPathLocation(
				"darwin",
				canonicalPath);
			return true;
		}
		catch (Exception exception) when (exception is
			       DllNotFoundException or
			       EntryPointNotFoundException)
		{
			location = default;
			return false;
		}
		finally
		{
			if (fileDescriptor >= 0)
				_ = DarwinClose(fileDescriptor);
		}
	}

	private static int ReadDarwinPath(
		int fileDescriptor,
		IntPtr pathBuffer)
	{
		if (RequiresDarwinArm64VarArgFcntl(
			    OperatingSystem.IsMacOS(),
			    RuntimeInformation.ProcessArchitecture))
		{
			// Darwin ARM64 passes variadic arguments on the stack. Fill x2-x7 so
			// the path pointer reaches fcntl's first ABI-defined vararg slot.
			return DarwinFcntlArm64(
				fileDescriptor,
				DarwinGetPathWithoutFirmlink,
				0,
				0,
				0,
				0,
				0,
				0,
				pathBuffer);
		}

		return DarwinFcntl(
			fileDescriptor,
			DarwinGetPathWithoutFirmlink,
			pathBuffer);
	}

	private static bool RequiresDarwinArm64VarArgFcntl(
		bool isMacOs,
		Architecture processArchitecture) =>
		isMacOs && processArchitecture == Architecture.Arm64;

	private static bool TryDecodeDarwinPathBuffer(
		byte[] pathBuffer,
		out string canonicalPath)
	{
		var terminatorIndex = Array.IndexOf(pathBuffer, (byte)0);
		if (terminatorIndex <= 0)
		{
			canonicalPath = string.Empty;
			return false;
		}

		canonicalPath = Encoding.UTF8.GetString(
			pathBuffer,
			0,
			terminatorIndex);
		return !string.IsNullOrWhiteSpace(canonicalPath);
	}

	private static bool IsPathInsideOrdinal(string candidate, string root)
	{
		var normalizedCandidate = PathUtility.Normalize(candidate);
		var normalizedRoot = PathUtility.Normalize(root);
		if (normalizedCandidate.Equals(normalizedRoot, StringComparison.Ordinal))
			return true;

		var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
			? normalizedRoot
			: normalizedRoot + Path.DirectorySeparatorChar;
		return normalizedCandidate.StartsWith(rootPrefix, StringComparison.Ordinal);
	}

	private static string DecodeLinuxMountPath(string value) =>
		value
			.Replace("\\040", " ", StringComparison.Ordinal)
			.Replace("\\011", "\t", StringComparison.Ordinal)
			.Replace("\\012", "\n", StringComparison.Ordinal)
			.Replace("\\134", "\\", StringComparison.Ordinal);

	private const uint LinuxStatxInode = 0x00000100;

	[StructLayout(LayoutKind.Explicit, Size = 256)]
	private struct LinuxStatx
	{
		[FieldOffset(0)]
		public uint Mask;

		[FieldOffset(32)]
		public ulong Inode;

		[FieldOffset(136)]
		public uint DeviceMajor;

		[FieldOffset(140)]
		public uint DeviceMinor;

		[FieldOffset(144)]
		public ulong MountId;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct DarwinAttributeList
	{
		public ushort BitmapCount;
		public ushort Reserved;
		public uint CommonAttributes;
		public uint VolumeAttributes;
		public uint DirectoryAttributes;
		public uint FileAttributes;
		public uint ForkAttributes;
	}

	private readonly record struct LinuxMount(
		ulong Id,
		string Device,
		string Root,
		string MountPoint);

	[StructLayout(LayoutKind.Sequential)]
	private struct WindowsFileTime
	{
		public uint Low;
		public uint High;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct WindowsFileInformation
	{
		public uint FileAttributes;
		public WindowsFileTime CreationTime;
		public WindowsFileTime LastAccessTime;
		public WindowsFileTime LastWriteTime;
		public uint VolumeSerialNumber;
		public uint FileSizeHigh;
		public uint FileSizeLow;
		public uint NumberOfLinks;
		public uint FileIndexHigh;
		public uint FileIndexLow;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(
		string fileName,
		uint desiredAccess,
		uint shareMode,
		IntPtr securityAttributes,
		uint creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetFileInformationByHandle(
		SafeFileHandle handle,
		out WindowsFileInformation information);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern uint GetFinalPathNameByHandle(
		SafeFileHandle handle,
		StringBuilder path,
		uint pathLength,
		uint flags);

	[DllImport("libc", EntryPoint = "statx", SetLastError = true)]
	private static extern int Statx(
		int directoryFileDescriptor,
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags,
		uint mask,
		out LinuxStatx status);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int LinuxOpen(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags);

	[DllImport("libc", EntryPoint = "close", SetLastError = true)]
	private static extern int LinuxClose(int fileDescriptor);

	[DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
	private static extern int GetAttrList(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		ref DarwinAttributeList attributes,
		IntPtr buffer,
		nuint bufferSize,
		uint options);

	[DllImport("libc", EntryPoint = "open", SetLastError = true)]
	private static extern int DarwinOpen(
		[MarshalAs(UnmanagedType.LPUTF8Str)] string path,
		int flags);

	[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int DarwinFcntl(
		int fileDescriptor,
		int command,
		IntPtr buffer);

	[DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
	private static extern int DarwinFcntlArm64(
		int fileDescriptor,
		int command,
		nint register2,
		nint register3,
		nint register4,
		nint register5,
		nint register6,
		nint register7,
		IntPtr buffer);

	[DllImport("libc", EntryPoint = "close", SetLastError = true)]
	private static extern int DarwinClose(int fileDescriptor);
}
