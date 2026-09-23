using Domain;

namespace Infrastructure;

public sealed class OrderStore : IOrderStore
{
    public void Save(Order order)
    {
        SharedLog.Write($"saving {order.Id}");
    }
}
