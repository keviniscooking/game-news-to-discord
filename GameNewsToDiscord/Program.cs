using System.Net.Http.Json;
using System.ServiceModel.Syndication;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

// =====================================================================
//  Game News -> Discord
//  Fetches RSS/Atom feeds for each game, filters + de-duplicates,
//  and posts new items to a per-game Discord webhook.
//  Runs on a schedule via GitHub Actions. State is committed back to
//  the repo so it remembers what it has already posted.
// =====================================================================

// ---- paths & environment ----
string feedsPath = Environment.GetEnvironmentVariable("FEEDS_CONFIG") ?? "GameNewsToDiscord/feeds.json";
string statePath = Environment.GetEnvironmentVariable("STATE_FILE") ?? "state/seen.json";
string heartbeatPath = "state/heartbeat.txt";
string? webhooksJson = Environment.GetEnvironmentVariable("DISCORD_WEBHOOKS");

// SEED_ONLY=1 marks everything currently in the feeds as "seen" WITHOUT posting.
// Used on first setup (and after big config changes) so you don't get a flood
// of old back-catalogue items. New games are also auto-seeded the first time
// they're seen, so you normally never need to set this by hand.
bool seedOnly = (Environment.GetEnvironmentVariable("SEED_ONLY") ?? "0")
    is "1" or "true" or "True" or "yes";

var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// ---- load config ----
if (!File.Exists(feedsPath))
{
    Console.WriteLine($"ERROR: config not found at {feedsPath}");
    return 1;
}
AppConfig config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(feedsPath), jsonOpts)
                   ?? throw new Exception("Could not parse feeds.json");

// ---- load webhooks (game key -> webhook url) ----
Dictionary<string, string> webhooks = new();
if (!string.IsNullOrWhiteSpace(webhooksJson))
{
    try { webhooks = JsonSerializer.Deserialize<Dictionary<string, string>>(webhooksJson) ?? new(); }
    catch (Exception ex) { Console.WriteLine($"WARN: could not parse DISCORD_WEBHOOKS: {ex.Message}"); }
}
else
{
    Console.WriteLine("WARN: DISCORD_WEBHOOKS not set. Nothing will be posted (seeding still works).");
}

// ---- load state (game key -> list of seen keys, newest last) ----
Dictionary<string, List<string>> state = new();
if (File.Exists(statePath))
{
    state = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(statePath)) ?? new();
}

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
// A descriptive User-Agent is polite and some feed hosts require one.
// Replace the contact address with your own.
http.DefaultRequestHeaders.UserAgent.ParseAdd("GameNewsToDiscord/1.0 (+github-actions; contact: you@example.com)");

var curator = new KeywordCurator();
int totalPosted = 0;

foreach (GameConfig game in config.Games)
{
    // Build fast-lookup structures from stored state for this game.
    List<string> seenList = state.TryGetValue(game.Key, out var stored) ? stored : new List<string>();
    HashSet<string> seenSet = new(seenList);
    bool isNewGame = !state.ContainsKey(game.Key);

    // 1) Fetch every feed for this game.
    List<NewsItem> items = new();
    foreach (FeedConfig feed in game.Feeds)
    {
        try
        {
            items.AddRange(await FetchFeedAsync(http, feed));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN [{game.Name}] feed '{feed.Source}' failed: {ex.Message}");
        }
    }

    // Oldest first so Discord shows them in natural chronological order.
    items = items.OrderBy(i => i.Published).ToList();

    // 2) Seed mode: remember everything, post nothing.
    if (seedOnly || isNewGame)
    {
        foreach (NewsItem it in items)
        {
            AddSeen(seenList, seenSet, it.Id);
            AddSeen(seenList, seenSet, it.TitleKey);
        }
        state[game.Key] = Trim(seenList);
        Console.WriteLine($"[{game.Name}] seeded {items.Count} existing item(s) (no posts).");
        continue;
    }

    // 3) Normal mode: post genuinely new items.
    webhooks.TryGetValue(game.Key, out string? webhook);
    HashSet<string> handledThisRun = new();   // guards cross-source duplicates in one run

    foreach (NewsItem it in items)
    {
        // Already posted in a previous run?
        if (seenSet.Contains(it.Id) || seenSet.Contains(it.TitleKey)) continue;
        // Same story from a second feed (e.g. Official + Steam) in THIS run?
        if (handledThisRun.Contains(it.TitleKey)) continue;

        // Filter (keyword rules; Claude hook lives inside the curator for later).
        if (!curator.ShouldPost(it, game))
        {
            AddSeen(seenList, seenSet, it.Id);
            AddSeen(seenList, seenSet, it.TitleKey);
            handledThisRun.Add(it.TitleKey);
            continue;
        }

        if (string.IsNullOrWhiteSpace(webhook))
        {
            // No webhook yet for this game: skip WITHOUT marking seen, so it posts
            // once you add the webhook.
            Console.WriteLine($"[{game.Name}] no webhook configured; not posting: {it.Title}");
            continue;
        }

        bool ok = await PostToDiscordAsync(http, webhook, game, it);
        if (ok)
        {
            AddSeen(seenList, seenSet, it.Id);
            AddSeen(seenList, seenSet, it.TitleKey);
            handledThisRun.Add(it.TitleKey);
            totalPosted++;
            Console.WriteLine($"[{game.Name}] posted: {it.Title}");
            await Task.Delay(600);   // be gentle with Discord
        }
        else
        {
            // Leave it unseen so we retry next run.
            Console.WriteLine($"[{game.Name}] FAILED to post (will retry next run): {it.Title}");
        }
    }

    state[game.Key] = Trim(seenList);
}

// ---- persist state ----
Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
File.WriteAllText(statePath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));

// Heartbeat (date only) so there is at least one commit per day. This keeps the
// scheduled workflow from being auto-disabled after 60 days of no repo activity.
File.WriteAllText(heartbeatPath, DateTime.UtcNow.ToString("yyyy-MM-dd") + "\n");

Console.WriteLine($"Done. Posted {totalPosted} new item(s).");
return 0;


// =====================================================================
//  Helpers
// =====================================================================

static async Task<List<NewsItem>> FetchFeedAsync(HttpClient http, FeedConfig feed)
{
    string xml = await http.GetStringAsync(feed.Url);
    using var sr = new StringReader(xml);
    using var xr = XmlReader.Create(sr, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
    SyndicationFeed sfeed = SyndicationFeed.Load(xr);   // handles both RSS 2.0 and Atom

    List<NewsItem> list = new();
    foreach (SyndicationItem it in sfeed.Items)
    {
        string title = it.Title?.Text?.Trim() ?? "(untitled)";
        string link = it.Links?.FirstOrDefault(l => l.Uri != null)?.Uri?.ToString() ?? "";
        string id = !string.IsNullOrWhiteSpace(it.Id) ? it.Id!
                    : (!string.IsNullOrEmpty(link) ? link : title);
        string summary = CleanText(it.Summary?.Text ?? "");
        DateTimeOffset published =
            it.PublishDate != default ? it.PublishDate :
            it.LastUpdatedTime != default ? it.LastUpdatedTime :
            DateTimeOffset.UtcNow;

        list.Add(new NewsItem(id, NormalizeTitle(title), title, link, summary, published, feed.Source));
    }
    return list;
}

static async Task<bool> PostToDiscordAsync(HttpClient http, string webhook, GameConfig game, NewsItem it)
{
    var embed = new Dictionary<string, object?>
    {
        ["title"] = Truncate(it.Title, 256),
        ["color"] = ColorFor(it.Source),
        ["timestamp"] = it.Published.ToString("o"),
        ["footer"] = new Dictionary<string, object?> { ["text"] = $"{game.Name} \u00b7 {it.Source}" },
    };
    if (!string.IsNullOrWhiteSpace(it.Url)) embed["url"] = it.Url;
    if (!string.IsNullOrWhiteSpace(it.Summary)) embed["description"] = Truncate(it.Summary, 400);

    var payload = new Dictionary<string, object?> { ["embeds"] = new[] { embed } };

    for (int attempt = 0; attempt < 2; attempt++)
    {
        HttpResponseMessage resp = await http.PostAsJsonAsync(webhook, payload);
        if (resp.IsSuccessStatusCode) return true;

        if ((int)resp.StatusCode == 429)   // rate limited: honour retry_after and try once more
        {
            double wait = 2;
            try
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("retry_after", out var ra)) wait = ra.GetDouble();
            }
            catch { /* ignore parse issues, use default */ }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(wait + 0.5, 30)));
            continue;
        }

        string body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"  Discord {(int)resp.StatusCode}: {Truncate(body, 200)}");
        return false;
    }
    return false;
}

// Strip HTML (Steam summaries are full of it), decode entities, collapse whitespace.
static string CleanText(string raw)
{
    if (string.IsNullOrEmpty(raw)) return "";
    string noTags = Regex.Replace(raw, "<.*?>", " ");
    string decoded = System.Net.WebUtility.HtmlDecode(noTags);
    return Regex.Replace(decoded, @"\s+", " ").Trim();
}

// Lowercased, punctuation-free title used to catch the same story across feeds.
static string NormalizeTitle(string title)
{
    string lower = title.ToLowerInvariant();
    string alnum = Regex.Replace(lower, "[^a-z0-9]+", " ");
    return "t:" + Regex.Replace(alnum, @"\s+", " ").Trim();
}

static void AddSeen(List<string> list, HashSet<string> set, string key)
{
    if (set.Add(key)) list.Add(key);
}

static List<string> Trim(List<string> list)
{
    const int max = 800;   // cap the state list so it can't grow forever
    if (list.Count <= max) return list;
    return list.Skip(list.Count - max).ToList();
}

static string Truncate(string s, int max)
    => s.Length <= max ? s : s[..(max - 1)] + "\u2026";

static int ColorFor(string source) => source.ToLowerInvariant() switch
{
    "steam" => 0x66C0F4,
    "official" => 0x2ECC71,
    "forum" => 0xE67E22,
    _ => 0x95A5A6,
};


// =====================================================================
//  Filtering  (free keyword rules now; Claude curation later)
// =====================================================================

interface ICurator
{
    bool ShouldPost(NewsItem item, GameConfig game);
}

sealed class KeywordCurator : ICurator
{
    public bool ShouldPost(NewsItem item, GameConfig game)
    {
        FilterConfig? f = game.Filter;
        if (f is null) return true;   // no rules configured => post everything

        string hay = (item.Title + " " + item.Summary).ToLowerInvariant();

        // Drop anything matching an "exclude" keyword (e.g. "sale", "discount").
        if (f.Exclude is { Count: > 0 } &&
            f.Exclude.Any(k => hay.Contains(k.ToLowerInvariant())))
            return false;

        // If "include" is set, require at least one match.
        if (f.Include is { Count: > 0 } &&
            !f.Include.Any(k => hay.Contains(k.ToLowerInvariant())))
            return false;

        return true;

        // ---------------------------------------------------------------
        // TODO (optional Claude curation layer):
        // If you later want smarter filtering/summarising, call the
        // Anthropic API here with the item title+summary and a prompt like
        // "Is this an important update (patch/release/major news)? Reply YES
        // or SKIP." Read the key from ANTHROPIC_API_KEY. Note: the API is
        // paid (tiny at this volume, but not free), so it's off by default.
        // ---------------------------------------------------------------
    }
}


// =====================================================================
//  Config + item models
// =====================================================================

record AppConfig(int PollMinutes, List<GameConfig> Games);

record GameConfig(string Key, string Name, List<FeedConfig> Feeds, FilterConfig? Filter = null);

record FeedConfig(string Source, string Url);

record FilterConfig(List<string>? Include = null, List<string>? Exclude = null);

record NewsItem(
    string Id,
    string TitleKey,
    string Title,
    string Url,
    string Summary,
    DateTimeOffset Published,
    string Source);
