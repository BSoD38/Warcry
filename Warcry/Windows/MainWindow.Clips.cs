using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Warcry.Windows;

/// <summary>The Clips tab: the imported-audio library.</summary>
public sealed partial class MainWindow
{
    private void DrawClips()
    {
        var lib = this.plugin.Clips;

        // Declared once per frame; the label is the shared payload key with the target below.
        if (Plugin.DragDrop.ServiceAvailable)
        {
            Plugin.DragDrop.CreateImGuiSource(
                "WarcryAudio",
                m => m.Extensions.Overlaps([".wav", ".ogg"]),
                m =>
                {
                    ImGui.Text($"Import {m.Files.Count} audio file(s) into Warcry");
                    return true;
                });
        }

        if (ImGui.Button("Import audio..."))
        {
            this.fileDialog.OpenFileDialog(
                "Import audio",
                "Audio files{.wav,.ogg}",
                (ok, paths) =>
                {
                    if (!ok)
                    {
                        return;
                    }

                    foreach (var p in paths)
                    {
                        lib.Import(p);
                    }
                },
                selectionCountMax: 0);
        }

        ImGui.SameLine();
        if (ImGui.Button("Open folder"))
        {
            Dalamud.Utility.Util.OpenLink(lib.ClipsDirectory);
        }

        ImGui.TextDisabled(Plugin.DragDrop.ServiceAvailable
            ? "wav and ogg, mono or stereo, any sample rate, up to 15s. You can also drag files onto this list."
            : "wav and ogg, mono or stereo, any sample rate, up to 15s.");

        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        var listHeight = MathF.Max(120f, avail.Y - 90f);

        if (ImGui.BeginChild("##cliplist", new Vector2(0, listHeight), true))
        {
            if (lib.Count == 0)
            {
                ImGui.TextDisabled("No clips yet. Import a wav or ogg, then use an action.");
            }

            foreach (var clip in lib.Clips.ToArray())
            {
                ImGui.PushID(clip.Info.Hash);

                if (ImGui.SmallButton("Play"))
                {
                    this.plugin.PlayClip(clip);
                }

                ImGui.SameLine();
                if (ImGui.SmallButton("Remove"))
                {
                    lib.Remove(clip.Info.Hash);
                    ImGui.PopID();
                    continue;
                }

                ImGui.SameLine();
                ImGui.TextUnformatted(
                    $"{clip.Info.DisplayName}   {clip.Info.DurationMs / 1000.0:0.00}s" +
                    $"   [{clip.Info.SourceSampleRate} Hz, {(clip.Info.SourceChannels == 1 ? "mono" : "stereo")}]");

                ImGui.PopID();
            }
        }

        ImGui.EndChild();

        // Attach the drop target to the list we just drew.
        if (Plugin.DragDrop.ServiceAvailable &&
            Plugin.DragDrop.CreateImGuiTarget("WarcryAudio", out var files, out _))
        {
            foreach (var f in files)
            {
                lib.Import(f);
            }
        }

        if (lib.Errors.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"{lib.Errors.Count} problem(s):");
            foreach (var e in lib.Errors.Take(4))
            {
                ImGui.TextDisabled($"  {e}");
            }

            if (ImGui.SmallButton("Dismiss"))
            {
                lib.ClearErrors();
            }
        }
    }
}
