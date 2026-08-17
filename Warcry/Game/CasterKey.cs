namespace Warcry.Game;

/// <summary>
/// Everything about a caster that a voice profile can match on.
/// </summary>
/// <remarks>
/// <para><see cref="VoiceId"/> alone is NOT a safe key: the six ARR race+gender combos
/// share voice ids with each other (e.g. 33/35/37/39 appear for both Hyur-Midlander-M
/// and Elezen-M). Always match on race+sex together with the voice.</para>
/// <para>Au Ra (97-120), Hrothgar (121-144) and Viera (145-168) do have contiguous
/// exclusive ranges — confirmed in practice by a male Hrothgar reading voiceId 130.</para>
/// </remarks>
public readonly record struct CasterKey(byte Race, byte Tribe, byte Sex, ushort VoiceId, byte VoiceSlot)
{
    public static readonly CasterKey None = new(0, 0, 0, 0, 0);

    public bool IsValid => Race != 0;
}
