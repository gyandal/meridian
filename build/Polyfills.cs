// Compiled only into the net8.0 builds of the packages (see Directory.Build.targets): small stand-ins for APIs
// that arrived in later .NET versions. The net9.0 and net10.0 builds use the real thing.
#if !NET9_0_OR_GREATER
#pragma warning disable CS9216 // Monitor on the Lock itself is exactly what this stand-in means to do.
namespace System.Threading
{
    /// <summary>
    /// .NET 9's lock type, on .NET 8. The compiler turns <c>lock (gate)</c> on a <c>Lock</c> into
    /// <see cref="EnterScope"/>; here that's the classic Monitor, which is what <c>lock</c> on any object used.
    /// </summary>
    internal sealed class Lock
    {
        public Scope EnterScope()
        {
            Monitor.Enter(this);
            return new Scope(this);
        }

        public readonly ref struct Scope(Lock owner)
        {
            public void Dispose() => Monitor.Exit(owner);
        }
    }
}
#endif
