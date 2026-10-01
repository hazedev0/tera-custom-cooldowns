using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;
using TCC.Debugging;
using TCC.Utils;

namespace TCC.Data.Chat;

public enum TwitchEmoteProvider
{
    Twitch,
    Bttv,
    SevenTv,
    Ffz
}

/// <param name="ZeroWidth">drawn over the previous emote instead of next to it</param>
public record TwitchEmote(TwitchEmoteProvider Provider, string Id, string Name, bool Animated, bool ZeroWidth = false)
{
    public string CacheKey => $"{Provider.ToString().ToLowerInvariant()}_{Id}";

    public string Url => Provider switch
    {
        // "default" serves an animated GIF for animated emotes and a PNG otherwise
        TwitchEmoteProvider.Twitch => $"https://static-cdn.jtvnw.net/emoticons/v2/{Id}/default/dark/2.0",
        TwitchEmoteProvider.Bttv => $"https://cdn.betterttv.net/emote/{Id}/2x.{(Animated ? "gif" : "png")}",
        TwitchEmoteProvider.SevenTv => $"https://cdn.7tv.app/emote/{Id}/2x.{(Animated ? "gif" : "png")}",
        TwitchEmoteProvider.Ffz => Animated ? $"https://cdn.frankerfacez.com/emote/{Id}/animated/2.gif" : $"https://cdn.frankerfacez.com/emote/{Id}/2",
        _ => throw new ArgumentOutOfRangeException()
    };
}

/// <summary>
/// Image of a downloaded emote: a frozen bitmap (the first frame of animated ones) and, for animated GIFs, the file.
/// </summary>
public record TwitchEmoteImage(BitmapSource Static, Uri? AnimatedGif, double AspectRatio);

/// <summary>
/// Third-party (FFZ, BTTV, 7TV) emote sets and the on-disk cache of emote images.
/// </summary>
public static class TwitchEmotes
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly string CachePath = Path.Combine(App.ResourcesPath, "cache", "twitch-emotes");

    private static readonly ConcurrentDictionary<string, Task<TwitchEmoteImage?>> Images = new();

    private static Dictionary<string, TwitchEmote> _globalEmotes = new();
    private static Dictionary<string, TwitchEmote> _channelEmotes = new();
    private static bool _globalLoaded;
    private static string _channelRoomId = "";

    static TwitchEmotes()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("TCC");
    }

    /// <summary>
    /// Third-party emote with this exact name, channel emotes first.
    /// </summary>
    public static TwitchEmote? Find(string word)
    {
        if (_channelEmotes.TryGetValue(word, out var emote)) return emote;
        return _globalEmotes.TryGetValue(word, out emote) ? emote : null;
    }

    public static async Task LoadGlobalAsync()
    {
        if (_globalLoaded) return;

        // later providers override earlier ones with the same name: FFZ < BTTV < 7TV
        var emotes = new Dictionary<string, TwitchEmote>();
        await TryLoad("FFZ global", "https://api.frankerfacez.com/v1/set/global", json =>
        {
            foreach (var setId in json["default_sets"] ?? new JArray())
                AddFfz(emotes, json["sets"]?[setId.ToString()]?["emoticons"] as JArray);
        });
        await TryLoad("BTTV global", "https://api.betterttv.net/3/cached/emotes/global", json => AddBttv(emotes, json as JArray));
        await TryLoad("7TV global", "https://7tv.io/v3/emote-sets/global", json => AddSevenTv(emotes, json["emotes"] as JArray));
        _globalEmotes = emotes;
        // retried on the next (re)connect if nothing loaded
        _globalLoaded = emotes.Count > 0;
    }

    public static async Task LoadChannelAsync(string roomId)
    {
        if (roomId == _channelRoomId) return;
        _channelRoomId = roomId;

        var emotes = new Dictionary<string, TwitchEmote>();
        await TryLoad("FFZ channel", $"https://api.frankerfacez.com/v1/room/id/{roomId}", json =>
        {
            var setId = json["room"]?["set"]?.ToString();
            if (setId != null) AddFfz(emotes, json["sets"]?[setId]?["emoticons"] as JArray);
        });
        await TryLoad("BTTV channel", $"https://api.betterttv.net/3/cached/users/twitch/{roomId}", json =>
        {
            AddBttv(emotes, json["sharedEmotes"] as JArray);
            AddBttv(emotes, json["channelEmotes"] as JArray);
        });
        await TryLoad("7TV channel", $"https://7tv.io/v3/users/twitch/{roomId}", json => AddSevenTv(emotes, json["emote_set"]?["emotes"] as JArray));
        if (roomId == _channelRoomId) _channelEmotes = emotes;
    }

    public static void ClearChannel()
    {
        _channelRoomId = "";
        _channelEmotes = new Dictionary<string, TwitchEmote>();
    }

    private static async Task TryLoad(string what, string url, Action<JToken> parse)
    {
        try
        {
            using var response = await Http.GetAsync(url);
            // channels without an FFZ/BTTV/7TV account return 404
            if (!response.IsSuccessStatusCode) return;
            parse(JToken.Parse(await response.Content.ReadAsStringAsync()));
        }
        catch (Exception e)
        {
            Log.F($"Failed to load {what} emotes: {e.Message}");
        }
    }

    private static void AddBttv(Dictionary<string, TwitchEmote> emotes, JArray? list)
    {
        if (list == null) return;
        foreach (var e in list)
        {
            var id = e.Value<string>("id");
            var code = e.Value<string>("code");
            if (id == null || code == null) continue;
            emotes[code] = new TwitchEmote(TwitchEmoteProvider.Bttv, id, code, e.Value<string>("imageType") == "gif",
                BttvZeroWidth.Contains(code));
        }
    }

    // BTTV has no zero-width flag; these are the global overlay emotes
    private static readonly HashSet<string> BttvZeroWidth =
        ["SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat"];

    private static void AddFfz(Dictionary<string, TwitchEmote> emotes, JArray? list)
    {
        if (list == null) return;
        foreach (var e in list)
        {
            var id = e.Value<string>("id");
            var name = e.Value<string>("name");
            // modifiers transform the previous emote (flip, spin...), which isn't supported
            if (id == null || name == null || e.Value<bool?>("modifier") == true) continue;
            emotes[name] = new TwitchEmote(TwitchEmoteProvider.Ffz, id, name, e["animated"] is JObject);
        }
    }

    private static void AddSevenTv(Dictionary<string, TwitchEmote> emotes, JArray? list)
    {
        if (list == null) return;
        foreach (var e in list)
        {
            var id = e.Value<string>("id");
            var name = e.Value<string>("name");
            if (id == null || name == null) continue;
            const int zeroWidthFlag = 1; // flag of the emote in the set
            const int zeroWidthDataFlag = 1 << 8; // flag of the emote itself
            var zeroWidth = ((e.Value<int?>("flags") ?? 0) & zeroWidthFlag) != 0
                            || ((e["data"]?.Value<int?>("flags") ?? 0) & zeroWidthDataFlag) != 0;
            emotes[name] = new TwitchEmote(TwitchEmoteProvider.SevenTv, id, name, e["data"]?.Value<bool?>("animated") == true, zeroWidth);
        }
    }

    /// <summary>
    /// Loads the emote image, downloading it to the cache the first time.
    /// </summary>
    public static Task<TwitchEmoteImage?> GetImageAsync(TwitchEmote emote)
    {
        return Images.GetOrAdd(emote.CacheKey, _ => Task.Run(() => LoadImageAsync(emote)));
    }

    private static async Task<TwitchEmoteImage?> LoadImageAsync(TwitchEmote emote)
    {
        try
        {
            Directory.CreateDirectory(CachePath);
            var path = FindCached(emote.CacheKey);
            if (path == null)
            {
                var bytes = await Http.GetByteArrayAsync(emote.Url);
                path = Path.Combine(CachePath, emote.CacheKey + (IsGif(bytes) ? ".gif" : ".png"));
                await File.WriteAllBytesAsync(path, bytes);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            var ratio = bitmap.PixelHeight == 0 ? 1 : bitmap.PixelWidth / (double)bitmap.PixelHeight;

            // for GIFs the bitmap is the first frame
            return new TwitchEmoteImage(bitmap, path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ? new Uri(path) : null, ratio);
        }
        catch (Exception e)
        {
            Log.F($"Failed to load Twitch emote {emote.Name} ({emote.Url}): {e.Message}");
            // allow retrying later
            Images.TryRemove(emote.CacheKey, out _);
            return null;
        }
    }

    private static string? FindCached(string key)
    {
        foreach (var ext in new[] { ".png", ".gif" })
        {
            var path = Path.Combine(CachePath, key + ext);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static bool IsGif(byte[] bytes) => bytes.Length > 3 && bytes[0] == 'G' && bytes[1] == 'I' && bytes[2] == 'F';
}
