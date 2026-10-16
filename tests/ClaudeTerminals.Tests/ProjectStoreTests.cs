using System.Text.Json;
using ClaudeTerminals.Sessions;

namespace ClaudeTerminals.Tests;

/// <summary>Projects group sessions; their file must never be able to cost the user their sessions.</summary>
public sealed class ProjectStoreTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "ct-proj-" + Guid.NewGuid().ToString("N")[..8]);
    private string File_ => Path.Combine(folder, "projects.json");

    public void Dispose()
    {
        try { Directory.Delete(folder, true); } catch (IOException) { }   // includes DirectoryNotFoundException
    }

    [Fact]
    public void Projects_round_trip_in_order()
    {
        var saved = new[] { new ProjectInfo { Name = "FiscFlow" }, new ProjectInfo { Name = "Website" }, new ProjectInfo { Name = "Inventory" } };
        ProjectStore.Save(File_, saved);

        var loaded = ProjectStore.Load(File_);

        Assert.Equal(saved.Select(p => (p.Id, p.Name)), loaded.Select(p => (p.Id, p.Name)));
    }

    [Fact]
    public void A_missing_file_means_no_projects() => Assert.Empty(ProjectStore.Load(File_));

    [Fact]
    public void A_corrupt_file_means_no_projects_instead_of_an_error()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(File_, "{ not json");

        Assert.Empty(ProjectStore.Load(File_));
    }

    [Fact]
    public void Entries_without_a_name_or_id_and_duplicate_ids_are_dropped()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(File_, """
            [
              { "Id": "a", "Name": "FiscFlow" },
              { "Id": "a", "Name": "Duplicate" },
              { "Id": "", "Name": "No id" },
              { "Id": "b", "Name": "  " },
              { "Id": "c", "Name": "Website" }
            ]
            """);

        Assert.Equal(new[] { "FiscFlow", "Website" }, ProjectStore.Load(File_).Select(p => p.Name));
    }

    [Fact]
    public void Sessions_saved_before_projects_existed_load_with_no_project()
    {
        var old = """[ { "Id": "x", "Name": "Alice", "Directory": "D:\\repo", "Command": "claude" } ]""";

        var sessions = JsonSerializer.Deserialize<List<SessionInfo>>(old)!;

        Assert.Equal("Alice", sessions.Single().Name);
        Assert.Null(sessions.Single().Project);
    }
}
