using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ScreenTime;

/// <summary>Opt-in desktop integration fixture. A local, muted waveform exercises real system events.</summary>
public static class MediaSessionSelfTests
{
    public static string Run()
    {
        Exception? error = null; string result = "";
        var worker = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                string path = Path.Combine(Path.GetTempPath(), "ScreenTime-media-test-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    using (var output = new BinaryWriter(File.Create(path)))
                    {
                        int bytes = 16000 * 2 * 10;
                        output.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); output.Write(36 + bytes);
                        output.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); output.Write(16);
                        output.Write((short)1); output.Write((short)1); output.Write(16000); output.Write(32000);
                        output.Write((short)2); output.Write((short)16);
                        output.Write(System.Text.Encoding.ASCII.GetBytes("data")); output.Write(bytes); output.Write(new byte[bytes]);
                    }
                    var states = new List<HashSet<string>>();
                    using var monitor = new MediaSessionMonitor(s => states.Add(s));
                    monitor.SetEnabled(true);
                    using var player = new MediaPlayer { Volume = 0, IsLoopingEnabled = true };
                    player.Source = MediaSource.CreateFromUri(new Uri(path));
                    var controls = player.SystemMediaTransportControls;
                    controls.IsEnabled = true; controls.IsPlayEnabled = true; controls.IsPauseEnabled = true;
                    controls.DisplayUpdater.Type = MediaPlaybackType.Music;
                    controls.DisplayUpdater.MusicProperties.Title = "ScreenTime local validation";
                    controls.DisplayUpdater.Update();
                    player.Play();
                    string? source = null;
                    for (int i = 0; i < 60 && source == null; i++)
                    {
                        await Task.Delay(100);
                        source = states.SelectMany(s => s).FirstOrDefault(s => s.Contains("ScreenTime", StringComparison.OrdinalIgnoreCase));
                    }
                    if (source == null) throw new InvalidOperationException("Local playback session was not observed; " + monitor.Status);
                    player.Pause();
                    bool paused = false;
                    for (int i = 0; i < 60 && !paused; i++)
                    {
                        await Task.Delay(100);
                        paused = states.Count > 0 && !states.Last().Contains(source);
                    }
                    if (!paused) throw new InvalidOperationException("Pause did not remove the session's playing state.");
                    monitor.SetEnabled(false);
                    if (monitor.Status != "off" || states.Last().Count != 0) throw new InvalidOperationException("Disabled monitor retained playback evidence.");
                    result = "Media integration passed: real muted local playback and pause events observed; disable cleared evidence; no media polling, titles or covers read by the monitor.";
                }
                catch (Exception ex) { error = ex; }
                finally { File.Delete(path); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            }));
            Dispatcher.Run();
        });
        worker.IsBackground = true;
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(25))) throw new TimeoutException("Media integration fixture did not finish.");
        if (error != null) throw new InvalidOperationException("Media integration fixture failed.", error);
        return result;
    }
}
