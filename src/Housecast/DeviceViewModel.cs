using System.ComponentModel;
using System.Runtime.CompilerServices;
using AirPlay.Engine;
using AirPlay.Engine.Discovery;

namespace Housecast;

public sealed class DeviceViewModel : INotifyPropertyChanged
{
    private readonly AirPlayEngine _engine;
    private readonly DeviceModels.DeviceModel _model;
    private bool _isSelected;
    private int _volume;
    private int _offsetMs;
    private bool _needsPin;
    private bool _isBusy;

    public DeviceViewModel(AirPlayEngine engine, AirPlaySpeaker speaker)
    {
        _engine = engine;
        Id = speaker.Id;
        Name = speaker.Name;
        _model = DeviceModels.Describe(speaker.Model, speaker.Manufacturer);
        var setting = WindowsSettings.SpeakerSettingFor(Id);
        _volume = setting.Volume;
        _offsetMs = setting.OffsetMs;
        _isSelected = engine.IsPlaying(Id);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised after the user ticked or unticked this device and the engine has applied it.
    /// </summary>
    public event Action? UserSelectionChanged;

    public string Id { get; }
    public string Name { get; }
    public string Pin { get; set; } = "";

    public string Icon => _model.Icon;
    public string Description => _model.Description;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (Set(ref _isSelected, value))
                _ = SetPlayingAsync(value);
        }
    }

    public int Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, value))
                return;
            SaveSetting();
            _ = _engine.SetVolumeAsync(Id, value);
        }
    }

    public int OffsetMs
    {
        get => _offsetMs;
        set
        {
            if (!Set(ref _offsetMs, value))
                return;
            SaveSetting();
            _engine.SetOffset(Id, value);
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
    /// Takes the engine's latest state, e.g. after the speaker dropped us, without sending anything.
    /// </summary>
    public void Update()
    {
        if (!IsBusy)
            Set(ref _isSelected, _engine.IsPlaying(Id), nameof(IsSelected));
    }

    /// <summary>
    /// Starts or stops this speaker, as ticking it does; groups use it for each member.
    /// </summary>
    public async Task SetPlayingAsync(bool playing)
    {
        Set(ref _isSelected, playing, nameof(IsSelected));
        IsBusy = true;
        try
        {
            if (!playing)
            {
                await _engine.StopAsync(Id);
                return;
            }

            // A device that has never been paired shows a PIN instead of playing
            var result = await _engine.PlayAsync(Id, Volume, OffsetMs);
            NeedsPin = result == PlayResult.NeedsPin;
            Set(ref _isSelected, result == PlayResult.Playing, nameof(IsSelected));
        }
        finally
        {
            IsBusy = false;
            UserSelectionChanged?.Invoke();
        }
    }

    public async Task SubmitPinAsync()
    {
        // A wrong PIN ends that pairing attempt too; ticking the speaker again shows a new PIN
        var paired = await _engine.SubmitPinAsync(Id, Pin.Trim());
        NeedsPin = false;
        if (!paired)
            return;

        Pin = "";
        OnPropertyChanged(nameof(Pin));
        await SetPlayingAsync(true);
    }

    private void SaveSetting() =>
        WindowsSettings.SaveSpeakerSetting(Id, new WindowsSettings.SpeakerSetting(_volume, _offsetMs));

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
