using HtmlAgilityPack;
using Nostrum.Extensions;
using Nostrum.WPF.Extensions;
using Nostrum.WPF.ThreadSafe;
using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TCC.Debugging;
using TCC.R;
using TCC.Utilities;
using TCC.Utils;
using TCC.ViewModels;
using TeraPacketParser.Analysis;

namespace TCC.Data.Chat;

public class ChatMessage : ThreadSafeObservableObject, IDisposable
{
    #region Properties

    private bool _animate = true;
    private bool _isVisible;
    private bool _hasTranslation;
    private ChatMessage? _translation;

    public ChatChannel Channel { get; protected set; }
    public string Timestamp { get; }
    public string RawMessage { get; protected init; }
    public string Author { get; set; } = "";
    public ulong AuthorGameId { get; set; }
    public uint AuthorPlayerId { get; set; }
    private uint AuthorServerId { get; }
    public bool ContainsPlayerName { get; set; }
    public bool IsGm { get; protected set; }
    public bool ShowTimestamp => App.Settings.ShowTimestamp;
    public bool ShowChannel => App.Settings.ShowChannel;
    public bool Animate
    {
        get => _animate && App.Settings.AnimateChatMessages;
        set => _animate = value;
    }
    public ThreadSafeObservableCollection<MessageLine> Lines { get; }
    public ThreadSafeObservableCollection<MessagePieceBase> Pieces { get; }
    public ThreadSafeObservableCollection<MessageLine> DisplayedLines
    {
        get
        {
            var ret = _translation != null
                    ? App.Settings.TranslationMode is TranslationMode.MergedTranslationFirst
                        ? _translation.Lines
                        : Lines
                    : Lines;

            var lastLine = ret.LastOrDefault();

            if (lastLine != null)
            {
                if (lastLine.LinePieces.LastOrDefault() is not TranslationIndicatorPiece)
                {
                    lastLine.LinePieces.Add(new TranslationIndicatorPiece { Container = this });
                }
            }
            return ret;
        }
    }
    public ThreadSafeObservableCollection<MessageLine> SecondaryLines
    {
        get
        {
            var ret = _translation != null
                    ? App.Settings.TranslationMode is TranslationMode.MergedOriginalFirst
                        ? _translation.Lines
                        : Lines
                    : Lines;

            var lastLine = ret.LastOrDefault();
            if (lastLine != null)
            {
                if (lastLine.LinePieces.LastOrDefault() is TranslationIndicatorPiece tip)
                {
                    lastLine.LinePieces.Remove(tip);
                }
            }
            return ret;
        }
    }
    public ThreadSafeObservableCollection<MessagePieceBase> UsedPieces => _translation != null
    ? App.Settings.TranslationMode is TranslationMode.MergedTranslationFirst
        ? _translation.Pieces
        : Pieces
    : Pieces;
    public ThreadSafeObservableCollection<MessagePieceBase> SecondaryPieces => _translation != null
    ? App.Settings.TranslationMode is TranslationMode.MergedOriginalFirst
        ? _translation.Pieces
        : Pieces
    : Pieces;
    public bool IsShowingTranslationFirst => App.Settings.TranslationMode is TranslationMode.MergedTranslationFirst;
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            foreach (var p in UsedPieces.ToArray()) p.IsVisible = value;
            foreach (var p in SecondaryPieces.ToArray()) p.IsVisible = value;
            if (value)
            {
                SettingsWindowViewModel.ChatShowChannelChanged += NotifyShowChannelChanged;
                SettingsWindowViewModel.ChatShowTimestampChanged += NotifyShowTimestampChanged;
                SettingsWindowViewModel.FontSizeChanged += NotifyFontSizeChanged;
                SettingsWindowViewModel.TranslationModeChanged += NotifyTranslationModeChanged;
            }
            else
            {
                SettingsWindowViewModel.ChatShowChannelChanged -= NotifyShowChannelChanged;
                SettingsWindowViewModel.ChatShowTimestampChanged -= NotifyShowTimestampChanged;
                SettingsWindowViewModel.FontSizeChanged -= NotifyFontSizeChanged;
                SettingsWindowViewModel.TranslationModeChanged -= NotifyTranslationModeChanged;
            }

            if (!RaiseAndSetIfChanged(value, ref _isVisible)) return;
            if (_translation is not null)
            {
                _translation.IsVisible = value;
            }
        }
    }
    public bool HasTranslation
    {
        get => _hasTranslation;
        set
        {
            if (!RaiseAndSetIfChanged(value, ref _hasTranslation)) return;
            InvokePropertyChanged(nameof(DisplayedLines));
        }
    }
    public int Size => App.Settings.FontSize;
    public string PlainMessage { get; protected init; }
    public string DisplayedAuthor { get; protected init; } = string.Empty;

    #endregion Properties

    protected ChatMessage()
    {
        ObjectTracker.Register(GetType());
        Dispatcher = ChatManager.Instance.Dispatcher;
        Pieces = new ThreadSafeObservableCollection<MessagePieceBase>(_dispatcher);
        Lines = new ThreadSafeObservableCollection<MessageLine>(_dispatcher);
        Timestamp = App.Settings.ChatTimestampSeconds ? DateTime.Now.ToLongTimeString() : DateTime.Now.ToShortTimeString();
        RawMessage = "";
        PlainMessage = "";
    }

    public ChatMessage(ChatChannel ch) : this()
    {
        Channel = ch;
    }

    public ChatMessage(ChatChannel ch, string auth, string msg, ulong authorGameId, bool isGm, uint authorPlayerId, uint authorServerId) : this()
    {
        Channel = ch;
        RawMessage = msg;
        IsGm = isGm;
        var authHtml = new HtmlDocument();
        authHtml.LoadHtml(auth);
        Author = authHtml.DocumentNode.InnerText;
        AuthorPlayerId = authorPlayerId;
        AuthorServerId = authorServerId;
        AuthorGameId = authorGameId;

        if (AuthorServerId != Game.Me.ServerId && AuthorServerId != 0)
        {
            var srv = PacketAnalyzer.ServerDatabase.GetServer(AuthorServerId);
            DisplayedAuthor = $"{Author}@({srv.Region})-{srv.Name}";
        }
        else
        {
            DisplayedAuthor = Author;
        }

        try
        {
            if (Channel == ChatChannel.Raid && Game.Group.Leader.Name == Author) Channel = ChatChannel.RaidLeader;
            switch (ch)
            {
                case ChatChannel.Greet:
                case ChatChannel.Angler:
                    ParseDirectMessage(RawMessage.UnescapeHtml());
                    break;

                case ChatChannel.Emote:
                    ParseEmoteMessage(msg);
                    break;

                default:
                    //ParseFormattedMessage(msg);
                    ParseHtmlMessage(msg);
                    break;
            }

            foreach (var piece in Pieces)
            {
                PlainMessage += piece.Text;
            }
        }
        catch (Exception e)
        {
            Log.F($"Failed to parse chat message: channel={ch} raw={msg}\n{e}");
        }
    }

    ~ChatMessage()
    {
        ObjectTracker.Unregister(GetType());
    }

    internal void AddPiece(MessagePieceBase mp)
    {
        mp.Container = this;
        Pieces.Add(mp);
    }

    private void InsertPiece(MessagePieceBase mp, int index)
    {
        mp.Container = this;
        Pieces.Insert(index, mp);
    }

    private void RemovePiece(MessagePieceBase mp)
    {
        Pieces.Remove(mp);
    }

    //TODO: refactor
    public void SplitSimplePieces()
    {
        var simplePieces = Pieces.ToSyncList().Where(item => item is SimpleMessagePiece and not UrlMessagePiece);

        foreach (var simplePiece in simplePieces)
        {
            simplePiece.Text = simplePiece.Text.Replace(" ", " [[");
            var words = simplePiece.Text.Split(new[] { "[[" }, StringSplitOptions.RemoveEmptyEntries);
            var index = Pieces.IndexOf(simplePiece);
            foreach (var word in words)
            {
                var endsWithK = word.ToLower().EndsWith("k ", StringComparison.InvariantCultureIgnoreCase) ||
                                word.ToLower().EndsWith("k", StringComparison.InvariantCultureIgnoreCase);
                var endsWithG = word.ToLower().EndsWith("g ", StringComparison.InvariantCultureIgnoreCase) ||
                                word.ToLower().EndsWith("g", StringComparison.InvariantCultureIgnoreCase);
                var isNumber = int.TryParse(word.ToLower().Replace("k ", "").Replace("k", "").Replace("g ", "").Replace("g", ""), out var money);
                MessagePieceBase mp;
                if ((endsWithK || endsWithG) && isNumber && Channel is ChatChannel.Trade or ChatChannel.TradeRedirect)
                {
                    mp = new MoneyMessagePiece(new Money(endsWithK ? money * 1000 : money, 0, 0));
                }
                else if (simplePiece is ActionMessagePiece amp)
                {
                    mp = new ActionMessagePiece(word, amp.ChatLinkAction)
                    {
                        Color = amp.Color,
                        Size = amp.Size
                    };
                }
                else
                {
                    mp = new SimpleMessagePiece(word) { Color = simplePiece.Color, Size = simplePiece.Size };
                }

                InsertPiece(mp, index);
                index = Pieces.IndexOf(mp) + 1;
            }
            RemovePiece(simplePiece);
        }

        // split lines
        Lines.Add(new MessageLine());
        foreach (var item in Pieces)
        {
            if (item.Text.Contains("\r\n") || item.Text.Contains("\n\t") || item.Text.Contains("\n"))
            {
                item.Text = item.Text.Replace("\r\n", "").Replace("\n\t", "").Replace("\n", "");
                Lines.Add(new MessageLine());
            }
            Lines.ToSyncList().Last().LinePieces.Add(item);
        }
    }

    private void NotifyShowChannelChanged()
    {
        InvokePropertyChanged(nameof(ShowChannel));
    }

    private void NotifyShowTimestampChanged()
    {
        InvokePropertyChanged(nameof(ShowTimestamp));
    }

    private void NotifyFontSizeChanged()
    {
        InvokePropertyChanged(nameof(Size));
    }

    private void NotifyTranslationModeChanged()
    {
        InvokePropertyChanged(nameof(IsShowingTranslationFirst));
        InvokePropertyChanged(nameof(DisplayedLines));
        InvokePropertyChanged(nameof(SecondaryLines));
    }

    private void ParseDirectMessage(string msg)
    {
        // greet messages can contain emotes too
        AddTextWithEmotes(msg, "");
    }

    private void ParseEmoteMessage(string msg)
    {
        const string header = "@social:";
        var start = msg.IndexOf(header, StringComparison.Ordinal);
        if (start == -1)
        {
            AddPiece(new SimpleMessagePiece(Author + " " + msg, App.Settings.FontSize, false));
            return;
        }
        start += header.Length;
        var id = uint.Parse(msg[start..]);
        var text = Game.DB!.SocialDatabase.Social[id].Replace("{Name}", Author);
        AddPiece(new SimpleMessagePiece(text, App.Settings.FontSize, false));
    }

    private void ParseHtmlMessage(string msg)
    {
        var html = new HtmlDocument(); html.LoadHtml(msg);
        var htmlPieces = html.DocumentNode.ChildNodes;

        foreach (var htmlPiece in htmlPieces)
        {
            ParseHtmlPiece(htmlPiece);
        }
    }

    private void ParseHtmlPiece(HtmlNode piece)
    {
        if (piece.HasAttributes)
        {
            var customColor = ChatUtils.GetCustomColor(piece);

            // heroic items bright magenta is ugly af
            if (customColor == "F93ECE")
                customColor = Colors.ItemHeroicColor.ToHex(sharp: false);

            if (piece.HasChildNodes && piece.ChildNodes.Count == 1 && piece.ChildNodes[0].Name != "#text")
            {
                //parse ChatLinkAction
                var chatLinkAction = piece.ChildNodes.FirstOrDefault(x =>
                    x.Name.IndexOf("ChatLinkAction", StringComparison.InvariantCultureIgnoreCase) != -1);
                if (chatLinkAction != null)
                {
                    var mp = MessagePieceBuilder.ParseChatLinkAction(chatLinkAction);
                    mp.Color = customColor;
                    AddPiece(mp);
                }
                else
                {
                    foreach (var node in piece.ChildNodes)
                    {
                        ParseHtmlPiece(node);
                    }
                }
            }
            else
            {
                //parse normal formatted piece
                var text = piece.InnerText;
                if (!App.Loading) ContainsPlayerName = TccUtils.CheckMention(text);
                CheckRedirect(text);
                var content = GetPieceContent(text);
                if (content != "")
                {
                    AddTextWithEmotes(
                        content
                            .Replace("<a href=\"asfunction:chatLinkAction\">", "")
                            .Replace("</a>", "")
                            .UnescapeHtml(), customColor
                    );
                }
            }
        }
        else
        {
            //parse normal non formatted piece
            var text = piece.InnerText;
            if (!App.Loading) ContainsPlayerName = TccUtils.CheckMention(text);
            CheckRedirect(text);
            var content = GetPieceContent(text);
            if (content == "") return;
            AddTextWithEmotes(
                content
                    .Replace("<a href=\"asfunction:chatLinkAction\">", "")
                    .Replace("</a>", "")
                    .UnescapeHtml(),
                Channel == ChatChannel.ReceivedWhisper ? Brushes.GoldBrush.Color.ToHex() : ""
            );
        }
    }

    // The client sends chat emotes as text placeholders instead of <img> tags:
    // {@img#} src="img://__Icon_Emote.Panda.emote_surprised" width="24" height="24" ... {@img/#}
    private static readonly Regex EmotePlaceholderRegex = new(@"\{@img#\}(.*?)\{@img/#\}", RegexOptions.Compiled);
    // matches only the icon path, so it works however the src quotes are encoded (", &quot;, &#34;)
    private static readonly Regex EmoteSourceRegex = new(@"img://__([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    /// <summary>
    /// Adds text as message pieces, turning emote placeholders into icons.
    /// </summary>
    private void AddTextWithEmotes(string text, string color)
    {
        // UnescapeHtml() doesn't decode numeric entities (&#34;, &#123;), which can hide the placeholder
        text = System.Net.WebUtility.HtmlDecode(text);
        var last = 0;
        var matches = EmotePlaceholderRegex.Matches(text);
        if (matches.Count == 0 && text.Contains("@img"))
            Log.F($"Chat text mentions @img but has no emote placeholder: {text}\nRaw message: {RawMessage}");
        foreach (Match match in matches)
        {
            AddTextPiece(text[last..match.Index], color);
            var source = EmoteSourceRegex.Match(match.Groups[1].Value);
            if (source.Success)
            {
                AddPiece(new IconMessagePiece(source.Groups[1].Value.ToLower(), App.Settings.FontSize, false));
            }
            else
            {
                Log.F($"Unrecognized chat emote placeholder: {match.Value}\nRaw message: {RawMessage}");
                AddTextPiece(match.Value, color);
            }
            last = match.Index + match.Length;
        }
        AddTextPiece(text[last..], color);
    }

    private void AddTextPiece(string text, string color)
    {
        // SplitSimplePieces() starts a new line before any piece containing "\n", so give each
        // line break its own piece; otherwise text before the break (e.g. " = :nekohi:\n")
        // moves to the next line, away from the emote it belongs to
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = i == 0 ? lines[i] : "\n" + lines[i];
            if (line == "") continue;
            AddPiece(new SimpleMessagePiece(line, App.Settings.FontSize, false, color));
        }
    }

    private void CheckRedirect(string text)
    {
        //redirect trading message if it's in global
        if ((text.Contains("WTS", StringComparison.InvariantCultureIgnoreCase) ||
             text.Contains("WTB", StringComparison.InvariantCultureIgnoreCase) ||
             text.Contains("WTT", StringComparison.InvariantCultureIgnoreCase)) &&
            Channel == ChatChannel.Global) Channel = ChatChannel.TradeRedirect;
    }

    private string GetPieceContent(string text)
    {
        var textToSplit = text.Replace(" ", " [[");
        var split = textToSplit.Split(new[] { "[[" }, StringSplitOptions.RemoveEmptyEntries).ToList();
        var content = new StringBuilder("");
        foreach (var token in split)
        {
            var rgxUrl = new Regex(@"^http(s)?://([\w-]+.)+[\w-]+(/[\w- ./?%&=])?$");
            if (rgxUrl.IsMatch(token)
                || token.StartsWith("discord.gg")
                || token.StartsWith("twitch.tv", StringComparison.OrdinalIgnoreCase))
            {
                //add it as url
                if (content.ToString() != "")
                {
                    AddPiece(new SimpleMessagePiece(content.ToString().UnescapeHtml(), App.Settings.FontSize, false));
                    content = new StringBuilder("");
                }
                AddPiece(new UrlMessagePiece(token.UnescapeHtml(), App.Settings.FontSize, false, "7289da"));
            }
            else
            {
                //add it as string
                content.Append(token);
            }
        }
        return content.ToString();
    }

    protected virtual void DisposeImpl()
    {
        SettingsWindowViewModel.ChatShowChannelChanged -= NotifyShowChannelChanged;
        SettingsWindowViewModel.ChatShowTimestampChanged -= NotifyShowTimestampChanged;
        SettingsWindowViewModel.FontSizeChanged -= NotifyFontSizeChanged;
        SettingsWindowViewModel.TranslationModeChanged -= NotifyTranslationModeChanged;

        foreach (var messagePiece in Pieces.ToSyncList())
        {
            messagePiece.Dispose();
        }
        Pieces.Clear();

        _translation?.Dispose();
    }

    public void Dispose()
    {
        DisposeImpl();
    }

    public void AddTranslation(ChatMessage message)
    {
        message.SplitSimplePieces();
        _translation = message;
        _translation.IsVisible = IsVisible;
        InvokePropertyChanged(nameof(DisplayedLines));
        InvokePropertyChanged(nameof(SecondaryLines));
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        _dispatcher.Invoke(() =>
        {
            foreach (var item in Pieces.ToSyncList())
            {
                sb.Append(item.Text);
            }
        });
        return sb.ToString();
    }
}