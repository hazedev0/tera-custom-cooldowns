using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Nostrum.WPF.Extensions;
using TCC.Utilities;
using TCC.Utils;

namespace TCC.Data.Chat;

/// <summary>
/// A Twitch chat message, with Twitch/BTTV/7TV emotes and the author's Twitch name color.
/// </summary>
public class TwitchChatMessage : ChatMessage
{
    // Twitch's colors for users who never picked one
    private static readonly string[] DefaultColors =
    [
        "#FF0000", "#0000FF", "#008000", "#B22222", "#FF7F50", "#9ACD32", "#FF4500", "#2E8B57",
        "#DAA520", "#D2691E", "#5F9EA0", "#1E90FF", "#FF69B4", "#8A2BE2", "#00FF7F"
    ];

    private static readonly Regex UrlRegex = new(@"^(https?://|www\.)\S+$|^[\w-]+(\.[\w-]+)*\.(com|net|org|tv|gg|io|app|me)(/\S*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <param name="author">display name</param>
    /// <param name="text">plain message text</param>
    /// <param name="color">the author's color tag ("#RRGGBB" or empty)</param>
    /// <param name="emotesTag">Twitch's emotes tag, e.g. "25:0-4,12-16/1902:6-10"</param>
    public TwitchChatMessage(string author, string text, string color, string emotesTag)
    {
        Channel = ChatChannel.Twitch;
        Author = author;
        DisplayedAuthor = author;
        RawMessage = text;
        PlainMessage = text;
        CustomAuthorBrush = GetAuthorBrush(author, color);

        try
        {
            if (!App.Loading) ContainsPlayerName = TccUtils.CheckMention(text);
            BuildPieces(text, ParseTwitchEmotes(text, emotesTag));
        }
        catch (Exception e)
        {
            Log.F($"Failed to parse Twitch message: {text} (emotes={emotesTag})\n{e}");
            Pieces.Clear();
            AddPiece(new SimpleMessagePiece(text, App.Settings.FontSize, false));
        }
    }

    /// <summary>
    /// Applies a change of the animated emotes setting.
    /// </summary>
    public void RefreshEmoteAnimation()
    {
        foreach (var piece in Pieces.ToSyncList().OfType<TwitchEmoteMessagePiece>()) piece.RefreshAnimation();
    }

    private void BuildPieces(string text, Dictionary<int, (int End, TwitchEmote Emote)> twitchEmotes)
    {
        var plain = new StringBuilder();
        // last emote added, if nothing but spaces came after it
        TwitchEmoteMessagePiece? lastEmote = null;

        void FlushText()
        {
            if (plain.Length == 0) return;
            AddPiece(new SimpleMessagePiece(plain.ToString(), App.Settings.FontSize, false));
            plain.Clear();
            lastEmote = null;
        }

        void AddEmote(TwitchEmote emote)
        {
            // zero-width emotes are drawn over the previous emote, dropping the space between them
            if (emote.ZeroWidth && lastEmote != null && plain.ToString().Trim() == "")
            {
                plain.Clear();
                lastEmote.AddOverlay(new TwitchEmoteMessagePiece(emote, isOverlay: true));
                return;
            }

            FlushText();
            lastEmote = new TwitchEmoteMessagePiece(emote);
            AddPiece(lastEmote);
        }

        var i = 0;
        while (i < text.Length)
        {
            if (twitchEmotes.TryGetValue(i, out var twitchEmote))
            {
                AddEmote(twitchEmote.Emote);
                i = twitchEmote.End;
                continue;
            }

            if (text[i] == ' ')
            {
                plain.Append(' ');
                i++;
                continue;
            }

            // a word: up to the next space or Twitch emote
            var end = i;
            while (end < text.Length && text[end] != ' ' && !twitchEmotes.ContainsKey(end)) end++;
            var word = text[i..end];

            var emote = TwitchEmotes.Find(word);
            if (emote != null)
            {
                AddEmote(emote);
            }
            else if (UrlRegex.IsMatch(word))
            {
                FlushText();
                AddPiece(new UrlMessagePiece(word, App.Settings.FontSize, false, "7289da"));
                lastEmote = null;
            }
            else
            {
                plain.Append(word);
            }

            i = end;
        }

        FlushText();
    }

    /// <summary>
    /// Maps the emotes tag to string ranges: start index -> (end index, emote).
    /// Tag positions count Unicode code points, not UTF-16 chars.
    /// </summary>
    private static Dictionary<int, (int End, TwitchEmote Emote)> ParseTwitchEmotes(string text, string emotesTag)
    {
        var ret = new Dictionary<int, (int, TwitchEmote)>();
        if (string.IsNullOrEmpty(emotesTag)) return ret;

        // code point index -> char index (plus one past the end)
        var charIndex = new List<int>();
        for (var i = 0; i < text.Length; i++)
        {
            charIndex.Add(i);
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
        }
        charIndex.Add(text.Length);

        foreach (var emoteDef in emotesTag.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = emoteDef.IndexOf(':');
            if (colon == -1) continue;
            var id = emoteDef[..colon];

            foreach (var range in emoteDef[(colon + 1)..].Split(','))
            {
                var dash = range.IndexOf('-');
                if (dash == -1
                    || !int.TryParse(range[..dash], out var first)
                    || !int.TryParse(range[(dash + 1)..], out var last)
                    || first < 0 || last < first || last + 1 >= charIndex.Count) continue;

                var start = charIndex[first];
                var end = charIndex[last + 1];
                ret[start] = (end, new TwitchEmote(TwitchEmoteProvider.Twitch, id, text[start..end], false));
            }
        }

        return ret;
    }

    private static Brush GetAuthorBrush(string author, string color)
    {
        Color c;
        try
        {
            c = (Color)ColorConverter.ConvertFromString(color != "" ? color : DefaultColor(author));
        }
        catch
        {
            c = (Color)ColorConverter.ConvertFromString(DefaultColor(author));
        }

        var brush = new SolidColorBrush(EnsureReadable(c));
        brush.Freeze();
        return brush;
    }

    private static string DefaultColor(string author)
    {
        var name = author.ToLowerInvariant();
        return name.Length == 0 ? DefaultColors[0] : DefaultColors[(name[0] + name[^1]) % DefaultColors.Length];
    }

    /// <summary>
    /// Brightens colors that would be too dark on the chat background.
    /// </summary>
    private static Color EnsureReadable(Color c)
    {
        static double Luminance(Color x) => (0.2126 * x.R + 0.7152 * x.G + 0.0722 * x.B) / 255;

        const double minLuminance = .45;
        var l = Luminance(c);
        if (l >= minLuminance) return c;

        // blend towards white until readable
        var t = (minLuminance - l) / (1 - l);
        return Color.FromRgb(
            (byte)(c.R + (255 - c.R) * t),
            (byte)(c.G + (255 - c.G) * t),
            (byte)(c.B + (255 - c.B) * t));
    }
}
