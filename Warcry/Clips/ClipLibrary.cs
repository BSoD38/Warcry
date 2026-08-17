using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Warcry.Clips;

/// <summary>
/// Owns the user's imported audio: copies it in, content-addresses it, decodes it once,
/// and hands out fresh sample providers over the shared buffer.
/// </summary>
/// <remarks>
/// <para><b>Content addressing</b> is the whole design. The user picks
/// <c>C:\stuff\fire.wav</c>; we copy it in and key everything on the SHA-256 of its
/// bytes. Their mappings never break when they move or rename the source, importing the
/// same clip twice is free and automatically deduplicated, the stored filename can never
/// contain a character that breaks a path or a JSON string, re-import is idempotent, and
/// garbage collection is a set difference.</para>
/// <para>Metadata lives in <c>clipmeta.json</c> written with System.Text.Json — NOT in
/// IPluginConfiguration, which serialises with TypeNameHandling.Objects and writes
/// through a 64 MB-capped backing store. See docs/PLAN.md 5.8.</para>
/// </remarks>
public sealed class ClipLibrary : IDisposable
{
    public const int SampleRate = 44100;
    public const int MaxDurationMs = 15_000;

    private static readonly string[] Accepted = [".wav", ".ogg"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string root;
    private readonly string clipsDir;
    private readonly string manifestPath;
    private readonly IPluginLog log;

    private readonly Dictionary<string, CachedClip> cache = [];
    private readonly List<string> errors = [];

    public ClipLibrary(string configDirectory, IPluginLog log)
    {
        this.log = log;
        this.root = configDirectory;
        this.clipsDir = Path.Combine(configDirectory, "clips");
        this.manifestPath = Path.Combine(configDirectory, "clipmeta.json");

        // GetPluginConfigDirectory() is not guaranteed to exist.
        Directory.CreateDirectory(this.clipsDir);
    }

    /// <summary>Snapshot of what is loaded. Read on the main thread only.</summary>
    public IReadOnlyCollection<CachedClip> Clips => this.cache.Values;

    public int Count => this.cache.Count;

    public IReadOnlyList<string> Errors => this.errors;

    public string ClipsDirectory => this.clipsDir;

    public bool TryGet(string hash, out CachedClip clip) => this.cache.TryGetValue(hash, out clip!);

    /// <summary>Rescans and decodes everything on disk. Off the game thread.</summary>
    public Task LoadAsync() => Task.Run(() =>
    {
        try
        {
            var manifest = this.ReadManifest();
            var loaded = new Dictionary<string, CachedClip>();

            foreach (var info in manifest.Clips)
            {
                var full = Path.Combine(this.clipsDir, info.Relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    this.errors.Add($"{info.DisplayName}: file missing ({info.Relative})");
                    continue;
                }

                try
                {
                    var samples = Decode(full, out _, out _);
                    loaded[info.Hash] = new CachedClip(info, samples);
                }
                catch (Exception ex)
                {
                    this.errors.Add($"{info.DisplayName}: {ex.Message}");
                }
            }

            // Publish as one swap rather than mutating while the game thread may read.
            lock (this.cache)
            {
                this.cache.Clear();
                foreach (var (k, v) in loaded)
                {
                    this.cache[k] = v;
                }
            }

            this.log.Information("ClipLibrary: loaded {Count} clips, {Errors} errors", loaded.Count, this.errors.Count);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ClipLibrary: load failed");
        }
    });

    /// <summary>
    /// Copies a user-selected file into the library, decodes it, and returns its hash.
    /// Idempotent — re-importing the same bytes is a no-op.
    /// </summary>
    public string? Import(string sourcePath)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!Accepted.Contains(ext))
            {
                this.errors.Add($"{Path.GetFileName(sourcePath)}: unsupported type {ext} (wav and ogg only)");
                return null;
            }

            if (!File.Exists(sourcePath))
            {
                this.errors.Add($"{sourcePath}: not found");
                return null;
            }

            string hash;
            using (var fs = File.OpenRead(sourcePath))
            {
                hash = Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
            }

            if (this.cache.ContainsKey(hash))
            {
                this.log.Information("ClipLibrary: {Name} already imported", Path.GetFileName(sourcePath));
                return hash;
            }

            // Shard by the first two hex chars so one directory never holds thousands.
            var shard = hash[..2];
            var relative = $"{shard}/{hash}{ext}";
            var destDir = Path.Combine(this.clipsDir, shard);
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, hash + ext);

            if (!File.Exists(dest))
            {
                // Temp-then-move in the same directory: atomic on NTFS, no partial file on crash.
                var tmp = dest + ".tmp";
                File.Copy(sourcePath, tmp, overwrite: true);
                File.Move(tmp, dest, overwrite: true);
            }

            var samples = Decode(dest, out var srcRate, out var srcChannels);
            var durationMs = (int)(samples.Length * 1000L / SampleRate);

            if (durationMs > MaxDurationMs)
            {
                File.Delete(dest);
                this.errors.Add($"{Path.GetFileName(sourcePath)}: {durationMs / 1000.0:0.0}s exceeds the {MaxDurationMs / 1000}s cap");
                return null;
            }

            if (samples.Length == 0)
            {
                File.Delete(dest);
                this.errors.Add($"{Path.GetFileName(sourcePath)}: decoded to zero samples");
                return null;
            }

            var info = new ClipInfo
            {
                Hash = hash,
                Relative = relative,
                DisplayName = Path.GetFileName(sourcePath),
                DurationMs = durationMs,
                SampleCount = samples.Length,
                SourceSampleRate = srcRate,
                SourceChannels = srcChannels,
                ImportedAt = DateTime.UtcNow,
            };

            lock (this.cache)
            {
                this.cache[hash] = new CachedClip(info, samples);
            }

            this.SaveManifest();
            this.log.Information("ClipLibrary: imported {Name} ({Ms} ms)", info.DisplayName, durationMs);
            return hash;
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ClipLibrary: import failed for {Path}", sourcePath);
            this.errors.Add($"{Path.GetFileName(sourcePath)}: {ex.Message}");
            return null;
        }
    }

    public void Remove(string hash)
    {
        if (!this.cache.TryGetValue(hash, out var clip))
        {
            return;
        }

        try
        {
            var full = Path.Combine(this.clipsDir, clip.Info.Relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception ex)
        {
            this.log.Warning(ex, "ClipLibrary: could not delete {Relative}", clip.Info.Relative);
        }

        lock (this.cache)
        {
            this.cache.Remove(hash);
        }

        this.SaveManifest();
    }

    public void ClearErrors() => this.errors.Clear();

    /// <summary>Decodes to mono 44.1 kHz float, which is what the mixer and the game both use.</summary>
    private static float[] Decode(string path, out int sourceRate, out int sourceChannels)
    {
        using WaveStream reader = Path.GetExtension(path).ToLowerInvariant() == ".ogg"
            ? new VorbisWaveReader(path)
            : new WaveFileReader(path);

        sourceRate = reader.WaveFormat.SampleRate;
        sourceChannels = reader.WaveFormat.Channels;

        ISampleProvider provider = reader.ToSampleProvider();

        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels > 2)
        {
            throw new NotSupportedException($"{provider.WaveFormat.Channels} channels; mono or stereo only");
        }

        if (provider.WaveFormat.SampleRate != SampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        }

        var samples = new List<float>();
        var buffer = new float[8192];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));

            // Guard against a pathological or malformed file eating memory.
            if (samples.Count > SampleRate * (MaxDurationMs / 1000) * 2)
            {
                break;
            }
        }

        return samples.ToArray();
    }

    private ClipManifest ReadManifest()
    {
        try
        {
            if (!File.Exists(this.manifestPath))
            {
                return new ClipManifest();
            }

            var json = File.ReadAllText(this.manifestPath);
            return JsonSerializer.Deserialize<ClipManifest>(json, JsonOptions) ?? new ClipManifest();
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ClipLibrary: clipmeta.json unreadable — starting empty rather than destroying it");
            return new ClipManifest();
        }
    }

    private void SaveManifest()
    {
        try
        {
            var manifest = new ClipManifest { Clips = this.cache.Values.Select(c => c.Info).ToList() };
            var json = JsonSerializer.Serialize(manifest, JsonOptions);

            var tmp = this.manifestPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, this.manifestPath, overwrite: true);
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "ClipLibrary: could not save clipmeta.json");
        }
    }

    public void Dispose()
    {
        lock (this.cache)
        {
            this.cache.Clear();
        }
    }
}
