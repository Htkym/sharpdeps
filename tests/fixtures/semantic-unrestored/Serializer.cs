using Newtonsoft.Json;

namespace Unrestored;

public sealed class Serializer
{
    public string Serialize(object value) => JsonConvert.SerializeObject(value);
}
