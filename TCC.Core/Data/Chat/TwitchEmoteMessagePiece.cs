using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Imaging;
using TCC.ViewModels;

namespace TCC.Data.Chat;

/// <summary>
/// A Twitch, FFZ, BTTV or 7TV emote, with any zero-width emotes drawn over it; shows its name until the image is loaded.
/// </summary>
public class TwitchEmoteMessagePiece : MessagePieceBase
{
    public const double EmoteHeight = 28;

    private readonly List<TwitchEmoteMessagePiece> _overlays = [];
    private BitmapSource? _staticImage;
    private Uri? _animatedImage;
    private double _width = double.NaN;

    /// <summary>
    /// Zero-width emotes drawn over this one.
    /// </summary>
    public IReadOnlyList<TwitchEmoteMessagePiece> Overlays => _overlays;

    public bool IsOverlay { get; }

    public string Description => _overlays.Count == 0 ? Text : string.Join(" ", _overlays.Select(o => o.Text).Prepend(Text));

    public BitmapSource? StaticImage
    {
        get => _staticImage;
        private set => RaiseAndSetIfChanged(value, ref _staticImage);
    }

    /// <summary>
    /// The animated GIF, while animated emotes are enabled.
    /// </summary>
    public Uri? AnimatedImage => App.Settings.TwitchAnimatedEmotes ? _animatedImage : null;

    public bool ShowAnimated => AnimatedImage != null;
    public bool ShowStatic => _staticImage != null && !ShowAnimated;
    // overlays only show their image, the base emote's name is in the tooltip
    public bool ShowName => _staticImage == null && !IsOverlay;

    public double Width
    {
        get => _width;
        private set => RaiseAndSetIfChanged(value, ref _width);
    }

    public double Height => EmoteHeight;

    public TwitchEmoteMessagePiece(TwitchEmote emote, bool isOverlay = false)
    {
        Text = emote.Name;
        IsOverlay = isOverlay;
        Load(emote);
    }

    public void AddOverlay(TwitchEmoteMessagePiece overlay)
    {
        _overlays.Add(overlay);
    }

    /// <summary>
    /// Applies a change of the animated emotes setting.
    /// </summary>
    public void RefreshAnimation()
    {
        InvokePropertyChanged(nameof(AnimatedImage));
        InvokePropertyChanged(nameof(ShowAnimated));
        InvokePropertyChanged(nameof(ShowStatic));
        foreach (var overlay in _overlays) overlay.RefreshAnimation();
    }

    private async void Load(TwitchEmote emote)
    {
        var image = await TwitchEmotes.GetImageAsync(emote);
        if (image == null) return;

        await ChatManager.Instance.Dispatcher.InvokeAsync(() =>
        {
            Width = EmoteHeight * image.AspectRatio;
            _animatedImage = image.AnimatedGif;
            StaticImage = image.Static;
            InvokePropertyChanged(nameof(ShowName));
            RefreshAnimation();
        });
    }
}
