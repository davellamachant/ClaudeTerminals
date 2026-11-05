using LlamaTree.Plugins;
using LlamaTree.Relay;

namespace LlamaTree.Tests;

public class PluginGrantsTests
{
    private static PluginInfo Plugin(string id, string hash, bool inbox = false) =>
        new(id, id, null, "index.html", "C:\\plugins\\" + id, "tab", [], ["sessions", "message"], null, inbox, hash);

    [Fact]
    public void AnswersAreAllowDenyOrAsk()
    {
        var g = new PluginGrants(null);
        Assert.Equal("ask", g.State("floor", "sessions"));
        g.Set("floor", "sessions", "allow", "H1");
        g.Set("floor", "message", "deny", "H1");
        Assert.Equal("allow", g.State("floor", "sessions"));
        Assert.Equal("deny", g.State("floor", "message"));
        g.Set("floor", "sessions", "ask", "H1");
        Assert.Equal("ask", g.State("floor", "sessions"));
        Assert.Throws<ArgumentException>(() => g.Set("floor", "sessions", "maybe", null));
    }

    [Fact]
    public void ProjectsAndHiddenSessionsDecideWhatItSees()
    {
        var g = new PluginGrants(null);
        Assert.True(g.Sees("floor", "s1", "billing"));   // no answers: every project

        g.SetProjects("floor", ["billing", PluginGrants.NoProject]);
        Assert.True(g.Sees("floor", "s1", "billing"));
        Assert.True(g.Sees("floor", "s2", PluginGrants.NoProject));
        Assert.False(g.Sees("floor", "s3", "search"));

        g.SetHidden("floor", "s1", hidden: true);
        Assert.False(g.Sees("floor", "s1", "billing"));
        g.SetHidden("floor", "s1", hidden: false);
        Assert.True(g.Sees("floor", "s1", "billing"));
    }

    [Fact]
    public void AChangedPluginJsonIsAskedAgain()
    {
        var g = new PluginGrants(null);
        g.Set("floor", "sessions", "allow", "H1");
        g.SetProjects("floor", ["billing"]);

        Assert.Empty(g.Reconcile([Plugin("floor", "H1")]));
        Assert.Equal(["floor"], g.Reconcile([Plugin("floor", "H2")]));
        Assert.Equal("ask", g.State("floor", "sessions"));
        Assert.True(g.Snapshot()["floor"].Changed);
        Assert.Equal(["billing"], g.Snapshot()["floor"].Projects);   // what it sees stays for when you allow it again

        g.Set("floor", "sessions", "allow", "H2");
        Assert.False(g.Snapshot()["floor"].Changed);
        Assert.Empty(g.Reconcile([Plugin("floor", "H2")]));
    }

    [Fact]
    public void AnswersFromBeforeHashesAreAskedAgain()
    {
        var g = new PluginGrants(null);
        g.Import(new() { ["floor"] = new PluginGrant(["sessions"], ["message"], ["billing"], ["s1"], null) });
        Assert.Equal(["floor"], g.Reconcile([Plugin("floor", "H1")]));
        Assert.Equal("ask", g.State("floor", "sessions"));
        var kept = g.Snapshot()["floor"];
        Assert.True(kept.Changed);
        Assert.Equal(["message"], kept.Denied);   // what it was refused, and what it sees, stay
        Assert.Equal(["billing"], kept.Projects);
        Assert.Equal(["s1"], kept.Hidden);

        g.Set("floor", "sessions", "allow", "H1");
        Assert.Equal("allow", g.State("floor", "sessions"));
        Assert.Equal("H1", g.Snapshot()["floor"].Manifest);
        Assert.Empty(g.Reconcile([Plugin("floor", "H1")]));
        Assert.Equal(["floor"], g.Reconcile([Plugin("floor", "H2")]));
    }

    [Fact]
    public void AnswersFromBeforeHashesOnDiskAreAskedAgain()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lt-grants-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "plugin-grants.json");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(path, """{ "floor": { "allowed": ["sessions"], "denied": [], "projects": null, "hidden": [] } }""");
            var g = new PluginGrants(path);
            Assert.Equal("ask", g.State("floor", "sessions"));   // not allowed even before it's checked against the plugin
            Assert.Empty(g.Allowed("floor"));
            Assert.Equal(["floor"], g.Reconcile([Plugin("floor", "H1")]));
            Assert.Equal("ask", new PluginGrants(path).State("floor", "sessions"));
            Assert.True(new PluginGrants(path).Snapshot()["floor"].Changed);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AGrantGoesByThePluginJsonThereNow()
    {
        using var temp = new TempFolder();
        var path = temp.File("grants.json");
        try
        {
            var before = new PluginGrants(path);
            before.Set("dash", "message", "allow", "H1");
            before.Set("old", "message", "deny", null);
            var g = new PluginGrants(path);
            Assert.Equal("allow", g.State("dash", "message"));   // it remembers a plugin.json, and none has been read to say otherwise

            // Once the plugins are read, a plugin not among them (a server's, not yet listed) is allowed nothing.
            var session = new RelayPeer("s1", "api", null, null, "Claude", true);
            var dash = new RelayPeer("plugin:dash", "Dash", null, null, "Plugin", true, true);
            g.Reconcile([Plugin("other", "H9")]);
            Assert.Equal("ask", g.State("dash", "message"));
            Assert.Equal(RelayDecision.Deny, g.RelayGate(dash, session, isReply: false));
            Assert.Empty(g.Allowed("dash"));
            Assert.Equal("deny", g.State("old", "message"));   // what you refused stays refused
            Assert.Equal(["message"], g.Snapshot()["dash"].Allowed);   // and the answer is kept for when it's listed

            g.Reconcile([Plugin("dash", "H1")]);
            Assert.Equal("allow", g.State("dash", "message"));
            Assert.Equal(RelayDecision.Allow, g.RelayGate(dash, session, isReply: false));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ImportIsOnlyIntoAnEmptyStore()
    {
        var g = new PluginGrants(null);
        g.Set("a", "sessions", "deny", null);
        g.Import(new() { ["b"] = new PluginGrant(["sessions"], [], null, [], null) });
        Assert.Equal("ask", g.State("b", "sessions"));
    }

    [Fact]
    public void SessionsAndProjectsGoneAreDropped()
    {
        var g = new PluginGrants(null);
        g.SetProjects("floor", ["billing", "search", PluginGrants.NoProject]);
        g.SetHidden("floor", "s1", true);
        g.SetHidden("floor", "s2", true);

        g.Keep(new HashSet<string> { "s2" }, new HashSet<string> { "billing" });

        Assert.Equal(["billing", PluginGrants.NoProject], g.Snapshot()["floor"].Projects);
        Assert.Equal(["s2"], g.Snapshot()["floor"].Hidden);
    }

    [Fact]
    public void AnswersAreKeptBetweenRuns()
    {
        using var temp = new TempFolder();
        var path = temp.File("grants.json");
        try
        {
            var g = new PluginGrants(path);
            g.Set("floor", "sessions", "allow", "H1");
            g.SetProjects("floor", ["billing"]);
            g.SetHidden("floor", "s1", true);

            var again = new PluginGrants(path);
            Assert.Equal("allow", again.State("floor", "sessions"));
            Assert.False(again.Sees("floor", "s1", "billing"));
            Assert.False(again.Sees("floor", "s2", "search"));
            Assert.True(again.Sees("floor", "s2", "billing"));
        }
        finally { File.Delete(path); }
    }

    // ---- plugins on the relay: RelayGate decides for them, in place of the rules

    /// <summary>A hub with two sessions (in projects p1 and p2) and the plugin "dash", allowed to message sessions in p1.</summary>
    private static (RelayHub Hub, PluginGrants Grants) HubWithPlugin(bool inbox)
    {
        var grants = new PluginGrants(null);
        grants.Set("dash", "message", "allow", "H1");
        grants.SetProjects("dash", ["p1"]);
        var hub = new RelayHub(new RelayPolicy(null)) { Gate = grants.RelayGate };
        hub.SetPeers([new RelayPeer("s1", "api", "p1", "P1", "Claude", true), new RelayPeer("s2", "web", "p2", "P2", "Codex", true),
            new RelayPeer("plugin:dash", "Dash", null, null, "Plugin", true, inbox)]);
        return (hub, grants);
    }

    [Fact]
    public void APluginMessagesTheSessionsItSeesAndGetsAnswers()
    {
        var (hub, _) = HubWithPlugin(inbox: false);
        var question = hub.Send("plugin:dash", "s1", "status?").Message!;
        Assert.Equal(SendOutcome.Denied, hub.Send("plugin:dash", "s2", "status?").Outcome);   // not in its projects

        // No inbox: a session can't message it first, but can answer it.
        Assert.Equal(SendOutcome.Denied, hub.Send("s1", "plugin:dash", "hello").Outcome);
        Assert.Equal(SendOutcome.Delivered, hub.Send("s1", "plugin:dash", "all green", replyTo: question.Id).Outcome);
        Assert.Equal("all green", hub.Read("plugin:dash").Single().Text);
        Assert.Empty(hub.Waiting());   // a plugin's message never waits to be asked about
    }

    [Fact]
    public void APluginThatTakesMessagesIsListedForTheSessionsItSees()
    {
        var (hub, _) = HubWithPlugin(inbox: true);
        Assert.Equal(SendOutcome.Delivered, hub.Send("s1", "plugin:dash", "done with the parser").Outcome);
        Assert.Contains(hub.Others("s1"), o => o.Peer.Id == "plugin:dash" && o.Decision == RelayDecision.Allow);
        Assert.Contains(hub.Others("s2"), o => o.Peer.Id == "plugin:dash" && o.Decision == RelayDecision.Deny);
    }

    [Fact]
    public void TakingAccessAwayRefusesEvenAReply()
    {
        var (hub, grants) = HubWithPlugin(inbox: false);
        var question = hub.Send("plugin:dash", "s1", "status?").Message!;
        grants.Set("dash", "message", "deny", null);
        Assert.Equal(SendOutcome.Denied, hub.Send("s1", "plugin:dash", "fine", replyTo: question.Id).Outcome);
        Assert.Equal(SendOutcome.Denied, hub.Send("plugin:dash", "s1", "again?").Outcome);
    }

    [Fact]
    public void AHiddenSessionCantBeMessagedByThePlugin()
    {
        var (hub, grants) = HubWithPlugin(inbox: true);
        grants.SetHidden("dash", "s1", true);
        Assert.Equal(SendOutcome.Denied, hub.Send("plugin:dash", "s1", "status?").Outcome);
        Assert.Equal(SendOutcome.Denied, hub.Send("s1", "plugin:dash", "hi").Outcome);
    }

    [Fact]
    public void BetweenSessionsTheGateLeavesItToTheRules()
    {
        var grants = new PluginGrants(null);
        Assert.Null(grants.RelayGate(new RelayPeer("s1", "a", null, null, "Claude", true), new RelayPeer("s2", "b", null, null, "Codex", true), false));
    }
}
