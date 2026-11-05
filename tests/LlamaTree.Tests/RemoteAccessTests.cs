using LlamaTree.Hosting;

namespace LlamaTree.Tests;

/// <summary>"Let my other computers connect": what's taken for a public key, and what's put in the scripts that run elevated.</summary>
public sealed class RemoteAccessTests
{
    private const string Ed25519 = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIKGw/qgcEgh1GFOX3uspytroBzfMG/gBHaPkRpnfRdf7";

    [Fact]
    public void A_public_key_is_taken_with_its_comment_and_fingerprint()
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = RemoteAccess.ParseKey("  " + Ed25519 + " dave@laptop\r\n");
        Assert.NotNull(key);
        Assert.Equal(Ed25519 + " dave@laptop", key.Line);
        Assert.Equal("dave@laptop", key.Comment);
        Assert.Equal("SHA256:vsux70fgKRvlzR+lG3896oBIl2Zh/CkhWVas82KMKOk", key.Fingerprint);   // as ssh-keygen -lf says
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a key")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]   // the wrong half
    [InlineData("command=\"rm -rf /\" " + Ed25519)]   // options in front
    [InlineData("ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIKGw/qgcEgh1GFOX3uspytroBzfMG/gBHaPkRpnfRdf7")]   // its data is an ed25519 key's
    [InlineData("ssh-ed25519 not-base64!")]
    [InlineData(Ed25519 + "\n" + Ed25519)]   // one key at a time
    public void Anything_else_isnt_taken_for_a_key(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Null(RemoteAccess.ParseKey(text));
    }

    [Theory]
    [InlineData("it's mine")]
    [InlineData("a\"b")]
    [InlineData("`$(whoami)`")]
    [InlineData("caf\u00e9")]
    public void A_comment_that_isnt_plain_text_is_left_off(string comment)
    {
        if (!OperatingSystem.IsWindows()) return;
        var key = RemoteAccess.ParseKey(Ed25519 + " " + comment);
        Assert.NotNull(key);
        Assert.Equal(Ed25519, key.Line);
        Assert.Equal("", key.Comment);
    }

    [Fact]
    public void Text_in_an_elevated_script_is_a_literal()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("'C:\\Users\\O''Brien\\AppData'", RemoteAccess.Quote("C:\\Users\\O'Brien\\AppData"));
    }

    [Fact]
    public void The_login_name_drops_this_computer_s_own_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("dave", RemoteAccess.LoginName(Environment.MachineName + "\\dave"));
        Assert.Equal("CORP\\dave", RemoteAccess.LoginName("CORP\\dave"));
    }

    [Theory]
    [InlineData("# comment\n#PasswordAuthentication yes\nPort 22\n", true)]   // sshd's default
    [InlineData("PasswordAuthentication no\nPort 22\n", false)]
    [InlineData("  passwordauthentication   NO  \n", false)]
    [InlineData("PasswordAuthentication yes\nPasswordAuthentication no\n", true)]   // the first counts
    [InlineData("Match Group administrators\n  PasswordAuthentication no\n", true)]   // only for some
    public void Password_sign_in_is_read_as_sshd_reads_it(string config, bool on)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(on, RemoteAccess.PasswordSignIn(config));
    }

    [Theory]
    [InlineData(6, "22", null, null, true)]                    // a rule of your own for SSH
    [InlineData(6, "80, 22,443", "", "", true)]                // in a list
    [InlineData(6, "20-30", null, null, true)]                 // in a range
    [InlineData(6, "22", @"C:\Windows\System32\OpenSSH\sshd.exe", null, true)]
    [InlineData(6, "22", null, "sshd", true)]
    [InlineData(6, "22", null, "*", true)]
    [InlineData(17, "22", null, null, false)]                  // UDP
    [InlineData(6, "2222", null, null, false)]
    [InlineData(6, "23-30", null, null, false)]
    [InlineData(6, "*", null, null, false)]                    // every port: nearly always one app's own
    [InlineData(6, null, null, null, false)]
    [InlineData(6, "22", @"C:\Program Files\Other\other.exe", null, false)]   // kept to another program
    [InlineData(6, "22", null, "TermService", false)]          // or another service
    public void A_rule_lets_ssh_in_when_it_names_port_22_for_tcp(int protocol, string? ports, string? application, string? service, bool lets)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(lets, RemoteAccess.LetsSshIn(protocol, ports, application, service));
    }

    [Fact]
    public void The_firewall_s_rules_can_be_read_without_administrator_rights()
    {
        if (!OperatingSystem.IsWindows()) return;
        var rules = RemoteAccess.OtherSshRules();   // null when reading them failed (the COM call, or a property's type)
        Assert.NotNull(rules);
        Assert.DoesNotContain(RemoteAccess.OwnRule, rules);
    }

    [Fact]
    public void Turn_on_makes_the_firewall_rule_by_the_name_the_other_rules_are_told_from() =>
        Assert.Contains("-DisplayName " + RemoteAccess.Quote(RemoteAccess.OwnRule), RemoteAccess.EnableScript(keysOnly: true, publicNetworks: false));
}
