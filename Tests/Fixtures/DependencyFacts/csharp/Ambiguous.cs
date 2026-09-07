using Alpha;
using Beta;

namespace Alpha { public sealed class User { } }
namespace Beta { public sealed class User { } }

internal sealed class AmbiguousHolder
{
    public User Value { get; } = new User();
}
