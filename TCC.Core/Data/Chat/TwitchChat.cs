using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TCC.Debugging;
using TCC.Utils;
using TCC.ViewModels;

namespace TCC.Data.Chat;

/// <summary>
/// Read-only Twitch chat client: joins a channel anonymously over Twitch's IRC WebSocket
/// and adds its messages to TCC chat on the <see cref="ChatChannel.Twitch"/> channel.
/// </summary>
public class TwitchChat
{
    private static TwitchChat? _instance;
    public static TwitchChat Instance => _instance ??= new TwitchChat();

    private static readonly Uri ServerUri = new("wss://irc-ws.chat.twitch.tv:443");
    // anonymous login: any "justinfan" nick can read chat without a token
    private const string AnonymousNick = "justinfan64537";
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;

    private TwitchChat()
    {
    }

    /// <summary>
    /// (Re)connects to the given channel; an empty name disconnects.
    /// </summary>
    public void Start(string? channel)
    {
        channel = (channel ?? "").Trim().TrimStart('#').ToLowerInvariant();

        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
            TwitchEmotes.ClearChannel();
            if (channel == "") return;

            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = TwitchEmotes.LoadGlobalAsync();
            Task.Run(() => RunAsync(channel, cts.Token));
        }
    }

    public void Stop() => Start(null);

    private async Task RunAsync(string channel, CancellationToken ct)
    {
        var retryDelay = TimeSpan.FromSeconds(2);
        var notifiedFailure = false;

        while (!ct.IsCancellationRequested)
        {
            var joined = false;
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(ServerUri, ct);
                await SendAsync(ws, "CAP REQ :twitch.tv/tags twitch.tv/commands", ct);
                await SendAsync(ws, $"NICK {AnonymousNick}", ct);
                await SendAsync(ws, $"JOIN #{channel}", ct);

                await foreach (var line in ReadLinesAsync(ws, ct))
                {
                    var msg = IrcMessage.Parse(line);
                    switch (msg.Command)
                    {
                        case "PING":
                            await SendAsync(ws, $"PONG :{msg.Trailing}", ct);
                            break;
                        case "JOIN" when msg.Nick == AnonymousNick:
                            joined = true;
                            notifiedFailure = false;
                            retryDelay = TimeSpan.FromSeconds(2);
                            ChatManager.Instance.AddTccMessage($"Joined Twitch chat #{channel}.");
                            break;
                        case "PRIVMSG":
                            OnPrivMsg(msg);
                            break;
                        case "ROOMSTATE":
                            OnRoomState(msg);
                            break;
                        case "NOTICE":
                            ChatManager.Instance.AddTccMessage($"Twitch: {msg.Trailing}");
                            break;
                        case "RECONNECT":
                            // server maintenance: reconnect right away
                            retryDelay = TimeSpan.Zero;
                            break;
                    }

                    if (msg.Command == "RECONNECT") break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.F($"Twitch chat connection error: {e}");
            }

            if (ct.IsCancellationRequested) return;

            if (joined) ChatManager.Instance.AddTccMessage($"Disconnected from Twitch chat #{channel}, reconnecting...");
            else if (!notifiedFailure && retryDelay > TimeSpan.Zero)
            {
                ChatManager.Instance.AddTccMessage($"Couldn't join Twitch chat #{channel}, retrying...");
                notifiedFailure = true;
            }

            try
            {
                await Task.Delay(retryDelay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            retryDelay = TimeSpan.FromSeconds(Math.Clamp(retryDelay.TotalSeconds * 2, 2, MaxRetryDelay.TotalSeconds));
        }
    }

    private static void OnPrivMsg(IrcMessage msg)
    {
        var text = msg.Trailing;

        // "/me" messages are sent as CTCP ACTION
        const string action = "\u0001ACTION ";
        if (text.StartsWith(action, StringComparison.Ordinal))
            text = text[action.Length..].TrimEnd('\u0001');

        var author = msg.Tags.TryGetValue("display-name", out var displayName) && displayName != ""
            ? displayName
            : msg.Nick;
        var color = msg.Tags.GetValueOrDefault("color", "");
        var emotes = msg.Tags.GetValueOrDefault("emotes", "");

        var cm = ChatManager.Instance;
        cm.AddChatMessage(cm.Factory.CreateTwitchMessage(author, text, color, emotes));
    }

    private static void OnRoomState(IrcMessage msg)
    {
        if (!msg.Tags.TryGetValue("room-id", out var roomId) || roomId == "") return;
        _ = TwitchEmotes.LoadChannelAsync(roomId);
    }

    private static Task SendAsync(ClientWebSocket ws, string line, CancellationToken ct)
    {
        return ws.SendAsync(Encoding.UTF8.GetBytes(line + "\r\n"), WebSocketMessageType.Text, true, ct);
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(ClientWebSocket ws, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new byte[8192];
        var pending = new StringBuilder();

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) yield break;

            pending.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;

            // one frame can carry several IRC lines
            foreach (var line in pending.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
                yield return line;
            pending.Clear();
        }
    }

    /// <summary>
    /// Minimal IRCv3 line: [@tags] [:prefix] COMMAND [params] [:trailing]
    /// </summary>
    private sealed class IrcMessage
    {
        public Dictionary<string, string> Tags { get; } = new();
        public string Nick { get; private set; } = "";
        public string Command { get; private set; } = "";
        public string Trailing { get; private set; } = "";

        public static IrcMessage Parse(string line)
        {
            var msg = new IrcMessage();
            var rest = line;

            if (rest.StartsWith('@'))
            {
                var end = rest.IndexOf(' ');
                if (end == -1) return msg;
                foreach (var tag in rest[1..end].Split(';'))
                {
                    var eq = tag.IndexOf('=');
                    if (eq == -1) msg.Tags[tag] = "";
                    else msg.Tags[tag[..eq]] = UnescapeTagValue(tag[(eq + 1)..]);
                }
                rest = rest[(end + 1)..];
            }

            if (rest.StartsWith(':'))
            {
                var end = rest.IndexOf(' ');
                if (end == -1) return msg;
                var prefix = rest[1..end];
                var bang = prefix.IndexOf('!');
                msg.Nick = bang == -1 ? prefix : prefix[..bang];
                rest = rest[(end + 1)..];
            }

            var trailingStart = rest.IndexOf(" :", StringComparison.Ordinal);
            var head = rest;
            if (trailingStart != -1)
            {
                msg.Trailing = rest[(trailingStart + 2)..];
                head = rest[..trailingStart];
            }
            else if (rest.StartsWith(':'))
            {
                msg.Trailing = rest[1..];
                head = "";
            }

            var space = head.IndexOf(' ');
            msg.Command = space == -1 ? head : head[..space];
            // PING may carry its argument without a colon
            if (msg.Command == "PING" && msg.Trailing == "" && space != -1) msg.Trailing = head[(space + 1)..];
            return msg;
        }

        private static string UnescapeTagValue(string value)
        {
            if (!value.Contains('\\')) return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i == value.Length - 1)
                {
                    sb.Append(value[i]);
                    continue;
                }
                i++;
                sb.Append(value[i] switch
                {
                    's' => ' ',
                    ':' => ';',
                    'r' => '\r',
                    'n' => '\n',
                    _ => value[i]
                });
            }
            return sb.ToString();
        }
    }
}
