using System.Collections.Generic;
using System.Windows.Input;
using Nostrum.WPF;
using Nostrum.WPF.ThreadSafe;
using TCC.Interop.Teralogs;

namespace TCC.UI.Controls.Chat;

public class TeralogsPopupViewModel : ThreadSafeObservableObject
{
    private readonly TeralogsClient _client;

    private string _playerName = "";
    private string _serverName = "";

    public string PlayerName
    {
        get => _playerName;
        set => RaiseAndSetIfChanged(value, ref _playerName);
    }

    private string _emptyText = "No data.";

    public string EmptyText
    {
        get => _emptyText;
        set => RaiseAndSetIfChanged(value, ref _emptyText);
    }

    public ThreadSafeObservableCollection<EncounterViewModel> Encounters { get; }

    public ICommand BrowsePlayer { get; }

    public TeralogsPopupViewModel()
    {
        Encounters = new ThreadSafeObservableCollection<EncounterViewModel>(_dispatcher);

        _client = new TeralogsClient();
        _client.Started += OnSearchStarted;
        _client.Finished += OnSearchFinished;
        _client.Failed += OnSearchFailed;

        BrowsePlayer = new RelayCommand(_ => Utils.Utilities.OpenUrl(TeralogsClient.GetPlayerUrl(_serverName, PlayerName)));
    }

    private void OnSearchStarted()
    {
        _dispatcher.Invoke(() => EmptyText = "Loading...");
    }

    private void OnSearchFinished(List<TeralogsEncounter> list)
    {
        _dispatcher.Invoke(() =>
        {
            EmptyText = "No entries.";
            Encounters.Clear();
            foreach (var e in list)
                Encounters.Add(new EncounterViewModel(e, _serverName));
        });
    }

    private void OnSearchFailed(string error)
    {
        _dispatcher.Invoke(() =>
        {
            EmptyText = $"Failed to retrieve data.\n{error}";
        });
    }

    public void RequestInfo(string name, string serverName)
    {
        _serverName = serverName;
        _dispatcher.Invoke(() =>
        {
            PlayerName = name;
            Encounters.Clear();
        });

        _client.GetEncounters(name, serverName);
    }
}

public class EncounterViewModel
{
    public string BossName { get; }
    public string DungeonName { get; }
    public long PlayerDps { get; }
    public int PlayerDeaths { get; }

    public ICommand Browse { get; }

    public EncounterViewModel(TeralogsEncounter encounter, string serverName)
    {
        DungeonName = encounter.DungeonName;
        BossName = encounter.BossName;
        PlayerDps = encounter.PlayerDps;
        PlayerDeaths = encounter.PlayerDeaths;

        Browse = new RelayCommand(_ => Utils.Utilities.OpenUrl(TeralogsClient.GetLogUrl(serverName, encounter.LogId)));
    }
}

public partial class TeralogsPopup
{
    public TeralogsPopup()
    {
        MouseLeave += (_, _) => WindowManager.ViewModels.PlayerMenuVM.Close();
        InitializeComponent();
    }
}
