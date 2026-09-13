internal interface IRepo { }
internal sealed class Repo : IRepo { }
internal static class Composition
{
    public static void Register(IServiceCollection services) => services.AddScoped<IRepo, Repo>();
}
