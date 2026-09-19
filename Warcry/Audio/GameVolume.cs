namespace Warcry.Audio;

// Reads the game's own volume sliders so plugin audio behaves like game audio.
// Sound* values are 0-100. The IsSnd* booleans are MUTE flags: true means muted.
// Always TryGet: GameConfigSection.GetUInt throws on a missing option.
public sealed class GameVolume
{
    private const double RefreshSeconds = 0.5;

    private double lastRefresh = double.NegativeInfinity;

    public uint Master { get; private set; } = 100;

    public uint Se { get; private set; } = 100;

    public uint Voice { get; private set; } = 100;

    public uint Player { get; private set; } = 100;

    public uint Party { get; private set; } = 100;

    public uint Other { get; private set; } = 100;

    public bool MutedMaster { get; private set; }

    public bool MutedSe { get; private set; }

    public bool MutedVoice { get; private set; }

    // Periodic refresh rather than IGameConfig.SystemChanged — config changes are rare and
    // this avoids depending on the event signature.
    public void Update(double totalSeconds)
    {
        if (totalSeconds - this.lastRefresh < RefreshSeconds)
        {
            return;
        }

        this.lastRefresh = totalSeconds;
        this.Refresh();
    }

    public void Refresh()
    {
        var system = Plugin.GameConfig.System;

        this.Master = Read("SoundMaster", this.Master);
        this.Se = Read("SoundSe", this.Se);
        this.Voice = Read("SoundVoice", this.Voice);
        this.Player = Read("SoundPlayer", this.Player);
        this.Party = Read("SoundParty", this.Party);
        this.Other = Read("SoundOther", this.Other);

        this.MutedMaster = ReadBool("IsSndMaster", this.MutedMaster);
        this.MutedSe = ReadBool("IsSndSe", this.MutedSe);
        this.MutedVoice = ReadBool("IsSndVoice", this.MutedVoice);

        uint Read(string key, uint fallback)
            => system.TryGetUInt(key, out var v) ? v : fallback;

        bool ReadBool(string key, bool fallback)
            => system.TryGetBool(key, out var v) ? v : fallback;
    }

    // soundCategory is Character+0x2369: 0 Player, 1 Party, 2 Other. 0 when muted.
    public float GainFor(byte soundCategory, bool useVoiceBus)
    {
        if (this.MutedMaster || (useVoiceBus ? this.MutedVoice : this.MutedSe))
        {
            return 0f;
        }

        var bus = useVoiceBus ? this.Voice : this.Se;

        var category = soundCategory switch
        {
            0 => this.Player,
            1 => this.Party,
            _ => this.Other,
        };

        return (this.Master / 100f) * (bus / 100f) * (category / 100f);
    }
}
