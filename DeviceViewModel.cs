using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AirPlayTray;

public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private readonly OwnToneClient _client;
    private bool _isSelected;
    private int _volume;
    private int _offsetMs;
    private bool _needsPin;
    private bool _isBusy;
    private DeviceModels.DeviceModel? _model;

    public DeviceViewModel(OwnToneClient client, OwnToneOutput output)
    {
        _client = client;
        Id = output.Id;
        Name = output.Name;
        Update(output);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the user ticked or unticked this device and OwnTone has applied it.
    /// </summary>
    public event Action? UserSelectionChanged;

    public string Id { get; }
    public string Name { get; }
    public string Pin { get; set; } = "";

    public DeviceModels.DeviceModel? Model
    {
        get => _model;
        set
        {
            if (!Set(ref _model, value))
                return;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(Description));
        }
    }

    public string Icon => Model?.Icon ?? DeviceModels.SpeakerIcon;
    public string Description => Model?.Description ?? "AirPlay";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value))
                return;
            _ = ApplySelectionAsync(value);
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            if (Set(ref _volume, value))
                _ = _client.SetVolumeAsync(Id, value);
        }
    }

    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (Set(ref _offsetMs, value))
                _ = ApplyOffsetAsync(value);
        }
    }

    public bool NeedsPin
    {
        get => _needsPin;
        private set => Set(ref _needsPin, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>
    /// Takes the latest state from OwnTone without sending anything back to it.
    /// </summary>
    public void Update(OwnToneOutput output)
    {
        if (IsBusy)
            return;

        Set(ref _isSelected, output.Selected, nameof(IsSelected));
        Set(ref _volume, output.Volume, nameof(Volume));
        Set(ref _offsetMs, output.OffsetMs, nameof(OffsetMs));
    }

    public async Task SubmitPinAsync()
    {
        IsBusy = true;
        try
        {
            if (!await _client.SendPinAsync(Id, Pin.Trim()))
                return;

            NeedsPin = false;
            Pin = "";
            OnPropertyChanged(nameof(Pin));
            Set(ref _isSelected, await _client.SetSelectedAsync(Id, true), nameof(IsSelected));
        }
        finally
        {
            IsBusy = false;
            UserSelectionChanged?.Invoke();
        }
    }

    // OwnTone only reads a device's offset when it connects, so a playing device reconnects to use it
    private async Task ApplyOffsetAsync(int offsetMs)
    {
        IsBusy = true;
        try
        {
            if (!await _client.SetOffsetAsync(Id, offsetMs) || !_isSelected)
                return;

            await _client.SetSelectedAsync(Id, false);
            await _client.SetSelectedAsync(Id, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplySelectionAsync(bool selected)
    {
        IsBusy = true;
        try
        {
            if (await _client.SetSelectedAsync(Id, selected) || !selected)
                return;

            // A device that has never been paired shows a PIN instead of playing
            var output = (await _client.GetOutputsAsync()).FirstOrDefault(o => o.Id == Id);
            NeedsPin = output is { RequiresAuth: true, NeedsAuthKey: true };
            Set(ref _isSelected, false, nameof(IsSelected));
        }
        finally
        {
            IsBusy = false;
            UserSelectionChanged?.Invoke();
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
