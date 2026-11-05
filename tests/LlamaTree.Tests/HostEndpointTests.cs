using LlamaTree.Hosting.Link;
using LlamaTree.Terminal;

namespace LlamaTree.Tests;

/// <summary>
/// The session host's socket (macOS and Linux) goes in a folder only you can use: never the shared /tmp, where another
/// account could take the path first and keep the host from listening.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public class HostEndpointTests : IDisposable
{
    private const string Sock = "llamatree.host.sock";
    private const int LinuxLimit = 107;
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string root = Path.Combine(Path.GetTempPath(), "lt-sock-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string sharedRoot, data;

    public HostEndpointTests()
    {
        sharedRoot = Path.Combine(root, "tmp");
        data = Path.Combine(root, "data", "LlamaTree", "run");
        Directory.CreateDirectory(sharedRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static uint Me => Libc.Instance.EffectiveUserId();

    private string Folder(string name, UnixFileMode mode)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        File.SetUnixFileMode(path, mode);
        return path;
    }

    /// <summary>A data folder too deep for a socket's path.</summary>
    private string LongData() => Path.Combine(root, new string('d', 60), new string('e', 40), "run");

    private string PathFor(string?[] runtime, string dataFolder, uint? uid = null) =>
        HostEndpoint.SocketPath(Sock, runtime, () => dataFolder, sharedRoot, uid ?? Me, LinuxLimit);

    [UnixFact]
    public void A_private_runtime_folder_is_used()
    {
        var runtime = Folder("run-user", OwnerOnly);
        Assert.Equal(Path.Combine(runtime, Sock), PathFor([runtime, null], data));
        Assert.False(Directory.Exists(data));   // nothing made elsewhere
    }

    [UnixFact]
    public void The_second_private_folder_is_used_when_the_first_is_missing_or_relative()
    {
        var tmpdir = Folder("T", OwnerOnly);
        Assert.Equal(Path.Combine(tmpdir, Sock), PathFor([Path.Combine(root, "missing"), tmpdir + "/"], data));
        Assert.Equal(Path.Combine(tmpdir, Sock), PathFor(["relative/run", tmpdir], data));
    }

    [UnixFact]
    public void Without_a_runtime_folder_the_data_folder_is_made_private()
    {
        Assert.Equal(Path.Combine(data, Sock), PathFor([null, ""], data));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(data));
    }

    [UnixFact]
    public void A_runtime_folder_others_can_reach_is_not_used()
    {
        // /tmp itself, say, as $TMPDIR: shared, so not used; nor one merely readable by others.
        var shared = Folder("shared", (UnixFileMode)0x3FF);   // 1777
        var readable = Folder("readable", OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        Assert.Equal(Path.Combine(data, Sock), PathFor([shared, readable], data));
        Assert.NotEqual(OwnerOnly, File.GetUnixFileMode(readable) & (UnixFileMode)0x1FF);   // left as it was
    }

    [UnixFact]
    public void A_runtime_folder_that_is_a_link_is_not_used()
    {
        var target = Folder("target", OwnerOnly);
        var link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, target);
        Assert.Equal(Path.Combine(data, Sock), PathFor([link], data));
    }

    [UnixFact]
    public void A_data_folder_too_long_for_a_socket_falls_back_to_a_private_folder_in_tmp()
    {
        var longRuntime = Folder(new string('r', 100), OwnerOnly);
        var path = PathFor([longRuntime], LongData());
        var shared = Path.Combine(sharedRoot, "llamatree-" + Me);
        Assert.Equal(Path.Combine(shared, Sock), path);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(shared));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(path) <= LinuxLimit);
        Assert.Equal(path, PathFor([longRuntime], LongData()));   // found again as it is
    }

    [UnixFact]
    public void A_folder_of_yours_in_tmp_that_others_can_reach_is_closed_up()
    {
        var shared = Path.Combine(sharedRoot, "llamatree-" + Me);
        Directory.CreateDirectory(shared);
        File.SetUnixFileMode(shared, (UnixFileMode)0x1FF);   // 0777
        Assert.Equal(Path.Combine(shared, Sock), PathFor([], LongData()));
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(shared));
    }

    [UnixFact]
    public void A_folder_in_tmp_another_account_owns_is_refused()
    {
        // Made by this user, so for a pretend other user it's someone else's: as an attacker's folder would be.
        var other = Me + 1;
        var shared = Path.Combine(sharedRoot, "llamatree-" + other);
        Directory.CreateDirectory(shared, OwnerOnly);
        var ex = Assert.Throws<IOException>(() => PathFor([], LongData(), other));
        Assert.Contains(shared, ex.Message);
        Assert.Contains("another account", ex.Message);
    }

    [UnixFact]
    public void A_link_in_tmp_is_refused_and_not_followed()
    {
        var target = Folder("victim", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        var shared = Path.Combine(sharedRoot, "llamatree-" + Me);
        Directory.CreateSymbolicLink(shared, target);
        var ex = Assert.Throws<IOException>(() => PathFor([], LongData()));
        Assert.Contains("is a link", ex.Message);
        Assert.NotEqual(OwnerOnly, File.GetUnixFileMode(target) & (UnixFileMode)0x1FF);   // not changed through the link
    }

    [UnixFact]
    public void A_dangling_link_in_tmp_is_refused()
    {
        var shared = Path.Combine(sharedRoot, "llamatree-" + Me);
        File.CreateSymbolicLink(shared, Path.Combine(root, "nowhere"));
        Assert.Throws<IOException>(() => PathFor([], LongData()));
        Assert.False(Directory.Exists(Path.Combine(root, "nowhere")));
    }

    [UnixFact]
    public void A_data_folder_that_is_a_link_falls_back_to_tmp()
    {
        var target = Folder("elsewhere", OwnerOnly);
        var linked = Path.Combine(root, "linked");
        Directory.CreateSymbolicLink(linked, target);
        Assert.Equal(Path.Combine(sharedRoot, "llamatree-" + Me, Sock), PathFor([], linked));
    }

    [UnixFact]
    public void Lstat_tells_the_owner_and_a_link()
    {
        var libc = Libc.Instance;
        Assert.True(libc.CanLStat);
        var folder = Folder("mine", OwnerOnly);
        Assert.True(libc.LStat(folder, out var owner, out var mode));
        Assert.Equal(Me, owner);
        Assert.Equal(0x4000u | 0x1C0u, mode);   // a folder, 0700
        var link = Path.Combine(root, "mine-link");
        Directory.CreateSymbolicLink(link, folder);
        Assert.True(libc.LStat(link, out _, out mode));
        Assert.Equal(0xA000u, mode & 0xF000);
        Assert.False(libc.LStat(Path.Combine(root, "missing"), out _, out _));
    }
}
