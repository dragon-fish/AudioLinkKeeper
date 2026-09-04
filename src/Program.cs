using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;

// AudioLinkKeeper
//
// Problem
// -------
// On Windows, pausing playback in any app that registers System Media Transport
// Controls (browsers, music players, ...) propagates a "paused" state to a
// connected Bluetooth speaker over AVRCP. Some speakers respond by closing their
// audio channel. The A2DP driver never learns about this and keeps streaming at
// full rate into a channel nobody is listening to, so the ENTIRE system goes
// silent -- games, system sounds, everything -- while Windows reports every
// layer as perfectly healthy.
//
// Only a fresh SMTC "playing" state brings the speaker back. No amount of raw
// WASAPI audio does, regardless of volume.
//
// Fix
// ---
// Keep one permanently-playing, zero-volume SMTC session alive. As long as some
// session is playing, no pause is ever propagated and the channel stays open.
//
// Usage
//   AudioLinkKeeper.exe            run the keeper (default)
//   AudioLinkKeeper.exe once       play one silent SMTC burst and exit (manual rescue)

const string SelfTitle = "AudioLinkKeeper";
const string SelfArtist = "keeping the bluetooth link alive";
const long MaxLogBytes = 512 * 1024;

// Everything lives next to the executable; regenerated if deleted.
string SilentWav = Path.Combine(AppContext.BaseDirectory, "silent.wav");
string LogPath = Path.Combine(AppContext.BaseDirectory, "keeper.log");
EnsureSilentWav(SilentWav);

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "hold";

if (mode == "once")
{
    var p = BuildPlayer();
    Log("one-shot: playing silent SMTC burst");
    await Task.Delay(2000);
    p.Dispose();
    return 0;
}

// ---------------------------------------------------------------- keeper loop
MediaPlayer player = BuildPlayer();
Log($"keeper started (permanently playing, volume 0) - {SilentWav}");
await SelfCheck();

int strikes = 0;
MediaPlaybackState lastLogged = MediaPlaybackState.Playing;

while (true)
{
    await Task.Delay(1000);
    try
    {
        var st = player.PlaybackSession.PlaybackState;
        if (st == MediaPlaybackState.Playing) { strikes = 0; lastLogged = st; continue; }

        // Log only on transitions, otherwise a stuck player floods the log.
        if (st != lastLogged) { Log($"session not playing ({st}), recovering"); lastLogged = st; }

        // PlaybackState.None means the MediaPlayer object itself is dead --
        // calling Play() on it does nothing at all, silently. Rebuild instead.
        if (st == MediaPlaybackState.None || ++strikes >= 3)
        {
            Log($"state={st} strikes={strikes} -> rebuilding player");
            try { player.Dispose(); } catch { }
            player = BuildPlayer();
            strikes = 0;
            lastLogged = MediaPlaybackState.Playing;
            await Task.Delay(1500);
            await SelfCheck();
        }
        else
        {
            player.Play();   // merely paused by the user, Play() is enough
        }
    }
    catch (Exception ex)
    {
        Log($"watchdog error: {ex.Message} -> rebuilding player");
        try { player.Dispose(); } catch { }
        player = BuildPlayer();
        strikes = 0;
        await Task.Delay(1500);
    }
}

// -------------------------------------------------------------------- helpers

MediaPlayer BuildPlayer()
{
    EnsureSilentWav(SilentWav);   // self-heal if the wav was deleted at runtime

    var p = new MediaPlayer
    {
        AudioCategory = MediaPlayerAudioCategory.Media,
        Volume = 0.0,
        IsLoopingEnabled = true,
    };

    // Display metadata must go through MediaPlaybackItem, otherwise the control
    // centre shows the raw file path as the track title.
    var item = new MediaPlaybackItem(MediaSource.CreateFromUri(
        new Uri(new Uri("file:///"), SilentWav.Replace('\\', '/'))));
    var props = item.GetDisplayProperties();
    props.Type = Windows.Media.MediaPlaybackType.Music;
    props.MusicProperties.Title = SelfTitle;
    props.MusicProperties.Artist = SelfArtist;
    item.ApplyDisplayProperties(props);
    p.Source = item;

    // Do NOT set every command to Never: Windows then decides the session has no
    // usable controls, hides it from SMTC entirely, and the whole thing stops
    // working. Disabling the meaningless next/previous buttons is enough.
    var cm = p.CommandManager;
    cm.IsEnabled = true;
    cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Never;
    cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Never;

    p.MediaFailed += (s, e) =>
        Log($"MediaFailed: {e.Error} / {e.ErrorMessage} / hr=0x{e.ExtendedErrorCode?.HResult:X8}");
    p.MediaEnded += (s, e) =>
    {
        Log("media ended unexpectedly (looping should prevent this), restarting");
        try { s.Play(); } catch { }
    };

    p.Play();
    return p;
}

async Task SelfCheck()
{
    try
    {
        var mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var mine = mgr.GetSessions().FirstOrDefault(s =>
            (s.SourceAppUserModelId ?? "").Contains("AudioLinkKeeper", StringComparison.OrdinalIgnoreCase));
        if (mine == null) Log("self-check FAILED: session not present in the system SMTC list");
        else Log($"self-check ok: session listed, status={mine.GetPlaybackInfo().PlaybackStatus}");
    }
    catch (Exception ex) { Log($"self-check error: {ex.Message}"); }
}

void Log(string msg)
{
    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}";
    Console.WriteLine(line);
    try
    {
        // Single-file rotation: keep at most the current log plus one previous.
        var fi = new FileInfo(LogPath);
        if (fi.Exists && fi.Length > MaxLogBytes)
        {
            string old = LogPath + ".1";
            try { if (File.Exists(old)) File.Delete(old); } catch { }
            try { File.Move(LogPath, old); } catch { try { File.Delete(LogPath); } catch { } }
        }
        File.AppendAllText(LogPath, line + Environment.NewLine);
    }
    catch { }
}

// Generate a pure-silence WAV (44.1 kHz / 16-bit / stereo / 2 s).
// Generated on demand so the tool has no external asset dependency.
static void EnsureSilentWav(string path)
{
    try
    {
        if (File.Exists(path) && new FileInfo(path).Length > 44) return;

        const int sampleRate = 44100, seconds = 2, channels = 2, bits = 16;
        int dataSize = sampleRate * seconds * channels * bits / 8;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + dataSize);
        bw.Write(new[] { 'W', 'A', 'V', 'E' });
        bw.Write(new[] { 'f', 'm', 't', ' ' });
        bw.Write(16);                                 // fmt chunk size
        bw.Write((short)1);                           // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bits / 8);   // byte rate
        bw.Write((short)(channels * bits / 8));       // block align
        bw.Write((short)bits);
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]);                 // all zeros = silence
    }
    catch { }
}
