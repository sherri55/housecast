using System.ComponentModel;

namespace AirPlayTray;

/// <summary>
/// A named set of speakers shown as one row: ticking it plays on all of them,
/// and its volume sets them all at once.
/// </summary>
public sealed class GroupViewModel : INotifyPropertyChanged
{
    private readonly Action _afterSelectionChanged;
    private IReadOnlyList<DeviceViewModel> _members = [];
    // Shows the user's click straight away, until every member has started or stopped
    private bool? _pendingSelection;

    public GroupViewModel(WindowsSettings.SpeakerGroup group, Action afterSelectionChanged)
    {
        Group = group;
        _afterSelectionChanged = afterSelectionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WindowsSettings.SpeakerGroup Group { get; }
    public string Name => Group.Name;
    public string Icon => DeviceModels.GroupIcon;

    public string Description => _members.Count switch
    {
        0 => "None of its speakers are on the network",
        1 => $"1 speaker · {_members[0].Name}",
        _ => $"{_members.Count} speakers · {string.Join(", ", _members.Select(m => m.Name))}",
    };

    /// <summary>
    /// The group's speakers that are on the network, shown as pills on the group's card.
    /// </summary>
    public IReadOnlyList<DeviceViewModel> Members => _members;

    /// <summary>
    /// Ticked when every speaker in the group that is on the network is playing.
    /// </summary>
    public bool IsSelected
    {
        get => _pendingSelection ?? (_members.Count > 0 && _members.All(m => m.IsSelected));
        set
        {
            _pendingSelection = value;
            OnPropertyChanged(nameof(IsSelected));
            _ = ApplySelectionAsync(value);
        }
    }

    public int Volume
    {
        get => _members.Count == 0 ? 0 : (int)Math.Round(_members.Average(m => m.Volume));
        set
        {
            foreach (var member in _members)
                member.Volume = value;
        }
    }

    /// <summary>
    /// Points the group at the speakers currently on the network.
    /// </summary>
    public void SetMembers(IEnumerable<DeviceViewModel> members)
    {
        foreach (var member in _members)
            member.PropertyChanged -= OnMemberChanged;
        _members = members.ToList();
        foreach (var member in _members)
            member.PropertyChanged += OnMemberChanged;

        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Members));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(Volume));
    }

    private async Task ApplySelectionAsync(bool selected)
    {
        try
        {
            await Task.WhenAll(_members.Select(m => m.SetPlayingAsync(selected)));
            _afterSelectionChanged();
        }
        finally
        {
            _pendingSelection = null;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceViewModel.IsSelected) or nameof(DeviceViewModel.Volume))
            OnPropertyChanged(e.PropertyName);
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
