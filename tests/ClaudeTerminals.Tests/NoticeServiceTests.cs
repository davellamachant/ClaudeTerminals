using ClaudeTerminals.Updates;

namespace ClaudeTerminals.Tests;

/// <summary>Reading notices.json (and checking the copy in the repository, which is published to our site): which tips, offers and news a copy of the app shows, and which it leaves out.</summary>
public sealed class NoticeServiceTests
{
    private static readonly Version V = new(1, 1, 5);
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static List<Notice> Parse(string notices) => NoticeService.Parse($$"""{ "notices": [ {{notices}} ] }""", V, Now);

    [Fact]
    public void A_notice_with_a_link_is_read_in_full()
    {
        var n = Assert.Single(Parse("""
            { "id": "llamalogger-free-year", "kind": "offer", "text": "Track exceptions in one place with LlamaLogger.",
              "link": { "label": "Get a free year", "url": "https://example.com/llamalogger" } }
            """));
        Assert.Equal(new Notice("llamalogger-free-year", "offer", "Track exceptions in one place with LlamaLogger.", "Get a free year", "https://example.com/llamalogger"), n);
    }

    [Fact]
    public void A_notice_is_a_tip_unless_it_says_otherwise_and_a_link_without_a_label_says_learn_more()
    {
        var n = Assert.Single(Parse("""{ "id": "a", "text": "Hi", "link": { "url": "https://example.com/" } }"""));
        Assert.Equal("tip", n.Kind);
        Assert.Equal("Learn more", n.LinkLabel);
    }

    [Fact]
    public void A_notice_can_be_written_as_lines_but_not_too_many()
    {
        Assert.Equal("Track exceptions in one place.\nA free year through Claude Terminals.",
            Assert.Single(Parse("""{ "id": "a", "text": "  Track exceptions in one place.  \r\n  A free year through Claude Terminals. " }""")).Text);
        Assert.Single(Parse("""{ "id": "a", "text": "1\n2\n3\n4" }"""));
        Assert.Empty(Parse("""{ "id": "a", "text": "1\n2\n3\n4\n5" }"""));
    }

    [Fact]
    public void A_notice_can_have_links_in_its_text_and_only_what_they_show_counts_towards_its_length()
    {
        var text = "Check out [www.llamalogger.com](https://www.llamalogger.com/" + new string('x', 400) + ").";
        Assert.Equal(text, Assert.Single(Parse($$"""{ "id": "a", "text": "{{text}}" }""")).Text);
    }

    [Theory]
    [InlineData("""{ "id": "a", "text": "Hi" }""", "accent")]
    [InlineData("""{ "id": "a", "text": "Hi", "color": "Amber" }""", "amber")]
    [InlineData("""{ "id": "a", "text": "Hi", "color": "orange" }""", "orange")]
    [InlineData("""{ "id": "a", "text": "Hi", "color": "#1E90FF" }""", "#1e90ff")]
    public void A_notice_is_in_the_accent_colour_unless_it_names_another_or_its_own(string notice, string color) =>
        Assert.Equal(color, Assert.Single(Parse(notice)).Color);

    [Theory]
    [InlineData("""{ "id": "a", "text": "Hi", "from": "2026-11-16" }""")]                        // not yet
    [InlineData("""{ "id": "a", "text": "Hi", "until": "2026-11-14" }""")]                        // over
    [InlineData("""{ "id": "a", "text": "Hi", "minVersion": "1.1.6" }""")]                        // for a later version
    [InlineData("""{ "id": "a", "text": "Hi", "maxVersion": "1.1.4" }""")]                        // for earlier versions
    public void Notices_for_other_days_or_versions_are_left_out(string notice) => Assert.Empty(Parse(notice));

    [Theory]
    [InlineData("""{ "id": "a", "text": "Hi", "from": "2026-11-15", "until": "2026-11-15" }""")]   // until is the last day shown
    [InlineData("""{ "id": "a", "text": "Hi", "minVersion": "1.1.5", "maxVersion": "1.1.5.0" }""")]
    public void The_first_and_last_days_and_versions_are_included(string notice) => Assert.Single(Parse(notice));

    [Theory]
    [InlineData("""{ "text": "No id" }""")]
    [InlineData("""{ "id": "Has Spaces", "text": "Hi" }""")]
    [InlineData("""{ "id": "a", "text": "  " }""")]
    [InlineData("""{ "id": "a", "kind": "popup", "text": "Hi" }""")]
    [InlineData("""{ "id": "a", "text": "Hi", "color": "teal" }""")]
    [InlineData("""{ "id": "a", "text": "Hi", "color": "red; background: url(x)" }""")]
    [InlineData("""{ "id": "a", "text": "Hi", "link": { "url": "http://example.com/" } }""")]
    [InlineData("""{ "id": "a", "text": "Hi", "link": { "url": "javascript:alert(1)" } }""")]
    [InlineData("""{ "id": "a", "text": "See [this](http://example.com/)" }""")]
    [InlineData("""{ "id": "a", "text": "See [this](javascript:void0)" }""")]
    public void A_malformed_notice_is_left_out(string notice) => Assert.Empty(Parse(notice));

    [Fact]
    public void Only_the_first_notice_with_an_id_counts_and_the_order_is_kept()
    {
        var found = Parse("""
            { "id": "b", "text": "First" }, { "id": "a", "text": "Second" }, { "id": "b", "text": "Again" }
            """);
        Assert.Equal(["First", "Second"], found.Select(n => n.Text));
    }

    [Theory]
    [InlineData("ourpublicstore", "https://ourpublicstore.blob.core.windows.net/downloads/TerminalsData/notices.json")]
    [InlineData(null, null)]                         // a build from source: no notices
    [InlineData("", null)]
    [InlineData("evil.example.com/x?", null)]        // only an account name, never an address
    [InlineData("UPPERCASE", null)]
    public void The_notices_come_from_the_storage_account_built_into_the_app(string? account, string? url) =>
        Assert.Equal(url, NoticeService.FeedFor(account));

    [Fact]
    public void A_file_without_notices_has_none() =>
        Assert.Empty(NoticeService.Parse("{}", V, Now));

    [Fact]
    public void The_published_file_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "notices.json");
        var json = File.ReadAllText(path);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var all = doc.RootElement.GetProperty("notices").GetArrayLength();
        // Every notice in it is well formed, whatever its dates and versions.
        Assert.Equal(all, NoticeService.Parse(json.Replace("\"from\"", "\"_from\"").Replace("\"until\"", "\"_until\"")
            .Replace("\"minVersion\"", "\"_min\"").Replace("\"maxVersion\"", "\"_max\""), V, Now).Count);
    }
}
