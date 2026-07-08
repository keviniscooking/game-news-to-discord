# Game News to Discord

A tiny C# console app that watches each game's news feed and posts new items to a
Discord channel. It runs for free on GitHub Actions on a schedule — nothing runs on
your own machine.

## How it works

- Reads `GameNewsToDiscord/feeds.json` — one entry per game, each with one or more
  source-tagged feeds.
- Fetches each feed, **de-duplicates** (so the same story from two feeds — e.g. an
  official site *and* Steam — posts once), applies an optional keyword filter, and
  posts new items to that game's Discord webhook as an embed.
- Remembers what it has posted in `state/seen.json`, which the workflow commits back
  to the repo after each run.
- **Seed-then-post:** the first time it sees a game (or feed set) it marks everything
  currently in the feed as "already seen" and posts nothing, so you never get a flood
  of old back-catalogue items. From then on it only posts genuinely new ones.

Two games use two feeds each (official-first, Steam as backup):

| Game | Sources |
|------|---------|
| World of Warships | Official RSS (`worldofwarships.com/en/rss/news/`) + Steam |
| War Thunder | Official forum RSS + Steam |

All others post directly from their Steam news feed, which for those studios *is* the
developers' own announcement channel.

## Setup

### 0. Prerequisite
Have the code build locally first: from the repo root run `dotnet build`. (Targets
`net10.0`; change to `net8.0` in the `.csproj` if you ever want an LTS target.)

### 1. Create the repo
Push this folder to a new GitHub repo. A **private** repo is fine — GitHub Actions
minutes are free and a scheduled job like this uses a trivial amount.

### 2. Create one Discord webhook per game channel
In Discord, for each game channel:
**Edit Channel → Integrations → Webhooks → New Webhook → Copy Webhook URL.**
Do this for each of the nine channels you want fed.

### 3. Add the webhooks as a repo secret
In GitHub: **Settings → Secrets and variables → Actions → New repository secret.**

- Name: `DISCORD_WEBHOOKS`
- Value: a JSON object mapping each game **key** (from `feeds.json`) to its webhook URL:

```json
{
  "ardem": "https://discord.com/api/webhooks/…",
  "readyornot": "https://discord.com/api/webhooks/…",
  "satisfactory": "https://discord.com/api/webhooks/…",
  "starrupture": "https://discord.com/api/webhooks/…",
  "warthunder": "https://discord.com/api/webhooks/…",
  "worldofwarships": "https://discord.com/api/webhooks/…",
  "f125": "https://discord.com/api/webhooks/…",
  "hoi4": "https://discord.com/api/webhooks/…",
  "subnautica2": "https://discord.com/api/webhooks/…"
}
```

The webhook URLs live **only** in this secret — never commit them to the repo. You can
start with just a few games; any game without a webhook is skipped (and will post once
you add its webhook later).

### 4. First run
Go to the **Actions** tab → *Game News to Discord* → **Run workflow**.
The first run auto-seeds (marks current items as seen, posts nothing). After that it
runs every 30 minutes on its own and posts only new items.

## Everyday operations

**Add a game.** Add a block to `feeds.json` (with a unique `key`) and add that key →
webhook to the `DISCORD_WEBHOOKS` secret. Its first run auto-seeds silently.

**Add or change a feed on an existing game.** Because the game is already known, its new
feed won't auto-seed and could backfill old items. To reseed just that game, delete its
key from `state/seen.json` and run the workflow once (it reseeds silently). To reseed
everything, run the workflow manually with **seed_only** checked.

**Filter out noise (optional).** Add a `filter` to any game in `feeds.json`:

```json
{
  "key": "worldofwarships",
  "name": "World of Warships",
  "feeds": [ … ],
  "filter": { "exclude": ["sale", "discount", "bundle"] }
}
```

- `exclude`: drop items whose title/summary contains any of these words.
- `include`: if present, only post items containing at least one of these words.

With no `filter`, everything is posted.

**Smarter curation later (optional, paid).** `Program.cs` has a marked `TODO` in
`KeywordCurator.ShouldPost` where an Anthropic API call can classify/summarise items.
It's off by default because the API is paid (tiny at this volume, but not free). Add an
`ANTHROPIC_API_KEY` secret and uncomment the env line in `news.yml` when you want it.

## Things to verify / know

- **War Thunder forum feed** (`…/c/official-news-and-information/7.rss`) is the standard
  Discourse category-RSS pattern but I haven't confirmed it resolves. If that feed 404s
  or returns HTML instead of XML, the run logs a warning and carries on with the Steam
  feed. Remove the `Forum` line if you don't want it.
- **World of Warships official feed** should return RSS; if it ever returns HTML, same
  graceful fallback to Steam applies.
- **Announced updates only.** Steam feeds carry what a studio *posts as an announcement*.
  A silent background patch with no announcement won't appear — inherent to the source.
- **Scheduling.** GitHub cron is best-effort and can lag a few minutes. A once-a-day
  heartbeat commit keeps the workflow from being auto-disabled during quiet stretches.

## Layout

```
GameNewsToDiscord/
  Program.cs          # fetch → dedupe → filter → post
  GameNewsToDiscord.csproj
  feeds.json          # the games and their feeds (edit this)
state/
  seen.json           # created on first run; committed back automatically
  heartbeat.txt       # once-a-day keepalive
.github/workflows/
  news.yml            # the schedule + commit-state-back
```
