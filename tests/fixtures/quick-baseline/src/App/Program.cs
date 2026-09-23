using App.Services;
using Core;

namespace App;

internal static class Program
{
    private static void Main()
    {
        OrderService.Run();
    }
}
