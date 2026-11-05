using System.Windows.Threading;
using TinyHand.Core;
using Vortice.Multimedia;
using Vortice.XAudio2;

namespace TinyHand;

/// <summary>
/// Plays the tap sound with almost no delay: every sound is loaded up front and played through XAudio2.
/// The engine sleeps after 30 quiet seconds, like the AVAudioEngine in the macOS app.
/// </summary>
internal sealed class TapSound : IDisposable
{
    private sealed record Clip(WaveFormat Format, byte[] Data);

    private static readonly Dictionary<TapSoundChoice, Clip> Clips = [];

    private IXAudio2? engine;
    private IXAudio2MasteringVoice? master;
    private IXAudio2SourceVoice? voice;
    private readonly Dictionary<TapSoundChoice, AudioBuffer> buffers = [];
    private TapSoundChoice choice;
    private readonly DispatcherTimer idle;
    private bool running;

    public bool Enabled { get; set; } = true;

    public TapSound(TapSoundChoice choice)
    {
        this.choice = choice;
        idle = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        idle.Tick += (_, _) => Sleep();
    }

    public void Load(TapSoundChoice value) => choice = value;

    /// <summary>Starts the engine ahead of the first tap so it plays right away.</summary>
    public void Warm() => Start();

    public void Play()
    {
        if (!Enabled || !Start() || voice is null || !buffers.TryGetValue(choice, out var buffer))
        {
            return;
        }
        try
        {
            voice.Stop();
            voice.FlushSourceBuffers();
            voice.SubmitSourceBuffer(buffer);
            voice.Start();
        }
        catch (Exception e)
        {
            Log.Write($"tap sound failed: {e.Message}");
            Teardown();
        }
    }

    private bool Start()
    {
        idle.Stop();
        idle.Start();
        if (running && engine is not null)
        {
            return true;
        }
        try
        {
            if (engine is null)
            {
                LoadClips();
                if (Clips.Count == 0)
                {
                    return false;
                }
                engine = XAudio2.XAudio2Create();
                master = engine.CreateMasteringVoice();
                voice = engine.CreateSourceVoice(Clips.Values.First().Format);
                foreach (var (key, clip) in Clips)
                {
                    buffers[key] = new AudioBuffer(clip.Data, BufferFlags.EndOfStream);
                }
            }
            engine.StartEngine();
            running = true;
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"no audio: {e.Message}");
            Teardown();
            return false;
        }
    }

    /// <summary>Stops the engine and lets go of the audio device, so the next tap picks up the current output.</summary>
    private void Sleep()
    {
        idle.Stop();
        Teardown();
    }

    private void Teardown()
    {
        running = false;
        try
        {
            voice?.DestroyVoice();
            master?.DestroyVoice();
            engine?.StopEngine();
        }
        catch (Exception)
        {
        }
        foreach (var b in buffers.Values)
        {
            b.Dispose();
        }
        buffers.Clear();
        voice = null;
        master = null;
        engine?.Dispose();
        engine = null;
    }

    private static void LoadClips()
    {
        if (Clips.Count > 0)
        {
            return;
        }
        foreach (var c in Enum.GetValues<TapSoundChoice>())
        {
            try
            {
                if (ReadWave(Paths.Sound(c.FileName())) is Clip clip)
                {
                    Clips[c] = clip;
                }
            }
            catch (Exception e)
            {
                Log.Write($"sound {c} unreadable: {e.Message}");
            }
        }
    }

    /// <summary>Reads a plain PCM .wav file.</summary>
    private static Clip? ReadWave(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            return null;
        }
        WaveFormat? format = null;
        byte[]? data = null;
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            int body = pos + 8;
            if (size < 0 || body + size > bytes.Length)
            {
                size = bytes.Length - body;
            }
            if (id == "fmt ")
            {
                int channels = BitConverter.ToInt16(bytes, body + 2);
                int rate = BitConverter.ToInt32(bytes, body + 4);
                int bits = BitConverter.ToInt16(bytes, body + 14);
                format = new WaveFormat(rate, bits, channels);
            }
            else if (id == "data")
            {
                data = bytes.AsSpan(body, size).ToArray();
            }
            pos = body + size + (size & 1);
        }
        return format is not null && data is not null ? new Clip(format, data) : null;
    }

    public void Dispose()
    {
        idle.Stop();
        Teardown();
    }
}
