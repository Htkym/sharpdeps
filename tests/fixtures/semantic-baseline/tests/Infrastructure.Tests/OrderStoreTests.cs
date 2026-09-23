using Domain;
using Infrastructure;

namespace Infrastructure.Tests;

public sealed class OrderStoreTests
{
    public void SavesOrder()
    {
        IOrderStore store = new OrderStore();
        store.Save(new Order { Id = "1" });
    }
}
