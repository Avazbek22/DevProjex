using DevProjex.Avalonia.Services;
using System.Runtime.InteropServices;

namespace DevProjex.Tests.Unit.Avalonia;

public sealed class WindowsTaskbarProgressControllerTests
{
    [Fact]
    public void WindowsTaskbarList3Interface_UsesDocumentedShellGuid()
    {
        var nativeType = typeof(WindowsTaskbarProgressService)
            .Assembly
            .GetType("DevProjex.Avalonia.Services.WindowsTaskbarProgressNative", throwOnError: true)!;
        var interfaceType = nativeType
            .GetNestedType("ITaskbarList3", BindingFlags.NonPublic)!;
        var guid = interfaceType.GetCustomAttribute<GuidAttribute>()?.Value;

        Assert.Equal("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF", guid);
    }

    [Fact]
    public void SetProgress_WithoutAttachedWindow_DoesNotCallNativeShell()
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);

        var result = controller.SetProgress(50);

        Assert.False(result);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void Attach_WithZeroHandle_IsRejected()
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);

        var attached = controller.Attach(0);
        var progressResult = controller.SetIndeterminate();

        Assert.False(attached);
        Assert.False(progressResult);
        Assert.Empty(native.Calls);
    }

    [Fact]
    public void SetIndeterminate_ForAttachedWindow_SetsIndeterminateState()
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(123);

        var result = controller.SetIndeterminate();

        Assert.True(result);
        Assert.Equal([new NativeCall("State", 123, WindowsTaskbarProgressState.Indeterminate, 0, 0)], native.Calls);
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(42.5, 4250)]
    [InlineData(100, 10000)]
    [InlineData(250, 10000)]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    public void SetProgress_ClampsPercentAndUsesScaledShellValue(double percent, ulong expectedCompleted)
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(456);

        var result = controller.SetProgress(percent);

        Assert.True(result);
        Assert.Equal(
        [
            new NativeCall("State", 456, WindowsTaskbarProgressState.Normal, 0, 0),
            new NativeCall("Value", 456, null, expectedCompleted, 10_000)
        ], native.Calls);
    }

    [Fact]
    public void SetProgress_RepeatedSameValue_DoesNotSpamNativeShell()
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(789);

        Assert.True(controller.SetProgress(25));
        Assert.True(controller.SetProgress(25));

        Assert.Equal(
        [
            new NativeCall("State", 789, WindowsTaskbarProgressState.Normal, 0, 0),
            new NativeCall("Value", 789, null, 2500, 10_000)
        ], native.Calls);
    }

    [Fact]
    public void Clear_ForAttachedWindow_RemovesTaskbarProgress()
    {
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(321);
        controller.SetProgress(75);
        native.Calls.Clear();

        var result = controller.Clear();

        Assert.True(result);
        Assert.Equal([new NativeCall("State", 321, WindowsTaskbarProgressState.NoProgress, 0, 0)], native.Calls);
    }

    [Theory]
    [InlineData((int)WindowsTaskbarProgressState.Paused)]
    [InlineData((int)WindowsTaskbarProgressState.Error)]
    public void NonNormalStates_AreForwardedToNativeShell(int stateValue)
    {
        var state = (WindowsTaskbarProgressState)stateValue;
        var native = new FakeWindowsTaskbarProgressNative();
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(654);

        var result = state == WindowsTaskbarProgressState.Paused
            ? controller.SetPaused()
            : controller.SetError();

        Assert.True(result);
        Assert.Equal([new NativeCall("State", 654, state, 0, 0)], native.Calls);
    }

    [Fact]
    public void NativeFailure_DoesNotCacheFailedState()
    {
        var native = new FakeWindowsTaskbarProgressNative { FailNextStateCall = true };
        using var controller = new WindowsTaskbarProgressController(native);
        controller.Attach(987);

        Assert.False(controller.SetIndeterminate());
        Assert.True(controller.SetIndeterminate());

        Assert.Equal(
        [
            new NativeCall("State", 987, WindowsTaskbarProgressState.Indeterminate, 0, 0),
            new NativeCall("State", 987, WindowsTaskbarProgressState.Indeterminate, 0, 0)
        ], native.Calls);
    }

    private sealed class FakeWindowsTaskbarProgressNative : IWindowsTaskbarProgressNative
    {
        public List<NativeCall> Calls { get; } = [];

        public bool FailNextStateCall { get; set; }

        public bool TrySetProgressState(nint windowHandle, WindowsTaskbarProgressState state)
        {
            Calls.Add(new NativeCall("State", windowHandle, state, 0, 0));
            if (!FailNextStateCall)
                return true;

            FailNextStateCall = false;
            return false;
        }

        public bool TrySetProgressValue(nint windowHandle, ulong completed, ulong total)
        {
            Calls.Add(new NativeCall("Value", windowHandle, null, completed, total));
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed record NativeCall(
        string Kind,
        nint WindowHandle,
        WindowsTaskbarProgressState? State,
        ulong Completed,
        ulong Total);
}
