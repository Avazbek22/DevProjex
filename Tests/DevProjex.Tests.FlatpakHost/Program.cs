using DevProjex.Kernel;
using DevProjex.Terminal.CommandLine;

var marker = args[0];
using var sandbox = FlatpakEnvironment.UseForTesting(FlatpakEnvironment.Detect(
	true, Environment.GetEnvironmentVariable, path => path == "/.flatpak-info" && File.Exists(marker)));
using var cancellation = TerminalCancellationCoordinator.Register();
var environment = new InvocationEnvironment(hasAttachedConsole: false);
return await new TerminalApplication(environment).RunAsync(args[1..], cancellation.Token);
