// Packages a self-contained Linux build of LlamaTree (dotnet publish -f net10.0 -r linux-x64 --self-contained) as
//   llamatree_<version>_amd64.deb          Debian, Ubuntu, Mint...: apt installs WebKitGTK and libnotify with it
//   llamatree-<version>-1.x86_64.rpm       Fedora, openSUSE...: dnf / zypper do the same
//   LlamaTree-<version>-linux-x64.tar.gz   anything else: unpack and run (WebKitGTK and libnotify installed by hand)
// Written in plain .NET, so it runs on the Windows build agent with no Linux tools.
//
//   dotnet run tools/package-linux.cs -- <publish folder> <version> <output folder> [x64|arm64]
//
// The packages put the app in /opt/llamatree, a `llamatree` command in /usr/bin, and a menu entry with the app's icon.
// Nothing needs .NET or Node.js on the machine: the build is self-contained.

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 3) { Console.Error.WriteLine("Usage: dotnet run tools/package-linux.cs -- <publish folder> <version> <output folder> [x64|arm64]"); return 2; }
var publish = Path.GetFullPath(args[0]);
var version = args[1];
var output = Path.GetFullPath(args[2]);
var arch = args.Length > 3 ? args[3] : "x64";
var (debArch, rpmArch) = arch switch
{
    "x64" => ("amd64", "x86_64"),
    "arm64" => ("arm64", "aarch64"),
    _ => throw new ArgumentException($"Unknown architecture {arch}: x64 or arm64."),
};
if (!File.Exists(Path.Combine(publish, "LlamaTree"))) throw new FileNotFoundException($"{publish} has no LlamaTree: publish the net10.0 build for linux-{arch} there first.");
if (!File.Exists(Path.Combine(publish, "Photino.Native.so"))) throw new FileNotFoundException($"{publish} has no Photino.Native.so: publish it with -r linux-{arch}.");
Directory.CreateDirectory(output);

const string Name = "llamatree";
const string Maintainer = "LLAMACHANT TECHNOLOGY LTD <support@llamachant.com>";
const string Homepage = "https://www.llamatree.dev";
const string Summary = "Many Claude Code and Codex sessions in one window, with Visual Studio-style Git views";
const string Description = """
    LlamaTree runs many local Claude Code and Codex sessions side by side, each in a real terminal,
    with a Visual Studio-style Git Changes and Git Repository view and diff viewer for each session's worktree.
    """;
var mtime = DateTimeOffset.UtcNow;

// ---------------------------------------------------------------- what goes where

var items = new List<Item>();
void Dir(string path) => items.Add(new Item(path, null, 0b111_101_101, true));
void File_(string path, byte[] content, int mode) => items.Add(new Item(path, content, mode, false));

Dir("/opt/llamatree");
foreach (var dir in Directory.EnumerateDirectories(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    Dir("/opt/llamatree/" + Rel(dir));
foreach (var file in Directory.EnumerateFiles(publish, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
{
    var rel = Rel(file);
    // A Windows build agent keeps no Unix permissions: the programs are made executable here.
    var executable = rel is "LlamaTree" or "createdump";
    File_("/opt/llamatree/" + rel, File.ReadAllBytes(file), executable ? 0b111_101_101 : 0b110_100_100);
}
// A script rather than a link, so the app always starts from its own folder (it finds its page there).
File_("/usr/bin/llamatree", Encoding.UTF8.GetBytes("#!/bin/sh\nexec /opt/llamatree/LlamaTree \"$@\"\n"), 0b111_101_101);
File_("/usr/share/applications/llamatree.desktop", Encoding.UTF8.GetBytes($"""
    [Desktop Entry]
    Type=Application
    Name=LlamaTree
    GenericName=Claude Code sessions
    Comment={Summary}
    Exec=llamatree
    Icon=llamatree
    Terminal=false
    Categories=Development;IDE;
    Keywords=claude;codex;terminal;git;

    """), 0b110_100_100);
File_("/usr/share/icons/hicolor/256x256/apps/llamatree.png", File.ReadAllBytes(Path.Combine(publish, "app.png")), 0b110_100_100);

var files = items.OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
var installedSize = files.Where(f => !f.IsDir).Sum(f => (long)f.Content!.Length);

// Each package says how it was installed, so the app knows how to update itself (see UpdateService.LinuxInstall).
List<Item> As(string kind) =>
    [.. files.Append(new Item("/opt/llamatree/package-type", Encoding.ASCII.GetBytes(kind + "\n"), 0b110_100_100, false))
        .OrderBy(i => i.Path, StringComparer.Ordinal)];

Deb();
Rpm();
TarGz();
return 0;

string Rel(string full) => Path.GetRelativePath(publish, full).Replace('\\', '/');

// ---------------------------------------------------------------- .deb

void Deb()
{
    var files = As("deb");
    var control = $"""
        Package: {Name}
        Version: {version}
        Architecture: {debArch}
        Maintainer: {Maintainer}
        Installed-Size: {(installedSize + 1023) / 1024}
        Depends: libwebkit2gtk-4.1-0, libnotify4, libgtk-3-0, libc6, libgcc-s1, libstdc++6, ca-certificates
        Recommends: git, xclip | wl-clipboard
        Section: devel
        Priority: optional
        Homepage: {Homepage}
        Description: {Summary}
        {string.Join("\n", Description.Trim().Split('\n').Select(l => " " + l.Trim()))}

        """;
    var md5sums = string.Concat(files.Where(f => !f.IsDir).Select(f => $"{Convert.ToHexStringLower(MD5.HashData(f.Content!))}  {f.Path.TrimStart('/')}\n"));
    var controlTar = TarGzip([
        new Item("/control", Encoding.UTF8.GetBytes(control), 0b110_100_100, false),
        new Item("/md5sums", Encoding.UTF8.GetBytes(md5sums), 0b110_100_100, false),
    ], ".", withRootDir: true);
    var dataTar = TarGzip(files, ".", withRootDir: true);

    var path = Path.Combine(output, $"{Name}_{version}_{debArch}.deb");
    using var ar = File.Create(path);
    ar.Write("!<arch>\n"u8);
    ArMember(ar, "debian-binary", "2.0\n"u8.ToArray());
    ArMember(ar, "control.tar.gz", controlTar);
    ArMember(ar, "data.tar.gz", dataTar);
    Console.WriteLine($"  {path}");
}

void ArMember(Stream ar, string name, byte[] content)
{
    var header = $"{name,-16}{mtime.ToUnixTimeSeconds(),-12}{0,-6}{0,-6}{"100644",-8}{content.Length,-10}`\n";
    ar.Write(Encoding.ASCII.GetBytes(header));
    ar.Write(content);
    if (content.Length % 2 == 1) ar.WriteByte((byte)'\n');
}

// ---------------------------------------------------------------- .tar.gz

void TarGz()
{
    var top = $"LlamaTree-{version}-linux-{arch}";
    var files = As("tar");
    var entries = files.Where(f => f.Path.StartsWith("/opt/llamatree/", StringComparison.Ordinal))
        .Select(f => f with { Path = f.Path["/opt/llamatree".Length..] })
        .Append(new Item("/llamatree.desktop", files.First(f => f.Path.EndsWith(".desktop")).Content!, 0b110_100_100, false));
    var path = Path.Combine(output, top + ".tar.gz");
    File.WriteAllBytes(path, TarGzip(entries, top, withRootDir: true));
    Console.WriteLine($"  {path}");
}

/// <summary>A gzipped tar of the items under <paramref name="root"/>, owned by root, with the modes given.</summary>
byte[] TarGzip(IEnumerable<Item> entries, string root, bool withRootDir)
{
    using var buffer = new MemoryStream();
    using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var tar = new TarWriter(gz, TarEntryFormat.Gnu, leaveOpen: true))
    {
        if (withRootDir) tar.WriteEntry(Entry(new Item("", null, 0b111_101_101, true), root));
        foreach (var item in entries) tar.WriteEntry(Entry(item, root));
    }
    return buffer.ToArray();

    GnuTarEntry Entry(Item item, string top)
    {
        var name = top + item.Path + (item.IsDir ? "/" : "");
        var entry = new GnuTarEntry(item.IsDir ? TarEntryType.Directory : TarEntryType.RegularFile, name)
        {
            Mode = (UnixFileMode)item.Mode, Uid = 0, Gid = 0, UserName = "root", GroupName = "root", ModificationTime = mtime,
        };
        if (!item.IsDir) entry.DataStream = new MemoryStream(item.Content!);
        return entry;
    }
}

// ---------------------------------------------------------------- .rpm

// The RPM v3 file layout rpm 4 and 6 read: a lead, a signature header (sizes and digests of what follows), the main header
// (the package and its files), then a gzipped cpio archive of the files.
void Rpm()
{
    // Only the app's own folders belong to it; /usr/bin and the like belong to the system.
    var owned = As("rpm").Where(f => !f.IsDir || f.Path.StartsWith("/opt/llamatree", StringComparison.Ordinal)).ToList();
    var payload = Cpio(owned);
    var compressed = Gzip(payload);

    var dirNames = owned.Select(f => f.Path[..(f.Path.LastIndexOf('/') + 1)]).Distinct().ToList();
    var h = new Header(63);
    h.StringArray(100, ["C"]);                                  // HEADERI18NTABLE
    h.String(1000, Name);
    h.String(1001, version);
    h.String(1002, "1");                                        // RELEASE
    h.I18n(1004, Summary);
    h.I18n(1005, Description.Trim());
    h.Int32(1006, [(int)mtime.ToUnixTimeSeconds()]);            // BUILDTIME
    h.String(1007, "llamatree-build");                          // BUILDHOST
    h.Int32(1009, [(int)installedSize]);                        // SIZE
    h.String(1010, "LlamaTree " + version);                     // DISTRIBUTION
    h.String(1011, "Llamachant Technology Ltd");                // VENDOR
    h.String(1014, "Apache-2.0");                               // LICENSE
    h.String(1015, Maintainer);                                 // PACKAGER
    h.I18n(1016, "Development/Tools");                          // GROUP
    h.String(1020, Homepage);                                   // URL
    h.String(1021, "linux");                                    // OS
    h.String(1022, rpmArch);                                    // ARCH
    h.Int32(1028, owned.Select(f => f.IsDir ? 4096 : f.Content!.Length).ToArray());           // FILESIZES
    h.Int16(1030, owned.Select(f => (short)((f.IsDir ? 0x4000 : 0x8000) | f.Mode)).ToArray()); // FILEMODES
    h.Int16(1033, new short[owned.Count]);                                                      // FILERDEVS
    h.Int32(1034, Enumerable.Repeat((int)mtime.ToUnixTimeSeconds(), owned.Count).ToArray());    // FILEMTIMES
    h.StringArray(1035, owned.Select(f => f.IsDir ? "" : Convert.ToHexStringLower(SHA256.HashData(f.Content!))).ToArray()); // FILEDIGESTS
    h.StringArray(1036, owned.Select(_ => "").ToArray());                                       // FILELINKTOS
    h.Int32(1037, new int[owned.Count]);                                                        // FILEFLAGS
    h.StringArray(1039, owned.Select(_ => "root").ToArray());                                   // FILEUSERNAME
    h.StringArray(1040, owned.Select(_ => "root").ToArray());                                   // FILEGROUPNAME
    h.String(1044, $"{Name}-{version}-1.src.rpm");                                              // SOURCERPM: a binary package
    // PROVIDES, then REQUIRES: the libraries by soname, which Fedora's and openSUSE's packages both provide.
    h.Int32(1112, [8]);                                                                         // PROVIDEFLAGS: =
    h.StringArray(1047, [Name]);                                                                // PROVIDENAME
    h.StringArray(1113, [$"{version}-1"]);                                                      // PROVIDEVERSION
    string[] requires =
    [
        "libwebkit2gtk-4.1.so.0()(64bit)", "libnotify.so.4()(64bit)", "libgtk-3.so.0()(64bit)",
        "rpmlib(CompressedFileNames)", "rpmlib(FileDigests)", "rpmlib(PayloadFilesHavePrefix)",
    ];
    const int rpmlibLessEqual = 0x01000000 | 0x02 | 0x08;
    h.Int32(1048, [0, 0, 0, rpmlibLessEqual, rpmlibLessEqual, rpmlibLessEqual]);                // REQUIREFLAGS
    h.StringArray(1049, requires);                                                              // REQUIRENAME
    h.StringArray(1050, ["", "", "", "3.0.4-1", "4.6.0-1", "4.0-1"]);                          // REQUIREVERSION
    h.Int32(1095, Enumerable.Repeat(1, owned.Count).ToArray());                                 // FILEDEVICES
    h.Int32(1096, Enumerable.Range(1, owned.Count).ToArray());                                  // FILEINODES
    h.StringArray(1097, owned.Select(_ => "").ToArray());                                       // FILELANGS
    h.Int32(1116, owned.Select(f => dirNames.IndexOf(f.Path[..(f.Path.LastIndexOf('/') + 1)])).ToArray()); // DIRINDEXES
    h.StringArray(1117, owned.Select(f => f.Path[(f.Path.LastIndexOf('/') + 1)..]).ToArray()); // BASENAMES
    h.StringArray(1118, dirNames.ToArray());                                                    // DIRNAMES
    h.String(1124, "cpio");                                                                     // PAYLOADFORMAT
    h.String(1125, "gzip");                                                                     // PAYLOADCOMPRESSOR
    h.String(1126, "9");                                                                        // PAYLOADFLAGS
    h.Int32(5011, [8]);                                                                         // FILEDIGESTALGO: SHA-256
    h.String(5062, "utf-8");                                                                    // ENCODING
    // The payload's own digests, as rpmbuild writes them: RPM 6 no longer takes the signature's MD5 as covering the
    // payload, and refuses a package without these ("does not verify: no digest").
    h.StringArray(5092, [Convert.ToHexStringLower(SHA256.HashData(compressed))]);              // PAYLOADDIGEST
    h.Int32(5093, [8]);                                                                         // PAYLOADDIGESTALGO: SHA-256
    h.StringArray(5097, [Convert.ToHexStringLower(SHA256.HashData(payload))]);                 // PAYLOADDIGESTALT: uncompressed
    var header = h.ToBytes();

    var s = new Header(62);
    s.String(269, Convert.ToHexStringLower(SHA1.HashData(header)));                             // SHA1 of the header
    s.String(273, Convert.ToHexStringLower(SHA256.HashData(header)));                           // SHA256 of the header
    s.Int32(1000, [header.Length + compressed.Length]);                                         // SIZE
    s.Bin(1004, MD5.HashData([.. header, .. compressed]));                                      // MD5 of header and payload
    s.Int32(1007, [payload.Length]);                                                            // PAYLOADSIZE
    var signature = s.ToBytes();

    var path = Path.Combine(output, $"{Name}-{version}-1.{rpmArch}.rpm");
    using var rpm = File.Create(path);
    var lead = new byte[96];
    new byte[] { 0xed, 0xab, 0xee, 0xdb, 3, 0 }.CopyTo(lead, 0);
    lead[7] = 0;                                                                                // type: binary
    lead[9] = (byte)(rpmArch == "x86_64" ? 1 : 19);                                            // archnum
    Encoding.ASCII.GetBytes($"{Name}-{version}-1").CopyTo(lead, 10);
    lead[77] = 1;                                                                               // osnum: Linux
    lead[79] = 5;                                                                               // signature type: header-style
    rpm.Write(lead);
    rpm.Write(signature);
    rpm.Write(new byte[(8 - signature.Length % 8) % 8]);                                        // the signature is padded to 8 bytes
    rpm.Write(header);
    rpm.Write(compressed);
    Console.WriteLine($"  {path}");
}

static byte[] Gzip(byte[] data)
{
    using var buffer = new MemoryStream();
    using (var gz = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true)) gz.Write(data);
    return buffer.ToArray();
}

/// <summary>A cpio "newc" archive, with paths as ./path (rpm's PayloadFilesHavePrefix).</summary>
byte[] Cpio(List<Item> entries)
{
    using var cpio = new MemoryStream();
    var ino = 0;
    void Write(string name, int mode, byte[] data, int nlink)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var fields = new[] { ++ino, mode, 0, 0, nlink, (int)mtime.ToUnixTimeSeconds(), data.Length, 0, 0, 0, 0, nameBytes.Length, 0 };
        cpio.Write(Encoding.ASCII.GetBytes("070701" + string.Concat(fields.Select(f => f.ToString("x8")))));
        cpio.Write(nameBytes);
        Pad();
        cpio.Write(data);
        Pad();
    }
    void Pad() { while (cpio.Length % 4 != 0) cpio.WriteByte(0); }
    foreach (var e in entries) Write("." + e.Path, (e.IsDir ? 0x4000 : 0x8000) | e.Mode, e.IsDir ? [] : e.Content!, e.IsDir ? 2 : 1);
    ino = -1;
    Write("TRAILER!!!", 0, [], 1);
    return cpio.ToArray();
}

record Item(string Path, byte[]? Content, int Mode, bool IsDir);

/// <summary>An rpm header: index entries (sorted by tag) and their data, inside an immutable region (tag 62 or 63).</summary>
sealed class Header(int regionTag)
{
    private readonly SortedDictionary<int, (int Type, int Count, byte[] Data)> entries = [];

    public void String(int tag, string value) => entries[tag] = (6, 1, Encoding.UTF8.GetBytes(value + "\0"));
    public void I18n(int tag, string value) => entries[tag] = (9, 1, Encoding.UTF8.GetBytes(value + "\0"));
    public void StringArray(int tag, string[] values) => entries[tag] = (8, values.Length, Encoding.UTF8.GetBytes(string.Concat(values.Select(v => v + "\0"))));
    public void Bin(int tag, byte[] value) => entries[tag] = (7, value.Length, value);
    public void Int32(int tag, int[] values) => entries[tag] = (4, values.Length, values.SelectMany(BigEndian).ToArray());
    public void Int16(int tag, short[] values) => entries[tag] = (3, values.Length, values.SelectMany(v => new[] { (byte)(v >> 8), (byte)v }).ToArray());

    private static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    public byte[] ToBytes()
    {
        var store = new MemoryStream();
        var index = new List<byte[]>();
        foreach (var (tag, (type, n, data)) in entries)
        {
            var align = type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 };
            while (store.Length % align != 0) store.WriteByte(0);
            index.Add([.. BigEndian(tag), .. BigEndian(type), .. BigEndian((int)store.Length), .. BigEndian(n)]);
            store.Write(data);
        }
        // The region: its entry comes first and points at a trailer at the end of the data, which in turn points back
        // (as a negative offset) over the whole index, region entry included.
        var count = index.Count + 1;
        var trailerOffset = (int)store.Length;
        store.Write([.. BigEndian(regionTag), .. BigEndian(7), .. BigEndian(-count * 16), .. BigEndian(16)]);
        index.Insert(0, [.. BigEndian(regionTag), .. BigEndian(7), .. BigEndian(trailerOffset), .. BigEndian(16)]);

        var result = new MemoryStream();
        result.Write([0x8e, 0xad, 0xe8, 0x01, 0, 0, 0, 0]);
        result.Write(BigEndian(count));
        result.Write(BigEndian((int)store.Length));
        foreach (var e in index) result.Write(e);
        result.Write(store.ToArray());
        return result.ToArray();
    }
}
