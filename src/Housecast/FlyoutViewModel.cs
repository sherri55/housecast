using System.Collections.ObjectModel;
using System.ComponentModel;
using AirPlay.Engine;
using NAudio.CoreAudioApi;

namespace Housecast;

public sealed class FlyoutViewModel : INotifyPropertyChanged
{
    private readonly AirPlayEngine _engine;
    private bool _isEditingGroup;
    private string _groupError = "";
    private WindowsSettings.SpeakerGroup? _editingGroup;
    // Held for the app's lifetime: once the device is collected, its volume control silently stops working
    private readonly MMDevice _localDevice;
    private readonly AudioEndpointVolume _localSpeakers;
    private readonly bool _localWasMuted;
    private string _status = "Looking for AirPlay speakers…";
    // Groups a phone or Mac is playing to, and its name
    private readonly Dictionary<string, string> _groupSenders = [];

    public FlyoutViewModel(AirPlayEngine engine)
    {
        _engine = engine;
        _engine.MasterVolume = WindowsSettings.MasterVolume;
        using var enumerator = new MMDeviceEnumerator();
        _localDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        _localSpeakers = _localDevice.AudioEndpointVolume;
        _localWasMuted = _localSpeakers.Mute;
        _engine.GroupPlaybackChanged += (group, sender) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (sender is null)
                _groupSenders.Remove(group);
            else
                _groupSenders[group] = sender;
            Refresh();
        });
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
        get => _engine.MasterVolume;
        set
        {
            _engine.MasterVolume = value;
            WindowsSettings.MasterVolume = value;
            OnPropertyChanged(nameof(MasterVolume));
        }
    }

    /// <summary>
    /// The header's summary of where the sound is going.
    /// </summary>
    public string NowPlaying
    {
        get
        {
            if (_groupSenders.Count > 0)
            {
                var (group, sender) = _groupSenders.First();
                return $"{group} is playing from {sender}";
            }
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

    public void Refresh()
    {
        var speakers = _engine.Speakers;

        foreach (var gone in Devices.Where(d => speakers.All(s => s.Id != d.Id)).ToList())
            Devices.Remove(gone);

        foreach (var speaker in speakers)
        {
            var existing = Devices.FirstOrDefault(d => d.Id == speaker.Id);
            if (existing is null)
            {
                var index = Devices.TakeWhile(d => string.Compare(d.Name, speaker.Name, StringComparison.CurrentCultureIgnoreCase) < 0).Count();
                var device = new DeviceViewModel(_engine, speaker);
                device.UserSelectionChanged += SaveSelection;
                device.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeviceViewModel.IsSelected))
                        OnPropertyChanged(nameof(NowPlaying));
                };
                Devices.Insert(index, device);
            }
            else
                existing.Update();
        }

        Status = Devices.Count == 0 ? "Looking for AirPlay speakers…" : "";
        // Covers speakers that left the network or dropped us without a click here
        OnPropertyChanged(nameof(NowPlaying));

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
            // Read the engine's new state before saving, or the speakers' old ticks get remembered
            var model = new GroupViewModel(group, () =>
            {
                Refresh();
                SaveSelection();
            });
            model.SetMembers(Devices.Where(d => group.SpeakerIds.Contains(d.Id)));
            Groups.Add(model);
        }
        OnPropertyChanged(nameof(HasGroups));

        // Each group is also an AirPlay speaker phones and Macs can play to
        _ = _engine.PublishGroupsAsync(WindowsSettings.Groups.Select(g => new AirPlayGroup(g.Name, g.SpeakerIds.Select(id =>
        {
            var setting = WindowsSettings.SpeakerSettingFor(id);
            return new GroupMember(id, setting.Volume, setting.OffsetMs);
        }).ToList())).ToList());
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
