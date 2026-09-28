namespace Domain;

public sealed class Order
{
    public string Id { get; set; } = string.Empty;
}

public interface IOrderStore
{
    void Save(Order order);
}
