using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nostrum;

namespace TCC.Interop.Teralogs;

public class TeralogsEncounter
{
    public long LogId { get; init; }
    public string DungeonName { get; init; } = "";
    public string BossName { get; init; } = "";
    public long PlayerDps { get; init; }
    public int PlayerDeaths { get; init; }
}

/// <summary>
/// Looks up a player's most recent logs on teralogs.com (the log site of the current private servers).
/// </summary>
public class TeralogsClient
{
    private const string API_URL = "https://api.teralogs.com/graphql";
    private const string SITE_URL = "https://teralogs.com";
    private const int MAX_ENCOUNTERS = 5;

    // search matches player names exactly (ignoring case) and returns the newest logs first
    private static readonly string SEARCH_QUERY = "query($name: String!, $server: String) { search(searchVars: {query: $name, serverName: $server, limit: " + MAX_ENCOUNTERS + "}) { entries { id dungeon boss summary } } }";

    private bool _requestInProgress;

    public event Action? Started;

    public event Action<List<TeralogsEncounter>>? Finished;

    public event Action<string>? Failed;

    /// <summary>
    /// The website section of a server: its first word, e.g. teralogs.com/agaia for "Agaia Online".
    /// </summary>
    private static string GetSiteSection(string serverName)
    {
        var firstWord = serverName.Split(' ')[0];
        return new string(firstWord.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    public static string GetLogUrl(string serverName, long logId)
    {
        return $"{SITE_URL}/{GetSiteSection(serverName)}/logs/party/{logId}";
    }

    public static string GetPlayerUrl(string serverName, string playerName)
    {
        return $"{SITE_URL}/{GetSiteSection(serverName)}/search/{Uri.EscapeDataString(playerName)}";
    }

    public void GetEncounters(string playerName, string serverName)
    {
        Task.Run(async () =>
        {
            if (_requestInProgress) return;
            Started?.Invoke();
            _requestInProgress = true;
            var results = new List<TeralogsEncounter>();
            using var webClient = MiscUtils.GetDefaultHttpClient();

            try
            {
                var request = new JObject
                {
                    { "query", SEARCH_QUERY },
                    { "variables", new JObject { { "name", playerName }, { "server", serverName } } }
                };
                var resp = await webClient.PostAsync(API_URL, new StringContent(request.ToString(), Encoding.UTF8, "application/json"));
                var jResp = JObject.Parse(await resp.Content.ReadAsStringAsync());
                if (jResp["errors"] is JArray { Count: > 0 } errors)
                    throw new Exception(errors[0]["message"]?.Value<string>() ?? "Unknown error");

                foreach (var jEntry in jResp["data"]!["search"]!["entries"]!)
                {
                    // summary: JSON array with each party member's name, dps and deaths
                    var jPlayer = JArray.Parse(jEntry["summary"]!.Value<string>()!)
                        .FirstOrDefault(p => string.Equals(p["playerName"]?.Value<string>(), playerName, StringComparison.InvariantCultureIgnoreCase));
                    if (jPlayer == null) continue;

                    results.Add(new TeralogsEncounter
                    {
                        LogId = jEntry["id"]!.Value<long>(),
                        DungeonName = jEntry["dungeon"]!.Value<string>()!,
                        BossName = jEntry["boss"]!.Value<string>()!,
                        PlayerDps = jPlayer["playerDps"]!.Value<long>(),
                        PlayerDeaths = jPlayer["playerDeaths"]!.Value<int>()
                    });
                }
            }
            catch (Exception e)
            {
                Failed?.Invoke(e.Message);
                _requestInProgress = false;
                return;
            }

            Finished?.Invoke(results);
            _requestInProgress = false;
        });
    }
}
