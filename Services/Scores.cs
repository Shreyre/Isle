using System.Text.Json;

namespace Isle.Services;

public sealed record Side(string Abbr, string Score, bool Winner = false, bool Batting = false, string Name = "")
{
    // cricket reads "187 & 9/0 (8 ov)": the runs in big type, the overs (or "65 ov, target 313") as a footnote
    public string Main { get; } = Score.Split(" (")[0];
    public string Note { get; } = Score.IndexOf(" (") is int i && i >= 0 ? Score[(i + 2)..].Replace(") (", " · ").TrimEnd(')') : "";
}

/// One fixture from ESPN's scoreboard feed (the data behind Google's live score cards; Google itself has no API).
/// State is "pre", "in" or "post"; Refresh is the query that re-reads just this match's league.
/// Detail is cricket's one-line state of play ("Nagaland lead by 12 runs"); Start is local time.
public sealed record Fixture(string Id, string Sport, string Refresh, string League, string State, string Summary, Side A, Side B, string Detail = "", DateTime Start = default)
{
    public bool Live => State == "in";
    // built once per fetch, not per frame (the capsule reads them 30 times a second); construct anew rather than `with`
    /// "IND 245/3 (38.2 ov) – 210 NZ": away team first, the way ESPN and Google list it
    public string Line { get; } = $"{A.Abbr} {A.Score} – {B.Score} {B.Abbr}".Replace("  ", " ");
    /// the status for the capsule's trailing slot ("67'", "Innings break"; longer ones are cut)
    public string Short { get; } = Summary.Length > 14 ? Summary[..13] + "…" : Summary;
}

public static class Scores
{
    // the chips on the Scores page; some sports need a league or the feed answers 400
    public static readonly (string Label, string Glyph, string Query)[] Sports =
    {
        ("Cricket", "\U0001F3CF", "sport=cricket"),
        ("Football", "⚽", "sport=soccer"),
        ("NBA", "\U0001F3C0", "sport=basketball&league=nba"),
        ("NFL", "\U0001F3C8", "sport=football&league=nfl"),
        ("Tennis", "\U0001F3BE", "sport=tennis"),
        ("NHL", "\U0001F3D2", "sport=hockey&league=nhl"),
    };

    public static string GlyphFor(string sport) => sport switch
    {
        "cricket" => "\U0001F3CF", "soccer" => "⚽", "basketball" => "\U0001F3C0", "football" => "\U0001F3C8",
        "tennis" => "\U0001F3BE", "hockey" => "\U0001F3D2", _ => "\U0001F3C6",
    };

    /// Every fixture the feed lists for a query (live, upcoming and just finished), or null when it can't be reached.
    public static async Task<List<Fixture>> GetAsync(string query)
    {
        try
        {
            await using var s = await SystemInfo.Http.GetStreamAsync("https://site.web.api.espn.com/apis/v2/scoreboard/header?region=in&lang=en&" + query);
            using var doc = await JsonDocument.ParseAsync(s);
            var list = new List<Fixture>();
            foreach (var sport in doc.RootElement.GetProperty("sports").EnumerateArray())
            {
                string sp = Str(sport, "slug");
                foreach (var lg in sport.GetProperty("leagues").EnumerateArray())
                {
                    if (!lg.TryGetProperty("events", out var evs)) continue;
                    string refresh = $"sport={sp}&league={Str(lg, "slug")}", league = Str(lg, "shortName");
                    foreach (var e in evs.EnumerateArray())
                    {
                        if (!e.TryGetProperty("competitors", out var c) || c.GetArrayLength() < 2) continue;
                        e.TryGetProperty("fullStatus", out var fs);
                        string bat = fs.ValueKind == JsonValueKind.Object && fs.TryGetProperty("battingTeamId", out var b) ? b.GetRawText() : "";
                        var start = e.TryGetProperty("date", out var d) && d.TryGetDateTime(out var t) ? t.ToLocalTime() : default;
                        list.Add(new Fixture(Str(e, "id"), sp, refresh, league, Str(e, "status"), Str(e, "summary"), SideOf(c[0], bat), SideOf(c[1], bat),
                            System.Net.WebUtility.HtmlDecode(Str(fs, "summary")), start));
                    }
                }
            }
            return list;
        }
        catch (Exception ex) { AppHost.Log("Scores: " + ex.Message); return null; }

        static Side SideOf(JsonElement c, string bat) => new(Str(c, "abbreviation"), Str(c, "score"),
            c.TryGetProperty("winner", out var w) && w.ValueKind == JsonValueKind.True, bat.Length > 0 && bat == Str(c, "id"), Str(c, "displayName"));
    }

    static string Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : "";

    /// Cricket scores read "245/3 (38.2 ov)": the wickets after the slash (all out reads as plain runs).
    public static int Wickets(string score)
    {
        int i = score.IndexOf('/');
        if (i < 0) return 0;
        int n = 0;
        for (int j = i + 1; j < score.Length && char.IsDigit(score[j]); j++) n = n * 10 + score[j] - '0';
        return n;
    }

#if DEBUG
    public static void SelfCheck()
    {
        System.Diagnostics.Debug.Assert(Wickets("245/3 (38.2 ov)") == 3);
        System.Diagnostics.Debug.Assert(Wickets("184 (71 ov)") == 0);
        System.Diagnostics.Debug.Assert(Wickets("312/10") == 10);
        var m = new Fixture("1", "soccer", "", "", "in", "67'", new Side("ARS", "2"), new Side("CHE", "1"));
        System.Diagnostics.Debug.Assert(m.Line == "ARS 2 – 1 CHE");
        var pre = new Fixture("1", "soccer", "", "", "pre", "Sat 20:00 GMT+1 kick-off", new Side("ARS", ""), new Side("CHE", ""));
        System.Diagnostics.Debug.Assert(pre.Line == "ARS – CHE");
        System.Diagnostics.Debug.Assert(pre.Short == "Sat 20:00 GMT…" && m.Short == "67'");
        var bat = new Side("NAGA", "187 & 9/0 (8 ov)");
        System.Diagnostics.Debug.Assert(bat.Main == "187 & 9/0" && bat.Note == "8 ov");
        var fo = new Side("RAJ", "162 & 256 (70.4 ov) (f/o)");
        System.Diagnostics.Debug.Assert(fo.Main == "162 & 256" && fo.Note == "70.4 ov · f/o");
        System.Diagnostics.Debug.Assert(new Side("ARS", "2").Note == "" && new Side("ARS", "").Main == "");
    }
#endif
}
