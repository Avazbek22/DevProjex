namespace DevProjex.Kernel;

public static class BuildFlags
{
	// Build-time switch for Store builds where auto-elevation is not allowed.
	// Property form avoids compile-time dead-code warnings in callers.
	public static bool AllowElevation =>
#if DEVPROJEX_STORE
		false;
#else
		true;
#endif
}
