#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Polyfill for init-only properties in netstandard2.1
    /// </summary>
    internal static class IsExternalInit { }
}
#endif
