using Util;

namespace Core;

public static class UtilBridge
{
    public static string Describe(Order order) => Helpers.Format(order);
}
