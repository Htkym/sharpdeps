using Util;

namespace Core.Legacy;

public sealed class LegacyStore
{
    public void Save() => Helpers.Format(new Order());
}
