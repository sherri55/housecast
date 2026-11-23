using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using NAudio.CoreAudioApi;

namespace AirPlayTray;

public sealed class FlyoutViewModel : INotifyPropertyChanged
{
    private readonly OwnToneClient _client;
    private readonly string _distro;
    private Dictionary<string, DeviceModels.DeviceModel> _models = [];
    private DateTime _modelsReadAt = DateTime.MinValue;
    private bool _readingModels;
    private bool _isEditingGroup;
    private string _groupError = "";
    private WindowsSettings.SpeakerGroup? _editingGroup;
    // Held for the app's lifetime: once the device is collected, its volume control silently stops working
    private readonly MMDevice _localDevice;
    private readonly AudioEndpointVolume _localSpeakers;
    private readonly bool _localWasMuted;
    private string _status = "Starting AirPlay engine…";
    private int _masterVolume = 50;

    public FlyoutViewModel(OwnToneClient client, string distro)
    {
        _client = client;
        _distro = distro;
        using var enumerator = new MMDeviceEnumerator();
        _localDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _localSpeakers = _localDevice.AudioEndpointVolume;
        _localWasMuted = _localSpeakers.Mute;
        LoadGroups();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => Status.Length > 0;

    // AirPlay plays about two seconds behind this PC, so local speakers are usually muted
    public bool PlayOnThisPc
    {
        get => !_localSpeakers.Mute;
        set
        {
            _localSpeakers.Mute = !value;
            OnPropertyChanged(nameof(PlayOnThisPc));
        }
    }

    public int MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = value;
            OnPropertyChanged(nameof(MasterVolume));
            _ = _client.SetMasterVolumeAsync(value);
        }
    }

    /// <summary>
    /// The header's summary of where the sound is going.
    /// </summary>
    public string NowPlaying
    {
        get
        {
            var names = Devices.Where(d => d.IsSelected).Select(d => d.Name).ToList();
            return names.Count switch
            {
                0 => "Not playing on any speaker",
                1 => $"Playing on {names[0]}",
                2 => $"Playing on {names[0]} and {names[1]}",
                _ => $"Playing on {names[0]}, {names[1]} and {names.Count - 2} more",
            };
        }
    }

    public bool HasGroups => Groups.Count > 0;

    public async Task RefreshAsync()
    {
        IReadOnlyList<OwnToneOutput> outputs;
        try
        {
            outputs = await _client.GetOutputsAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Status = Devices.Count == 0 ? "Starting AirPlay engine…" : "AirPlay engine isn't responding";
            return;
        }

        // OwnTone's own "Computer" output is the WSL sound card, which plays nowhere
        var airPlay = outputs.Where(o => o.Type.StartsWith("AirPlay", StringComparison.Ordinal)).ToList();

        foreach (var gone in Devices.Where(d => airPlay.All(o => o.Id != d.Id)).ToList())
            Devices.Remove(gone);

        foreach (var output in airPlay)
        {
            var existing = Devices.FirstOrDefault(d => d.Id == output.Id);
            if (existing is null)
            {
                var index = Devices.TakeWhile(d => string.Compare(d.Name, output.Name, StringComparison.CurrentCultureIgnoreCase) < 0).Count();
                var device = new DeviceViewModel(_client, output);
                device.UserSelectionChanged += SaveSelection;
                device.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeviceViewModel.IsSelected))
                        OnPropertyChanged(nameof(NowPlaying));
                };
                Devices.Insert(index, device);
            }
            else
                existing.Update(output);
        }

        Status = Devices.Count == 0 ? "Looking for AirPlay devices…" : "";
        // Covers speakers that left the network or changed in OwnTone without a click here
        OnPropertyChanged(nameof(NowPlaying));

        foreach (var device in Devices)
            device.Model ??= _models.GetValueOrDefault(device.Name);
        if (Devices.Any(d => d.Model is null) && !_readingModels && DateTime.UtcNow - _modelsReadAt > TimeSpan.FromSeconds(30))
            _ = ReadModelsAsync();

        foreach (var group in Groups)
            group.SetMembers(Devices.Where(d => group.Group.SpeakerIds.Contains(d.Id)));
    }

    public ObservableCollection<GroupViewModel> Groups { get; } = [];

    public bool IsEditingGroup
    {
        get => _isEditingGroup;
        private set
        {
            _isEditingGroup = value;
            OnPropertyChanged(nameof(IsEditingGroup));
            OnPropertyChanged(nameof(IsNotEditingGroup));
        }
    }

    public bool IsNotEditingGroup => !IsEditingGroup;

    public string GroupName { get; set; } = "";

    public ObservableCollection<GroupChoice> GroupChoices { get; } = [];

    public bool CanDeleteGroup => _editingGroup is not null;

    public string GroupEditorTitle => _editingGroup is null ? "New group" : "Edit group";

    public string GroupError
    {
        get => _groupError;
        private set
        {
            _groupError = value;
            OnPropertyChanged(nameof(GroupError));
        }
    }

    public void StartNewGroup() => OpenGroupEditor(null);

    public void StartEditGroup(GroupViewModel group) => OpenGroupEditor(group.Group);

    public void SaveGroup()
    {
        var name = GroupName.Trim();
        var speakerIds = GroupChoices.Where(c => c.IsChosen).Select(c => c.Id).ToArray();
        // Group names are unique, and the saved copies are fresh objects, so match on the name
        var others = WindowsSettings.Groups.Where(g => g.Name != _editingGroup?.Name).ToList();

        if (name.Length == 0)
            GroupError = "Give the group a name.";
        else if (speakerIds.Length == 0)
            GroupError = "Tick at least one speaker.";
        else if (others.Any(g => string.Equals(g.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            GroupError = "There's already a group with that name.";
        else
        {
            WindowsSettings.Groups = [.. others, new WindowsSettings.SpeakerGroup(name, speakerIds)];
            CloseGroupEditor();
        }
    }

    /// <summary>
    /// The saved name of the group open in the editor, or null for a new one.
    /// </summary>
    public string? EditingGroupName => _editingGroup?.Name;

    public void DeleteGroup(string name)
    {
        WindowsSettings.Groups = WindowsSettings.Groups.Where(g => g.Name != name).ToArray();
        if (_editingGroup?.Name == name)
            CloseGroupEditor();
        else
            LoadGroups();
    }

    public void CancelGroupEdit() => CloseGroupEditor();

    private void OpenGroupEditor(WindowsSettings.SpeakerGroup? group)
    {
        _editingGroup = group;
        GroupName = group?.Name ?? "";
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(CanDeleteGroup));
        OnPropertyChanged(nameof(GroupEditorTitle));
        GroupError = "";

        GroupChoices.Clear();
        foreach (var device in Devices)
            GroupChoices.Add(new GroupChoice(device, group?.SpeakerIds.Contains(device.Id) ?? false));
        IsEditingGroup = true;
    }

    private void CloseGroupEditor()
    {
        _editingGroup = null;
        IsEditingGroup = false;
        LoadGroups();
    }

    private void LoadGroups()
    {
        Groups.Clear();
        foreach (var group in WindowsSettings.Groups)
        {
            // Read OwnTone's new state before saving, or the speakers' old ticks get remembered
            var model = new GroupViewModel(_client, group, async () =>
            {
                await RefreshAsync();
                SaveSelection();
            });
            model.SetMembers(Devices.Where(d => group.SpeakerIds.Contains(d.Id)));
            Groups.Add(model);
        }
        OnPropertyChanged(nameof(HasGroups));
    }

    /// <summary>
    /// A speaker in the group editor's list, shown with the same icon and model as the main list.
    /// </summary>
    public sealed class GroupChoice(DeviceViewModel device, bool isChosen)
    {
        public string Id => device.Id;
        public string Name => device.Name;
        public string Icon => device.Icon;
        public string Description => device.Description;
        public bool IsChosen { get; set; } = isChosen;
    }

    public void RestoreLocalSpeakers() => _localSpeakers.Mute = _localWasMuted;

    // Reading the announcements takes a second or two, so it happens in the background
    // and only again when a device turns up that isn't known yet
    private async Task ReadModelsAsync()
    {
        _readingModels = true;
        try
        {
            _models = await DeviceModels.ReadAsync(_distro);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            // WSL isn't reachable yet; the next refresh tries again
        }
        finally
        {
            _modelsReadAt = DateTime.UtcNow;
            _readingModels = false;
        }

        foreach (var device in Devices)
            device.Model ??= _models.GetValueOrDefault(device.Name);
    }

    // Speakers that are switched off right now keep whatever was remembered for them
    private void SaveSelection()
    {
        var listed = Devices.Select(d => d.Id).ToHashSet();
        WindowsSettings.SelectedSpeakerIds = WindowsSettings.SelectedSpeakerIds
            .Where(id => !listed.Contains(id))
            .Concat(Devices.Where(d => d.IsSelected).Select(d => d.Id))
            .ToArray();
    }

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
