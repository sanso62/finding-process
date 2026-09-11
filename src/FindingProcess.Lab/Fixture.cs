using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FindingProcess.Lab;

internal static class Fixture
{
    // No recovery-specific launcher/IPC is required by the target. The file is measurement only;
    // all test commands arrive through its ordinary Windows console input queue.
    internal static int Run(string statePath, int? lifetimeSeconds)
    {
        var memory = Marshal.AllocHGlobal(32);
        Identity? identity = null;
        try
        {
            var random = RandomNumberGenerator.GetBytes(32);
            Marshal.Copy(random, 0, memory, random.Length);
            using var current = Process.GetCurrentProcess();
            identity = new Identity(Environment.ProcessId, current.StartTime.ToUniversalTime().Ticks,
                Convert.ToHexString(random), memory.ToInt64());
            var input = Native.GetStdHandle(-10);
            var output = Native.GetStdHandle(-11);
            Native.Check(Native.SetConsoleMode(input, 0x8), "SetConsoleMode(fixture)");
            var line = new StringBuilder();
            var events = new Native.InputRecord[32];
            var watch = Stopwatch.StartNew();
            long tick = 0, value = 0;
            var lastInput = "";
            var nextBeat = TimeSpan.Zero;
            while (lifetimeSeconds is null || watch.Elapsed.TotalSeconds < lifetimeSeconds)
            {
                Native.Check(Native.GetNumberOfConsoleInputEvents(input, out var available), "Count input");
                if (available > 0)
                {
                    Native.Check(Native.ReadConsoleInputW(input, events, (uint)events.Length, out var read), "Read input");
                    foreach (var item in events.Take((int)read))
                    {
                        if (item.EventType != 1 || item.KeyDown == 0 || item.UnicodeChar == 0) continue;
                        var character = (char)item.UnicodeChar;
                        if (character == '\r')
                        {
                            lastInput = line.ToString();
                            line.Clear();
                            if (lastInput == "quit") return 0;
                            var parts = lastInput.Split(' ');
                            if (parts.Length is 2 or 3 && parts[0] == "add" && long.TryParse(parts[1], out var amount))
                                value += amount;
                            nextBeat = TimeSpan.Zero;
                        }
                        else if (character == '\b' && line.Length > 0) line.Length--;
                        else if (!char.IsControl(character) && line.Length < 256) line.Append(character);
                    }
                }
                if (watch.Elapsed >= nextBeat)
                {
                    Native.Check(Native.GetConsoleScreenBufferInfo(output, out var info), "Fixture dimensions");
                    tick++;
                    var state = new FixtureState(identity, tick, value, lastInput,
                        (short)(info.Window.Right - info.Window.Left + 1),
                        (short)(info.Window.Bottom - info.Window.Top + 1), null, Native.GetCurrentThreadId());
                    var text = lifetimeSeconds is null
                        ? $"[{DateTime.Now:HH:mm:ss}] pid={identity.Pid} tick={tick} value={value} size={state.Columns}x{state.Rows}\r\n" +
                          $"  last: {lastInput} | typing: {line} | commands: add 5 / quit + Enter\r\n"
                        : $"FP tick={tick} pid={identity.Pid} value={value}\r\n" +
                        $"input={lastInput}\r\nnonce={identity.Nonce}\r\nsize={state.Columns}x{state.Rows}\r\n";
                    Native.Check(Native.WriteConsoleW(output, text, (uint)text.Length, out var written, 0), "Fixture output");
                    if (written != text.Length) throw new IOException("Incomplete console write.");
                    JsonFile.Write(statePath, state);
                    nextBeat = watch.Elapsed + TimeSpan.FromMilliseconds(lifetimeSeconds is null ? 1000 : 250);
                }
                Thread.Sleep(10);
            }
            return 0;
        }
        catch (Exception error)
        {
            if (identity is not null)
                JsonFile.Write(statePath, new FixtureState(identity, 0, 0, "", 0, 0, error.ToString()));
            throw;
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
