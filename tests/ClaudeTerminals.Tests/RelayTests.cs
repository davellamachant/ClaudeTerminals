using System.Text.Json;
using ClaudeTerminals.Panels;
using ClaudeTerminals.Relay;

namespace ClaudeTerminals.Tests;

public class RelayTests
{
    private static RelayPeer Peer(string id, string? project = null) => new(id, id.ToUpperInvariant(), project, project, "Claude", true);

    private static RelayHub Hub(out RelayPolicy policy, params RelayPeer[] peers)
    {
        policy = new RelayPolicy(null);
        var hub = new RelayHub(policy);
        hub.SetPeers(peers.Length > 0 ? peers : [Peer("a", "p1"), Peer("b", "p1"), Peer("c", "p2")]);
        return hub;
    }

    [Fact]
    public void WithNoRuleTheMessageWaitsAndYouAreAskedOnce()
    {
        var hub = Hub(out _);
        var asked = new List<RelayAsk>();
        hub.Asked += asked.Add;

        Assert.Equal(SendOutcome.Waiting, hub.Send("a", "b", "one").Outcome);
        Assert.Equal(SendOutcome.Waiting, hub.Send("a", "b", "two").Outcome);

        Assert.Single(asked);
        Assert.Equal(2, hub.Waiting().Single().Count);
        Assert.Empty(hub.Read("b"));
    }

    [Fact]
    public void AllowingDeliversWhatWaitsAndLaterMessagesGoStraightThrough()
    {
        var hub = Hub(out var policy);
        hub.Send("a", "b", "one");

        policy.Set("a", "b", RelayDecision.Allow);
        Assert.Empty(hub.Waiting());
        Assert.Equal(SendOutcome.Delivered, hub.Send("a", "b", "two").Outcome);

        Assert.Equal(["one", "two"], hub.Read("b").Select(m => m.Text));
        Assert.Empty(hub.Read("b"));   // read once
    }

    [Fact]
    public void RefusingDropsWhatWaitsAndRefusesLaterMessages()
    {
        var hub = Hub(out var policy);
        hub.Send("a", "b", "one");

        policy.Set("a", "b", RelayDecision.Deny);
        Assert.Empty(hub.Waiting());
        Assert.Equal(SendOutcome.Denied, hub.Send("a", "b", "two").Outcome);
        Assert.Empty(hub.Read("b"));
    }

    [Fact]
    public void RulesGoOneWay()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);

        Assert.Equal(SendOutcome.Waiting, hub.Send("b", "a", "hello").Outcome);
    }

    [Fact]
    public void TheMostSpecificRuleWins()
    {
        var policy = new RelayPolicy(null);
        policy.Set("a", RelayPolicy.Anyone, RelayDecision.Deny);
        policy.Set("a", RelayPolicy.SameProject, RelayDecision.Allow);
        policy.Set("a", "d", RelayDecision.Deny);

        Assert.Equal(RelayDecision.Allow, policy.Decide("a", "b", sameProject: true).Decision);
        Assert.Equal(RelayDecision.Deny, policy.Decide("a", "d", sameProject: true).Decision);
        Assert.Equal(RelayDecision.Deny, policy.Decide("a", "c", sameProject: false).Decision);
        Assert.Equal(RelayDecision.Ask, policy.Decide("b", "a", sameProject: true).Decision);
    }

    [Fact]
    public void TheGlobalRulesDecideWhatASessionsOwnLeaveToDefault()
    {
        var policy = new RelayPolicy(null);
        policy.Set(RelayPolicy.Anyone, RelayPolicy.Anyone, RelayDecision.Deny);
        policy.Set(RelayPolicy.Anyone, RelayPolicy.SameProject, RelayDecision.Allow);

        Assert.Equal(RelayDecision.Allow, policy.Decide("a", "b", sameProject: true).Decision);
        Assert.Equal(RelayDecision.Deny, policy.Decide("a", "c", sameProject: false).Decision);

        // A session's own rules come first, even its "anyone" over the global "same project".
        policy.Set("a", RelayPolicy.Anyone, RelayDecision.Deny);
        Assert.Equal(RelayDecision.Deny, policy.Decide("a", "b", sameProject: true).Decision);
        policy.Set("a", "c", RelayDecision.Allow);
        Assert.Equal(RelayDecision.Allow, policy.Decide("a", "c", sameProject: false).Decision);
        Assert.Equal(RelayDecision.Allow, policy.Decide("b", "a", sameProject: true).Decision);   // b has none of its own
    }

    [Fact]
    public void GlobalRulesStayWhenSessionsGoAndOnlySensibleRulesAreTaken()
    {
        var policy = new RelayPolicy(null);
        policy.Set(RelayPolicy.Anyone, RelayPolicy.Anyone, RelayDecision.Allow);
        policy.KeepSessions([]);
        Assert.Single(policy.Rules());

        Assert.Throws<ArgumentException>(() => policy.Set(RelayPolicy.Anyone, "b", RelayDecision.Allow));
        Assert.Throws<ArgumentException>(() => policy.Set("a", "a", RelayDecision.Allow));
        Assert.Throws<ArgumentException>(() => policy.Set(RelayPolicy.SameProject, "b", RelayDecision.Allow));
    }

    [Fact]
    public void AReplyToAMessageGetsThroughWithoutARule()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        var question = hub.Send("a", "b", "what changed?").Message!;

        Assert.Equal(SendOutcome.Delivered, hub.Send("b", "a", "the parser", replyTo: question.Id).Outcome);
        // Naming a message it wasn't sent doesn't count.
        Assert.Equal(SendOutcome.Waiting, hub.Send("c", "a", "me too", replyTo: question.Id).Outcome);
    }

    [Fact]
    public void ARefusedSessionCanStillReplyButNotStartAConversation()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        policy.Set("b", "a", RelayDecision.Deny);
        var question = hub.Send("a", "b", "status?").Message!;

        Assert.Equal(SendOutcome.Denied, hub.Send("b", "a", "unasked").Outcome);
        Assert.Equal(SendOutcome.Delivered, hub.Send("b", "a", "fine", replyTo: question.Id).Outcome);
    }

    [Fact]
    public void AnExchangeStopsAfterMaxTurns()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        var last = hub.Send("a", "b", "turn 1").Message!;
        for (var turn = 2; turn <= RelayHub.MaxTurns; turn++)
        {
            var (from, to) = turn % 2 == 0 ? ("b", "a") : ("a", "b");
            last = hub.Send(from, to, "turn " + turn, replyTo: last.Id).Message!;
            Assert.Equal(turn, last.Turn);
        }

        var next = last.To;
        Assert.Contains("reached", Assert.Throws<RelayException>(() => hub.Send(next, last.From, "one more", replyTo: last.Id)).Message);
        // A new message starts afresh, under the rules (a may message b).
        Assert.Equal(1, hub.Send("a", "b", "new topic").Message!.Turn);
    }

    [Fact]
    public async Task WaitForReplyReturnsTheReplyAsSoonAsItComes()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        var question = hub.Send("a", "b", "what changed?").Message!;
        hub.Send("c", "a", "unrelated");   // waits for an answer: not a reply

        var waiting = hub.WaitForReplyAsync("a", question.Id, TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        hub.Send("b", "a", "the parser", replyTo: question.Id);

        var reply = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("the parser", reply!.Text);
        Assert.Equal(0, hub.Unread("a"));   // taken, not left to read again
    }

    [Fact]
    public async Task WaitForReplyGivesUpAtItsTimeAndLeavesOtherMessagesUnread()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        policy.Set("b", "a", RelayDecision.Allow);
        var question = hub.Send("a", "b", "status?").Message!;
        hub.Send("b", "a", "something else");

        Assert.Null(await hub.WaitForReplyAsync("a", question.Id, TimeSpan.FromMilliseconds(100)));
        Assert.Equal(1, hub.Unread("a"));
        Assert.Throws<RelayException>(() => hub.WaitForReplyAsync("a", "not-mine", TimeSpan.Zero).GetAwaiter().GetResult());
    }

    [Fact]
    public void TheLogFollowsEachMessage()
    {
        var hub = Hub(out var policy);
        var waits = hub.Send("a", "b", "first").Message!;
        policy.Set("a", "c", RelayDecision.Deny);
        hub.Send("a", "c", "refused");

        Assert.Equal(RelayStatus.Waiting, hub.Log().Single(e => e.Message.Id == waits.Id).Status);
        hub.AnswerOnce("a", "b", deliver: true);   // Allow once: delivered, with no rule made
        Assert.Equal(RelayStatus.Delivered, hub.Log().Single(e => e.Message.Id == waits.Id).Status);
        Assert.DoesNotContain(policy.Rules(), r => r.To == "b");
        hub.Read("b");
        Assert.Equal(RelayStatus.Read, hub.Log().Single(e => e.Message.Id == waits.Id).Status);
        Assert.Equal(RelayStatus.Refused, hub.Log().Single(e => e.Message.Text == "refused").Status);

        hub.Send("a", "b", "again");   // still no rule: asks again
        hub.AnswerOnce("a", "b", deliver: false);
        Assert.Equal(RelayStatus.Dropped, hub.Log().First().Status);
        Assert.Equal("again", hub.Log().First().Message.Text);   // newest first
    }

    [Fact]
    public void AnswersCanSetTheGlobalRules()
    {
        var policy = new RelayPolicy(null);
        policy.Set(RelayPolicy.Anyone, RelayPolicy.SameProject, RelayDecision.Allow);
        Assert.Equal(RelayDecision.Allow, policy.Decide("x", "y", sameProject: true).Decision);
    }

    [Fact]
    public void AnOldMessageCanNoLongerBeRepliedTo()
    {
        var clock = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        var policy = new RelayPolicy(null);
        var hub = new RelayHub(policy, () => clock);
        hub.SetPeers([Peer("a"), Peer("b")]);
        policy.Set("a", "b", RelayDecision.Allow);
        var question = hub.Send("a", "b", "status?").Message!;

        clock += RelayHub.ReplyWindow + TimeSpan.FromMinutes(1);
        Assert.Equal(SendOutcome.Waiting, hub.Send("b", "a", "late", replyTo: question.Id).Outcome);
    }

    [Fact]
    public void PausedRefusesEverythingAndKeepsWhatWaits()
    {
        var hub = Hub(out var policy);
        hub.Send("a", "b", "one");
        policy.Set("a", "c", RelayDecision.Allow);
        policy.Paused = true;

        Assert.Throws<RelayException>(() => hub.Send("a", "c", "two"));
        Assert.Single(hub.Waiting());
        Assert.All(hub.Others("a"), o => Assert.Equal(RelayDecision.Deny, o.Decision));
    }

    [Fact]
    public void SessionsAreFoundByIdOrByAnUnambiguousName()
    {
        var hub = Hub(out _, new RelayPeer("1", "api", null, null, "Claude", true), new RelayPeer("2", "web", null, null, "Codex", true),
            new RelayPeer("3", "web", null, null, "Codex", false));

        Assert.Equal("1", hub.Find("API").Id);
        Assert.Equal("3", hub.Find("3").Id);
        Assert.Contains("2 sessions", Assert.Throws<RelayException>(() => hub.Find("web")).Message);
        Assert.Throws<RelayException>(() => hub.Find("nope"));
        Assert.Throws<RelayException>(() => hub.Send("1", "1", "me"));
    }

    [Fact]
    public void TooManyMessagesInAMinuteAreRefused()
    {
        var hub = Hub(out var policy);
        policy.Set("a", "b", RelayDecision.Allow);
        for (var i = 0; i < RelayHub.PerMinute; i++) hub.Send("a", "b", "m" + i);

        Assert.Throws<RelayException>(() => hub.Send("a", "b", "one more"));
    }

    [Fact]
    public void ASessionGoneTakesItsWaitingMessagesAndRulesWithIt()
    {
        var hub = Hub(out var policy);
        hub.Send("a", "b", "one");
        policy.Set("a", "b", RelayDecision.Deny);
        policy.Set("c", "b", RelayDecision.Allow);
        policy.Set("c", RelayPolicy.Anyone, RelayDecision.Allow);

        hub.SetPeers([Peer("a", "p1"), Peer("c", "p2")]);
        policy.KeepSessions(["a", "c"]);

        Assert.Empty(hub.Waiting());
        Assert.Equal([new RelayRule("c", RelayPolicy.Anyone, RelayDecision.Allow)], policy.Rules());
    }

    [Fact]
    public void RulesAreKeptBetweenRuns()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}.json");
        try
        {
            var policy = new RelayPolicy(path);
            policy.Set("a", "b", RelayDecision.Allow);
            policy.Set("a", RelayPolicy.Anyone, RelayDecision.Deny);
            policy.Paused = true;

            var again = new RelayPolicy(path);
            Assert.True(again.Paused);
            Assert.Equal(RelayDecision.Allow, again.Decide("a", "b", false).Decision);
            Assert.Equal(RelayDecision.Deny, again.Decide("a", "c", false).Decision);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ADamagedFileMeansAskingAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            Assert.Empty(new RelayPolicy(path).Rules());
        }
        finally { File.Delete(path); }
    }

    // ---- the tools, as an agent calls them

    private static string Call(PanelMcp mcp, string session, string tool, object? args = null)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } });
        using var doc = JsonDocument.Parse(json);
        var reply = mcp.Handle(session, doc.RootElement)!;
        return reply["result"]!["content"]![0]!["text"]!.GetValue<string>();
    }

    [Fact]
    public void AgentsSeeTheToolsAndMessageThroughThem()
    {
        var hub = Hub(out var policy);
        var mcp = new PanelMcp(new PanelStore(Path.Combine(Path.GetTempPath(), $"panels-{Guid.NewGuid():N}.json")), hub);
        using (var list = JsonDocument.Parse("""{ "jsonrpc": "2.0", "id": 1, "method": "tools/list" }"""))
        {
            var names = mcp.Handle("a", list.RootElement)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).ToList();
            Assert.Contains("send_message", names);
            Assert.Contains("show_panel", names);
        }

        policy.Set("a", "c", RelayDecision.Deny);
        var sessions = Call(mcp, "a", "list_sessions");
        Assert.Contains("\"B\"", sessions);
        Assert.DoesNotContain("\"C\"", sessions);   // refused: not listed

        Assert.StartsWith("Waiting", Call(mcp, "a", "send_message", new { to = "B", text = "hi" }));
        policy.Set("a", "b", RelayDecision.Allow);
        var read = Call(mcp, "b", "read_messages");
        Assert.Contains("not from the person", read);
        Assert.Contains("hi", read);
        Assert.Equal("No new messages.", Call(mcp, "b", "read_messages"));
        Assert.StartsWith("Not sent", Call(mcp, "a", "send_message", new { to = "c", text = "hi" }));
    }

    [Fact]
    public void WithoutARelayThereAreNoMessageTools()
    {
        var mcp = new PanelMcp(new PanelStore(Path.Combine(Path.GetTempPath(), $"panels-{Guid.NewGuid():N}.json")));
        using var list = JsonDocument.Parse("""{ "jsonrpc": "2.0", "id": 1, "method": "tools/list" }""");
        var names = mcp.Handle("a", list.RootElement)!["result"]!["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>());
        Assert.DoesNotContain("send_message", names);
    }
}
