namespace DevProjex.Kernel.Models;

public sealed record AppInstanceLaunchResult(bool Succeeded, string? ErrorMessage)
{
    public static AppInstanceLaunchResult Success { get; } = new(true, null);

    public static AppInstanceLaunchResult Failure(string? errorMessage)
        => new(false, errorMessage);
}
