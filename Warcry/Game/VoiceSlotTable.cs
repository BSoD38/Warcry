using System.Collections.Generic;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace Warcry.Game;

// Maps a raw Character.Vfx.VoiceId to the 1-based "Voice N" slot the character creator
// shows. Built once from CharaMakeType (32 rows = 16 tribes x 2 genders, each with a
// 12-entry voice array). No sheet names voices, so "Voice 1..12" is the only label there is.
public sealed class VoiceSlotTable
{
    private readonly Dictionary<(byte Race, byte Sex), ushort[]> voicesByRaceSex = new();

    private VoiceSlotTable() { }

    public static VoiceSlotTable Build(IDataManager data, IPluginLog log)
    {
        var table = new VoiceSlotTable();
        var sheet = data.GetExcelSheet<CharaMakeType>();

        foreach (var row in sheet)
        {
            var race = (byte)row.Race.RowId;
            if (race == 0)
            {
                continue;
            }

            // CharaMakeType.Gender: 0 = Male, 1 = Female, matching CustomizeData.Sex.
            var sex = (byte)row.Gender;
            var key = (race, sex);

            // Voice sets are per race+gender, never per tribe: the two tribes of a race
            // always carry an identical 12-entry list, so first one in wins.
            if (table.voicesByRaceSex.ContainsKey(key))
            {
                continue;
            }

            var voices = new ushort[row.VoiceStruct.Count];
            for (var i = 0; i < voices.Length; i++)
            {
                voices[i] = (ushort)row.VoiceStruct[i];
            }

            table.voicesByRaceSex[key] = voices;
        }

        log.Information("VoiceSlotTable: {Count} race+gender combinations", table.voicesByRaceSex.Count);
        return table;
    }

    // 0 if not found.
    public byte SlotOf(byte race, byte sex, ushort voiceId)
    {
        if (!this.voicesByRaceSex.TryGetValue((race, sex), out var voices))
        {
            return 0;
        }

        for (var i = 0; i < voices.Length; i++)
        {
            if (voices[i] == voiceId)
            {
                return (byte)(i + 1);
            }
        }

        return 0;
    }

}
