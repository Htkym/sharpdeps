using Domain;

namespace Application;

public sealed class OrderService
{
    public Order Create(string id) => new() { Id = id };
}
