namespace LlamaTree.Hosting;

/// <summary>The app's named locks: one window per profile, one session host per profile.</summary>
internal static class InstanceLock
{
    /// <summary>
    /// Takes the lock <paramref name="name"/>; <paramref name="first"/> says whether no one else held it. On macOS and
    /// Linux a named lock is otherwise only seen within one login session, so a host started over SSH wouldn't see one
    /// started from the desktop; there it's this user's, across sessions. Windows keeps the plain name, which the
    /// installer checks (AppMutex).
    /// </summary>
    public static Mutex Take(string name, out bool first) =>
        OperatingSystem.IsWindows()
            ? new Mutex(true, name, out first)
            : new Mutex(true, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out first);
}
