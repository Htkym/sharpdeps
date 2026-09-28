using Core;
using Util;

namespace App.Services;

public sealed class OrderService
{
    public static void Run()
    {
        var order = new Order { Id = "1" };
        Helpers.Format(order);
    }
}
