#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices;

/// <summary>
/// Required by compiler features that use init-only setters (records, init
/// accessors) when targeting netstandard2.0.
/// </summary>
internal static class IsExternalInit
{
}
#endif
