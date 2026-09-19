namespace Warcry.Game;

// VoiceId alone is not a safe key: the six ARR race+gender combos share voice ids (33/35/
// 37/39 appear for both Hyur-Midlander-M and Elezen-M). Match race+sex with the voice.
// Au Ra (97-120), Hrothgar (121-144) and Viera (145-168) do have exclusive ranges.
public readonly record struct CasterKey(byte Race, byte Tribe, byte Sex, ushort VoiceId, byte VoiceSlot)
{
    public static readonly CasterKey None = new(0, 0, 0, 0, 0);

    public bool IsValid => Race != 0;
}
