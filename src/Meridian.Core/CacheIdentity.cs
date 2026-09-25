namespace Meridian.Core;

/// <summary>
/// A stable description of an object's behaviour, used to key cached results (e.g. finished charts).
/// Two objects with the same identity must produce the same output for the same input. Objects that
/// can't promise that (a transform wrapping an anonymous lambda) simply don't implement this, and
/// anything built from them isn't cached.
/// </summary>
public interface ICacheIdentity
{
    string CacheIdentity { get; }
}
