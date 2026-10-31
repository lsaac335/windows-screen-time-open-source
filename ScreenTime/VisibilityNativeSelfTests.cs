using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Forms = System.Windows.Forms;

namespace ScreenTime;

public static class VisibilityNativeSelfTests
{
    // Isolated test windows in a child process; never manipulate the user's application windows.
    public static void Fixture(string directory)
    {
        var area = Forms.Screen.PrimaryScreen!.WorkingArea;
        using var a = new Forms.Form { Text = "ScreenTime visibility test A", StartPosition = Forms.FormStartPosition.Manual, Bounds = new Rectangle(area.Left + 25, area.Top + 55, 300, 260), TopMost = true };
        using var b = new Forms.Form { Text = "ScreenTime visibility test B", StartPosition = Forms.FormStartPosition.Manual, Bounds = new Rectangle(area.Left + 380, area.Top + 55, 300, 260), TopMost = true };
        a.Show(); b.Show();
        File.WriteAllText(Path.Combine(directory, "handles.json"), JsonSerializer.Serialize(new long[] { a.Handle.ToInt64(), b.Handle.ToInt64() }));
        string last = "";
        var limit = Stopwatch.StartNew();
        using var timer = new Forms.Timer { Interval = 100 };
        timer.Tick += (_, _) =>
        {
            string command = File.Exists(Path.Combine(directory, "command.txt")) ? File.ReadAllText(Path.Combine(directory, "command.txt")) : "";
            if (command == "exit" || limit.Elapsed.TotalSeconds > 90) { a.Close(); b.Close(); Forms.Application.ExitThread(); return; }
            if (command == last) return;
            last = command;
            if (command == "cover") { b.Bounds = a.Bounds; b.BringToFront(); }
            if (command == "hide") b.Hide();
            if (command == "minimize") { b.Show(); b.WindowState = Forms.FormWindowState.Minimized; }
            File.WriteAllText(Path.Combine(directory, "ack.txt"), command);
        };
        timer.Start(); Forms.Application.Run();
    }
    public static string Run(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "handles.json", "ack.txt", "command.txt" }) File.Delete(Path.Combine(directory, name));
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        info.ArgumentList.Add("--visibility-fixture"); info.ArgumentList.Add(directory);
        using var child = Process.Start(info)!;
        int checks = 0;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException("Native visibility test: " + name); checks++; }
        try
        {
            for (int i = 0; i < 40 && !File.Exists(Path.Combine(directory, "handles.json")); i++) Thread.Sleep(100);
            long[] handles = JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(directory, "handles.json")))!;
            using var monitor = new WindowVisibilityMonitor();
            VisibleApp? Resolve(IntPtr h) => h.ToInt64() == handles[0] ? new("a", "A") : h.ToInt64() == handles[1] ? new("b", "B") : null;
            void Read() { Thread.Sleep(500); monitor.Update(false, Resolve); monitor.Update(true, Resolve); }
            void Command(string command)
            {
                File.WriteAllText(Path.Combine(directory, "command.txt"), command);
                for (int i = 0; i < 40; i++)
                { if (File.Exists(Path.Combine(directory, "ack.txt")) && File.ReadAllText(Path.Combine(directory, "ack.txt")) == command) break; Thread.Sleep(100); }
                Read();
            }
            Read();
            Check(monitor.Available, "desktop COM and DWM are available: " + monitor.Status);
            Check(monitor.Apps.Any(a => a.Id == "a") && monitor.Apps.Any(a => a.Id == "b"), "two side-by-side windows both visible");
            monitor.Update(false, Resolve);
            monitor.Update(true, h => handles.Contains(h.ToInt64()) ? new("same-app", "Same app") : null);
            Check(monitor.Apps.Count(a => a.Id == "same-app") == 1, "same-app top-level windows merge");
            Command("cover");
            Check(monitor.Apps.All(a => a.Id != "a") && monitor.Apps.Any(a => a.Id == "b"), "fully obscured window excluded");
            Command("hide");
            Check(monitor.Apps.Any(a => a.Id == "a") && monitor.Apps.All(a => a.Id != "b"), "hidden window excluded and uncovered app restored");
            Command("minimize");
            Check(monitor.Apps.Any(a => a.Id == "a") && monitor.Apps.All(a => a.Id != "b"), "minimized window excluded");
            monitor.Update(false, Resolve);
            Check(!monitor.Available && monitor.Apps.Length == 0, "paused or locked state clears visibility snapshot");
        }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "command.txt"), "exit");
            if (!child.WaitForExit(4000)) child.Kill();
        }
        return "Native visibility tests passed: " + checks + " assertions (real DWM/desktop API, parallel windows, same-app union, full occlusion, hide/minimize, disabled state).";
    }
}
