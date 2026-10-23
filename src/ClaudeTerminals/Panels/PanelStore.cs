using System.Text.Json;

namespace ClaudeTerminals.Panels;

/// <summary>What changed: the panel (owner and id), and whether it was just created. Null fields: several at once.</summary>
internal sealed record PanelChange(string? Owner = null, string? Id = null, bool Created = false);

/// <summary>
/// Every panel, kept in panels.json so they survive a restart. Called from the MCP server's threads and the UI thread,
/// so everything goes through one lock; <see cref="Changed"/> is raised after it is released.
/// </summary>
internal sealed class PanelStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object gate = new();
    private readonly List<Panel> panels;
    private readonly string path;

    public PanelStore(string path)
    {
        this.path = path;
        panels = Load(path);
    }

    /// <summary>Raised on whichever thread made the change.</summary>
    public event Action<PanelChange>? Changed;

    public List<Panel> Snapshot()
    {
        lock (gate) return [.. panels];
    }

    public List<Panel> ForOwner(string owner)
    {
        lock (gate) return panels.Where(p => p.Owner == owner).ToList();
    }

    /// <summary>Shows a panel, replacing the session's panel with the same id (whose place, and scope, it keeps).</summary>
    public (Panel Panel, bool Created) Show(string owner, JsonElement args)
    {
        var id = PanelSpec.ReadId(args);
        var panel = new Panel { Owner = owner, Id = id };
        PanelSpec.Apply(panel, args, creating: true);
        bool created;
        lock (gate)
        {
            var i = panels.FindIndex(p => p.Owner == owner && p.Id == id);
            created = i < 0;
            if (created && panels.Count(p => p.Owner == owner) >= PanelSpec.MaxPanelsPerSession)
                throw new PanelException($"This session already has {PanelSpec.MaxPanelsPerSession} panels; close one first.");
            panel.Updated = DateTime.UtcNow;
            if (created) panels.Add(panel);
            else
            {
                // The person may have chosen where it shows, and ticked items; that stays.
                panel.Scope = panels[i].Scope;
                panel.Version = panels[i].Version + 1;
                PanelSpec.KeepTicks(panels[i], panel);
                panels[i] = panel;
            }
            Save();
        }
        Changed?.Invoke(new PanelChange(owner, id, created));
        return (panel, created);
    }

    /// <summary>Changes only the fields named in <paramref name="args"/> of one of the session's panels.</summary>
    public Panel Update(string owner, JsonElement args)
    {
        var id = PanelSpec.ReadId(args);
        Panel panel;
        lock (gate)
        {
            panel = panels.FirstOrDefault(p => p.Owner == owner && p.Id == id)
                ?? throw new PanelException($"This session has no panel \"{id}\" (it may have been closed). Use show_panel to show it again.");
            // Applied to a copy, so a mistake halfway through leaves the panel as it was.
            var copy = Copy(panel);
            PanelSpec.Apply(copy, args, creating: false);
            PanelSpec.KeepTicks(panel, copy);
            PanelSpec.ApplyChecks(copy, args);
            copy.Updated = DateTime.UtcNow;
            copy.Version = panel.Version + 1;
            panels[panels.IndexOf(panel)] = panel = copy;
            Save();
        }
        Changed?.Invoke(new PanelChange(owner, id));
        return panel;
    }

    public bool Close(string owner, string id)
    {
        lock (gate)
        {
            if (panels.RemoveAll(p => p.Owner == owner && p.Id == id) == 0) return false;
            Save();
        }
        Changed?.Invoke(new PanelChange(owner, id));
        return true;
    }

    /// <summary>A copy of one of the session's panels, as it is now (ticks included).</summary>
    public Panel? Get(string owner, string id)
    {
        lock (gate)
        {
            var panel = panels.FirstOrDefault(p => p.Owner == owner && p.Id == id);
            return panel == null ? null : Copy(panel);
        }
    }

    /// <summary>
    /// The person ticked or unticked an item. <paramref name="path"/> is the group indexes then the item's ("0/2/5", or
    /// "5" without groups), as of <paramref name="version"/>: if the panel changed since, nothing happens and the page
    /// shows the panel as it now is.
    /// </summary>
    public bool SetChecked(string owner, string id, string path, bool value, int version)
    {
        lock (gate)
        {
            var panel = panels.FirstOrDefault(p => p.Owner == owner && p.Id == id);
            if (panel == null || panel.Version != version) return false;
            var steps = path.Split('/');
            if (steps.Any(s => !int.TryParse(s, out _))) return false;
            var indexes = steps.Select(int.Parse).ToArray();
            List<PanelItem>? items = panel.Items;
            List<PanelGroup>? groups = panel.Groups;
            foreach (var g in indexes[..^1])
            {
                if (groups == null || g < 0 || g >= groups.Count) return false;
                (items, groups) = (groups[g].Items, groups[g].Groups);
            }
            var last = indexes[^1];
            if (items == null || last < 0 || last >= items.Count || items[last].Checked == null) return false;
            items[last].Checked = value;
            panel.Version++;
            Save();
        }
        Changed?.Invoke(new PanelChange(owner, id));
        return true;
    }

    /// <summary>The person's choice of where a panel shows.</summary>
    public void SetScope(string owner, string id, string scope)
    {
        if (!PanelSpec.Scopes.Contains(scope)) return;
        lock (gate)
        {
            var panel = panels.FirstOrDefault(p => p.Owner == owner && p.Id == id);
            if (panel == null || panel.Scope == scope) return;
            panel.Scope = scope;
            Save();
        }
        Changed?.Invoke(new PanelChange(owner, id));
    }

    /// <summary>Drops the panels of sessions that no longer exist (a removed session takes its panels with it).</summary>
    public void KeepOwners(IReadOnlyCollection<string> owners)
    {
        lock (gate)
        {
            if (panels.RemoveAll(p => !owners.Contains(p.Owner)) == 0) return;
            Save();
        }
        Changed?.Invoke(new PanelChange());
    }

    private static Panel Copy(Panel p) => JsonSerializer.Deserialize<Panel>(JsonSerializer.Serialize(p, Options), Options)!;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(panels, Options));
            File.Move(temp, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The panels still show; they just won't be back after a restart.
        }
    }

    private static List<Panel> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return (JsonSerializer.Deserialize<List<Panel>>(File.ReadAllText(path), Options) ?? [])
                    .Where(p => !string.IsNullOrEmpty(p.Owner) && !string.IsNullOrEmpty(p.Id) && (p.Markdown != null || p.Items != null || p.Groups != null))
                    .ToList();
        }
        catch (Exception)
        {
            // A corrupt file loses the panels, nothing else.
        }
        return [];
    }
}
