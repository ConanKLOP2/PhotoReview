using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using PhotoReview.App;
using PhotoReview.Integration.Tests.Fakes;
using PhotoReview.Integration.Tests.Infrastructure;
using Xunit;

namespace PhotoReview.Integration.Tests;

/// <summary>
/// T14d — INV-7 and INV-9 locked on the real <see cref="MainWindow"/> through the T14a seam.
/// <list type="bullet">
/// <item>INV-7: an Explorer snapshot that lands after the user has already navigated must not
/// reorder the catalog.</item>
/// <item>INV-9a: opening a <b>file</b> waits for the snapshot before the first frame.</item>
/// <item>INV-9b: opening a <b>folder</b> presents a natural-order fallback first, then applies the
/// Explorer order and presents the first image of that order.</item>
/// </list>
/// Runs in the "GlobalState" collection because hosting MainWindow mutates the process-wide
/// PHOTOREVIEW_DATA_ROOT; <see cref="DataRootFixture"/> owns that variable and its cleanup.
/// </summary>
[Collection("GlobalState")]
[Trait("Category", "Slow")]
public sealed class MainWindowExplorerOrderTests
{
    /// <summary>Upper bound for a wait that is expected to succeed quickly.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a "this must never happen" wait keeps pumping the dispatcher before it counts as
    /// proof. Everything ruled out this way is at most a couple of dispatcher continuations past a
    /// signal the test already observed, so a second is far more than it would need.
    /// </summary>
    private static readonly TimeSpan NeverWindow = TimeSpan.FromSeconds(1);

    private static readonly string[] Names = ["a.png", "b.png", "c.png", "d.png"];

    /// <summary>INV-9b: the fallback frame precedes the snapshot, the reordered frame follows it.</summary>
    private static readonly bool[] FallbackBeforeSnapshotThenAfter = [false, true];

    /// <summary>The suffix MainWindow appends to FolderText once Explorer order is applied.</summary>
    private const string ExplorerApplied = "· Explorer";

    // ------------------------------------------------------------------ INV-7

    [Fact(DisplayName = "A late Explorer snapshot is ignored once the user has navigated")]
    public async Task LateExplorerSnapshotIsIgnoredAfterNavigation()
    {
        using var root = new TempRoot("inv7");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var natural = CreateImages(folder);
        var reversed = Reverse(natural);
        // T14d: "fake trả snapshot đảo thứ tự sau 500 ms".
        var fake = new FakeExplorerOrderProvider(reversed, TimeSpan.FromMilliseconds(500));
        var presented = new List<string>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var opened = Open(folder, fake, presented);
                window = opened;
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 1, Settle), Diagnose(opened, presented));

                // The user presses Next while the snapshot is still in flight: that bumps
                // _catalogInteractionGeneration, which is what INV-7 keys off.
                Assert.False(fake.HasReturned, "The snapshot arrived before the test could navigate.");
                PressNext(opened);
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 2, Settle), Diagnose(opened, presented));

                fake.Release();
                Assert.True(await StaTestHost.WaitForAsync(() => fake.HasReturned, Settle), "The fake snapshot never completed.");
                Assert.False(
                    await StaTestHost.WaitForAsync(() => opened.FolderText.Text.Contains(ExplorerApplied, StringComparison.Ordinal), NeverWindow),
                    $"The late snapshot reordered a catalog the user had already interacted with. {Diagnose(opened, presented)}");

                // Behavioural proof that the catalog order is untouched: after b.png the next image
                // is still c.png. Under the reversed order (d, c, b, a) it would have been a.png.
                PressNext(opened);
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 3, Settle), Diagnose(opened, presented));
            });
        }
        finally
        {
            fake.Release();
            await CloseAsync(window);
        }

        Assert.Equal(new[] { natural[0], natural[1], natural[2] }, presented, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(1, fake.CallCount);
    }

    // ----------------------------------------------------------------- INV-9a

    [Fact(DisplayName = "Opening a file waits for the Explorer snapshot before the first frame")]
    public async Task OpeningAFileWaitsForTheSnapshot()
    {
        using var root = new TempRoot("inv9a");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var natural = CreateImages(folder);
        var requested = natural[2];
        var fake = new FakeExplorerOrderProvider(Reverse(natural));
        var presented = new List<string>();
        var snapshotHadReturned = new List<bool>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var opened = new MainWindow(requested, new MainWindowTestHooks
                {
                    Explorer = fake,
                    OnPresented = path => { snapshotHadReturned.Add(fake.HasReturned); presented.Add(path); },
                });
                window = opened;

                // FolderText receives the file count as the last statement of catalog setup; on the
                // file-open path the very next yield is `await explorerTask`. Seeing the count here
                // therefore means the load is parked on the snapshot, with nothing presented yet —
                // a state the test can observe deterministically instead of guessing with a sleep.
                Assert.True(
                    await StaTestHost.WaitForAsync(() => opened.FolderText.Text.Contains($"({natural.Length} ảnh)", StringComparison.Ordinal), Settle),
                    Diagnose(opened, presented));
                Assert.False(fake.HasReturned);
                Assert.Empty(presented);
                Assert.False(
                    await StaTestHost.WaitForAsync(() => presented.Count > 0, NeverWindow),
                    $"Opening a file presented a frame before the Explorer snapshot arrived. {Diagnose(opened, presented)}");

                fake.Release();
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 1, Settle), Diagnose(opened, presented));
                // The snapshot really was usable: it reindexed the catalog. Without this the test
                // could pass on a snapshot that MainWindow rejected outright.
                Assert.True(
                    await StaTestHost.WaitForAsync(() => opened.FolderText.Text.Contains(ExplorerApplied, StringComparison.Ordinal), Settle),
                    Diagnose(opened, presented));
                Assert.False(
                    await StaTestHost.WaitForAsync(() => presented.Count > 1, NeverWindow),
                    $"The requested file was presented more than once. {Diagnose(opened, presented)}");
            });
        }
        finally
        {
            fake.Release();
            await CloseAsync(window);
        }

        Assert.Equal(requested, Assert.Single(presented), ignoreCase: true);
        Assert.True(Assert.Single(snapshotHadReturned), "The first frame was presented before the snapshot returned.");
        Assert.Equal(1, fake.CallCount);
    }

    // ----------------------------------------------------------------- INV-9b

    [Fact(DisplayName = "Opening a folder presents a fallback first, then the Explorer-ordered first image")]
    public async Task OpeningAFolderPresentsFallbackThenExplorerOrder()
    {
        using var root = new TempRoot("inv9b");
        using var dataRoot = new DataRootFixture();
        var folder = root.Dir("images");
        var natural = CreateImages(folder);
        var reversed = Reverse(natural);
        var fake = new FakeExplorerOrderProvider(reversed);
        var presented = new List<string>();
        var snapshotHadReturned = new List<bool>();
        MainWindow? window = null;
        try
        {
            await StaTestHost.RunAsync(async () =>
            {
                var opened = new MainWindow(folder, new MainWindowTestHooks
                {
                    Explorer = fake,
                    OnPresented = path => { snapshotHadReturned.Add(fake.HasReturned); presented.Add(path); },
                });
                window = opened;

                // The gate is still shut, so reaching one presentation at all is the proof that the
                // folder path presents a natural-order fallback without waiting for the snapshot.
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 1, Settle), Diagnose(opened, presented));
                Assert.False(fake.HasReturned);
                Assert.DoesNotContain(ExplorerApplied, opened.FolderText.Text, StringComparison.Ordinal);

                fake.Release();
                Assert.True(await StaTestHost.WaitForAsync(() => presented.Count >= 2, Settle), Diagnose(opened, presented));
                Assert.True(
                    await StaTestHost.WaitForAsync(() => opened.FolderText.Text.Contains(ExplorerApplied, StringComparison.Ordinal), Settle),
                    Diagnose(opened, presented));
                Assert.False(
                    await StaTestHost.WaitForAsync(() => presented.Count > 2, NeverWindow),
                    $"Applying the Explorer order presented more than one extra frame. {Diagnose(opened, presented)}");
            });
        }
        finally
        {
            fake.Release();
            await CloseAsync(window);
        }

        // Fallback = first image of the natural sort; then the first image of the Explorer order.
        Assert.Equal(new[] { natural[0], reversed[0] }, presented, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(FallbackBeforeSnapshotThenAfter, snapshotHadReturned);
        Assert.Equal(1, fake.CallCount);
    }

    // --------------------------------------------------------------- helpers

    private static MainWindow Open(string folder, FakeExplorerOrderProvider fake, List<string> presented)
        => new(folder, new MainWindowTestHooks { Explorer = fake, OnPresented = presented.Add });

    private static async Task CloseAsync(MainWindow? window)
    {
        if (window is null) return;
        await StaTestHost.RunAsync(() =>
        {
            // Releases the preload scheduler, thumbnail cache and the provider this window owns.
            // A never-shown window can refuse Close(); leaking it for the rest of the run is
            // acceptable, failing the test over cleanup is not.
            try { window.Close(); } catch (InvalidOperationException) { }
            return Task.CompletedTask;
        });
    }

    private static string Diagnose(MainWindow window, List<string> presented)
        => $"presented=[{string.Join(", ", presented.Select(Path.GetFileName))}] "
            + $"folder='{window.FolderText.Text}' status='{window.StatusText.Text}'";

    private static string[] Reverse(string[] paths)
    {
        var copy = (string[])paths.Clone();
        Array.Reverse(copy);
        return copy;
    }

    /// <summary>
    /// Presses the configured "Next" key on the window's real PreviewKeyDown route — the same entry
    /// point a physical keystroke takes (MainWindow.xaml wires PreviewKeyDown="Window_KeyDown").
    /// Nothing is injected at OS level: SendInput/SendKeys/SetForegroundWindow are forbidden in this
    /// repo (rule 4 of PERF-DIAGNOSIS-TASKS.md) after a simulated keystroke escaped to another window.
    /// </summary>
    private static void PressNext(MainWindow window)
    {
        // Read the binding from the same settings source MainWindow uses, so a machine whose real
        // config.json remaps Next does not silently turn this into some other command.
        var configured = AppSettings.Load().Shortcuts.Next;
        Assert.True(Enum.TryParse<Key>(configured, ignoreCase: true, out var key),
            $"The configured Next shortcut '{configured}' is not a WPF Key.");
        var source = PresentationSource.FromVisual(window)
            ?? (PresentationSource?)HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle())
            ?? throw new InvalidOperationException("The hosted window has no PresentationSource to raise input against.");
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
        // Window_KeyDown sets Handled synchronously, before it awaits ShowImageAsync, so this also
        // proves the keystroke reached the Next branch rather than falling through the handler.
        Assert.True(args.Handled, $"MainWindow did not handle the Next key ({key}).");
    }

    private static string[] CreateImages(string folder)
    {
        var created = new string[Names.Length];
        for (var index = 0; index < Names.Length; index++)
        {
            created[index] = Path.Combine(folder, Names[index]);
            File.WriteAllBytes(created[index], Png(16, 16));
        }
        return created;
    }

    /// <summary>A minimal, valid 8-bit RGBA PNG of the requested size, written by hand.</summary>
    private static byte[] Png(int width, int height)
    {
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 4);
            raw[row] = 0; // filter type: none
            for (var x = 0; x < width; x++)
            {
                var p = row + 1 + x * 4;
                raw[p] = 0x40; raw[p + 1] = 0x80; raw[p + 2] = 0xC0; raw[p + 3] = 0xFF;
            }
        }

        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // color type: RGBA
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteBigEndian(byte[] target, int offset, int value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        stream.Write(length);
        var payload = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) payload[i] = (byte)type[i];
        data.CopyTo(payload, 4);
        stream.Write(payload);
        var crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(payload)));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static uint Crc32(byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
