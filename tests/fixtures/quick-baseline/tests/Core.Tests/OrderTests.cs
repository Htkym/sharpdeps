using App.Services;
using Core.Legacy;
using Legacy = Core.Legacy;

namespace Core.Tests;

public sealed class OrderTests
{
    public void CreatesLegacyStore()
    {
        var store = new LegacyStore();
        store.Save();
    }
}
