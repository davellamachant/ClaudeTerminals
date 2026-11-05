namespace LlamaTree.Hosting;

/// <summary>The app's named locks: one window per profile, one session host per profile.</summary>
internal static class InstanceLock
{
    /// <summary>
    /// Takes the lock <paramref name="name"/>; <paramref name="first"/> says whether no one else held it. On macOS and
    /// Linux a named lock is otherwise only seen within one login session, so a host started over SSH wouldn't see one
    /// started from the desktop; there it's this user's, across sessions. Windows keeps the plain name, which the
    /// installer checks (AppMutex); see <see cref="TakeAcrossSessions"/> for the rest.
    /// </summary>
    public static Mutex Take(string name, out bool first) =>
        OperatingSystem.IsWindows()
            ? new Mutex(true, name, out first)
            : new Mutex(true, name, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out first);

    /// <summary>
    /// On Windows, the lock <paramref name="name"/> across this user's logon sessions as well: <see cref="Take"/>'s plain
    /// name is only seen within one, and Windows' OpenSSH Server runs a connection in a session of its own, where a host
    /// started for it wouldn't see the one on the desktop. Null (and <paramref name="first"/> true) elsewhere, where
    /// <see cref="Take"/> already is. The name holds the user's SID: another user's is another lock.
    /// </summary>
    public static Mutex? TakeAcrossSessions(string name, out bool first)
    {
        first = true;
        if (!OperatingSystem.IsWindows()) return null;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "";
        return new Mutex(true, name + "." + sid, new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = false }, out first);
    }
}
