using PhotoReview.Core.Catalog;
using PhotoReview.Core.Model;

namespace PhotoReview.Core.Tests.Catalog;

/// <summary>FEAT-RENAME (PR2): ReviewCatalog.RenameEntry / RenameEntries re-key entries in place.</summary>
public sealed class ReviewCatalogRenameTests
{
    private static string P(string name) => $@"C:\photos\{name}";

    private static ReviewCatalog Build(params string[] names)
    {
        var catalog = new ReviewCatalog();
        var entries = names.Select((n, i) => new CatalogEntry(P(n))
        {
            Length = 100 + i,
            LastWriteUtc = new DateTime(2026, 1, 1, 0, 0, i % 60, DateTimeKind.Utc),
            Width = 10 + i,
            Height = 20 + i,
        });
        catalog.Reset(entries, RawPairMode.Separate);
        return catalog;
    }

    private static ReviewCatalog BuildPaired(RawPairMode mode, params string[] sidecars)
    {
        var catalog = new ReviewCatalog();
        catalog.Reset(
            [new CatalogEntry(P("a.jpg")), new CatalogEntry(P("a.cr2")), new CatalogEntry(P("b.jpg")), new CatalogEntry(P("c.jpg"))],
            mode,
            sidecars.Select(P));
        return catalog;
    }

    private sealed record Snapshot(string[] Paths, int Version, int Current, CatalogEntry[] Entries);

    private static Snapshot Capture(ReviewCatalog c) =>
        new([.. c.Paths], c.StructuralVersion, c.CurrentIndex, c.EntriesSnapshot());

    private static void AssertUnchanged(Snapshot before, ReviewCatalog c)
    {
        Assert.Equal(before.Paths, c.Paths);
        Assert.Equal(before.Version, c.StructuralVersion);
        Assert.Equal(before.Current, c.CurrentIndex);
        var now = c.EntriesSnapshot();
        Assert.Equal(before.Entries.Length, now.Length);
        for (var i = 0; i < now.Length; i++)
        {
            Assert.Equal(before.Entries[i], now[i]);
            Assert.Equal(before.Entries[i].Path, now[i].Path, StringComparer.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void RenameEntry_FirstMiddleLast_KeepsIndexCurrentAndMetadata(int index)
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg");
        catalog.SetCurrent(3);
        var oldPath = catalog.PathAt(index);
        var before = catalog.Find(oldPath)!;
        var version = catalog.StructuralVersion;

        Assert.True(catalog.RenameEntry(oldPath, P("renamed.jpg")));

        Assert.Equal(index, catalog.IndexOf(P("renamed.jpg")));
        Assert.Equal(-1, catalog.IndexOf(oldPath));
        Assert.Equal(P("renamed.jpg"), catalog.PathAt(index));
        Assert.Contains(P("renamed.jpg"), catalog.Paths);
        Assert.DoesNotContain(oldPath, catalog.Paths);
        Assert.Equal(5, catalog.Count);
        Assert.Equal(3, catalog.CurrentIndex);
        Assert.Equal(version + 1, catalog.StructuralVersion);
        var after = catalog.Find(P("renamed.jpg"))!;
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before.LastWriteUtc, after.LastWriteUtc);
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
    }

    [Fact]
    public void RenameEntry_CurrentEntry_StaysCurrentUnderNewName()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        catalog.SetCurrent(1);

        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));

        Assert.Equal(1, catalog.CurrentIndex);
        Assert.Equal(P("z.jpg"), catalog.Current!.Path);
    }

    [Fact]
    public void RenameEntry_NonCurrentEntry_DoesNotMoveCurrent()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        catalog.SetCurrent(2);

        Assert.True(catalog.RenameEntry(P("a.jpg"), P("z.jpg")));

        Assert.Equal(2, catalog.CurrentIndex);
        Assert.Equal(P("c.jpg"), catalog.Current!.Path);
    }

    [Fact]
    public void RenameEntry_NameSortsElsewhere_KeepsPositionUntilReset()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");

        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));
        Assert.Equal([P("a.jpg"), P("z.jpg"), P("c.jpg")], catalog.Paths);

        // A fresh listing sorted by name puts it last again.
        var sorted = catalog.Paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        catalog.Reset(sorted);
        Assert.Equal([P("a.jpg"), P("c.jpg"), P("z.jpg")], catalog.Paths);
        Assert.Equal(2, catalog.IndexOf(P("z.jpg")));
    }

    [Fact]
    public void RenameEntry_CaseOnly_IsValidAndRespellsPath()
    {
        var catalog = Build("a.jpg", "b.jpg");
        var version = catalog.StructuralVersion;

        Assert.True(catalog.RenameEntry(P("a.jpg"), P("A.JPG")));

        Assert.Equal(P("A.JPG"), catalog.PathAt(0));
        Assert.Equal(P("A.JPG"), catalog.Paths[0]);
        Assert.Equal(0, catalog.IndexOf(P("a.jpg"))); // lookups are case-insensitive
        Assert.Equal(version + 1, catalog.StructuralVersion);
    }

    [Fact]
    public void RenameEntry_UnknownPath_ReturnsFalseAndLeavesCatalogUntouched()
    {
        var catalog = Build("a.jpg", "b.jpg");
        var before = Capture(catalog);

        Assert.False(catalog.RenameEntry(P("nope.jpg"), P("z.jpg")));

        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntry_NewPathListedByOtherEntry_ReturnsFalseAndLeavesCatalogUntouched()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        catalog.SetCurrent(1);
        var before = Capture(catalog);

        Assert.False(catalog.RenameEntry(P("a.jpg"), P("C.JPG")));

        AssertUnchanged(before, catalog);
        Assert.Equal(0, catalog.IndexOf(P("a.jpg")));
    }

    [Fact]
    public void RenameEntries_TwoEntriesToSamePath_ReturnsFalseAndLeavesCatalogUntouched()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        var before = Capture(catalog);

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("z.jpg"),
            [P("b.jpg")] = P("Z.jpg"),
        });

        Assert.False(ok);
        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntries_SwapTwoNames_IsValidBecauseBothAreBeingRenamed()
    {
        var catalog = Build("a.jpg", "b.jpg");

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("b.jpg"),
            [P("b.jpg")] = P("a.jpg"),
        });

        Assert.True(ok);
        Assert.Equal([P("b.jpg"), P("a.jpg")], catalog.Paths);
        Assert.Equal(100, catalog.EntriesSnapshot()[0].Length); // metadata stayed with the position
    }

    [Theory]
    [InlineData("", "z.jpg")]
    [InlineData("a.jpg", "")]
    [InlineData(" ", " ")]
    public void RenameEntries_BlankPath_ReturnsFalseAndLeavesCatalogUntouched(string from, string to)
    {
        var catalog = Build("a.jpg", "b.jpg");
        var before = Capture(catalog);
        var map = new Dictionary<string, string>
        {
            [string.IsNullOrWhiteSpace(from) ? from : P(from)] = string.IsNullOrWhiteSpace(to) ? to : P(to),
        };

        Assert.False(catalog.RenameEntries(map));

        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntries_EmptyMap_ReturnsFalse()
    {
        var catalog = Build("a.jpg");
        var before = Capture(catalog);

        Assert.False(catalog.RenameEntries(new Dictionary<string, string>()));

        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntries_MultipleStandaloneEntries_BumpsStructuralVersionOnce()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        var version = catalog.StructuralVersion;

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("x.jpg"),
            [P("c.jpg")] = P("y.jpg"),
        });

        Assert.True(ok);
        Assert.Equal([P("x.jpg"), P("b.jpg"), P("y.jpg")], catalog.Paths);
        Assert.Equal(version + 1, catalog.StructuralVersion);
    }

    [Fact]
    public void RenameEntries_KeyNoEntryLists_IsIgnoredWhileOthersApply()
    {
        var catalog = Build("a.jpg", "b.jpg");

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("x.jpg"),
            [P("ghost.xmp")] = P("x.xmp"),
        });

        Assert.True(ok);
        Assert.Equal([P("x.jpg"), P("b.jpg")], catalog.Paths);
    }

    [Fact]
    public void RenameEntries_PairedGroup_RespellsEveryMemberAndRepresentative()
    {
        var catalog = BuildPaired(RawPairMode.PreferJpeg, "a.xmp");
        Assert.Equal(3, catalog.Count);
        var version = catalog.StructuralVersion;

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("n.jpg"),
            [P("a.cr2")] = P("n.cr2"),
            [P("a.xmp")] = P("n.xmp"),
        });

        Assert.True(ok);
        Assert.Equal(version + 1, catalog.StructuralVersion);
        Assert.Equal(3, catalog.Count);
        var entry = catalog.EntriesSnapshot()[0];
        Assert.Equal(P("n.jpg"), entry.Path);
        var group = Assert.IsType<CaptureGroup>(entry.CaptureGroup);
        Assert.Equal(P("n.jpg"), group.JpegPath);
        Assert.Equal(P("n.cr2"), group.RawPath);
        Assert.Equal(P("n.xmp"), group.XmpPath);
        Assert.Equal(0, catalog.IndexOf(P("n.cr2")));
        Assert.Equal(0, catalog.IndexOf(P("n.jpg")));
        Assert.Equal(-1, catalog.IndexOf(P("a.jpg")));
        Assert.Equal(-1, catalog.IndexOf(P("a.cr2")));
        Assert.Same(catalog.Find(P("n.jpg")), catalog.Find(P("n.cr2")));
    }

    [Fact]
    public void RenameEntries_PairedGroupPreferRaw_RepresentativeIsRawPath()
    {
        var catalog = BuildPaired(RawPairMode.PreferRaw, "a.xmp");
        Assert.Equal(P("a.cr2"), catalog.PathAt(0));

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("n.jpg"),
            [P("a.cr2")] = P("n.cr2"),
            [P("a.xmp")] = P("n.xmp"),
        });

        Assert.True(ok);
        Assert.Equal(P("n.cr2"), catalog.PathAt(0));
        var group = catalog.EntriesSnapshot()[0].CaptureGroup!;
        Assert.Equal(P("n.jpg"), group.JpegPath);
        Assert.Equal(P("n.cr2"), group.RawPath);
        Assert.Equal(0, catalog.IndexOf(P("n.jpg")));
    }

    [Fact]
    public void RenameEntries_GroupWithSubsetOfKeys_RespellsOnlyMappedMembers()
    {
        var catalog = BuildPaired(RawPairMode.PreferJpeg, "a.xmp");

        var ok = catalog.RenameEntries(new Dictionary<string, string> { [P("a.cr2")] = P("n.cr2") });

        Assert.True(ok);
        var entry = catalog.EntriesSnapshot()[0];
        Assert.Equal(P("a.jpg"), entry.Path);
        Assert.Equal(P("a.jpg"), entry.CaptureGroup!.JpegPath);
        Assert.Equal(P("n.cr2"), entry.CaptureGroup.RawPath);
        Assert.Equal(P("a.xmp"), entry.CaptureGroup.XmpPath);
        Assert.Equal(0, catalog.IndexOf(P("n.cr2")));
        Assert.Equal(-1, catalog.IndexOf(P("a.cr2")));
    }

    [Fact]
    public void RenameEntries_XmpKeyOnly_IsNotIndexedSoNothingChanges()
    {
        // The sidecar is not in the path index (only image members are), so a map that lists nothing but the .xmp
        // matches no entry: it is ignored like any unknown key. A rename always carries the image members too.
        var catalog = BuildPaired(RawPairMode.PreferJpeg, "a.xmp");
        var before = Capture(catalog);

        Assert.False(catalog.RenameEntries(new Dictionary<string, string> { [P("a.xmp")] = P("n.xmp") }));

        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntries_XmpWithRepresentative_RespellsSidecarOfGroup()
    {
        var catalog = BuildPaired(RawPairMode.PreferJpeg, "a.xmp");
        var version = catalog.StructuralVersion;

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("a.jpg"),
            [P("a.xmp")] = P("n.xmp"),
        });

        Assert.True(ok);
        var entry = catalog.EntriesSnapshot()[0];
        Assert.Equal(P("a.jpg"), entry.Path);
        Assert.Equal(P("n.xmp"), entry.CaptureGroup!.XmpPath);
        Assert.Equal(version + 1, catalog.StructuralVersion);
    }

    [Fact]
    public void RenameEntries_GroupMemberTakenByOtherEntry_ReturnsFalseAndLeavesCatalogUntouched()
    {
        var catalog = BuildPaired(RawPairMode.PreferJpeg, "a.xmp");
        var before = Capture(catalog);

        var ok = catalog.RenameEntries(new Dictionary<string, string>
        {
            [P("a.jpg")] = P("n.jpg"),
            [P("a.cr2")] = P("b.jpg"), // listed by the standalone entry b
        });

        Assert.False(ok);
        AssertUnchanged(before, catalog);
        Assert.Equal(P("a.cr2"), catalog.EntriesSnapshot()[0].CaptureGroup!.RawPath);
    }

    [Fact]
    public void RenameEntries_XmpOfStandaloneEntry_ReturnsFalse()
    {
        var catalog = Build("a.jpg");
        var before = Capture(catalog);

        Assert.False(catalog.RenameEntries(new Dictionary<string, string> { [P("a.xmp")] = P("n.xmp") }));

        AssertUnchanged(before, catalog);
    }

    [Fact]
    public void RenameEntry_AfterRemoveBurst_UsesLiveIndex()
    {
        var names = Enumerable.Range(0, 40).Select(i => $"img{i:D3}.jpg").ToArray();
        var catalog = Build(names);
        _ = catalog.IndexOf(P(names[0]));
        for (var i = 0; i < 10; i++) catalog.Remove(P(names[5 + i])); // positions go stale lazily

        Assert.True(catalog.RenameEntry(P(names[30]), P("zz.jpg")));

        Assert.Equal(20, catalog.IndexOf(P("zz.jpg")));
        Assert.Equal(P("zz.jpg"), catalog.PathAt(20));
        Assert.Equal(-1, catalog.IndexOf(P(names[30])));
        Assert.Equal(18, catalog.IndexOf(P(names[28])));
        Assert.Equal(1, catalog.IndexOf(P(names[1])));
    }

    [Fact]
    public void ReplaceOrder_AfterRename_AcceptsNewPathSetAndRejectsOld()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));

        Assert.False(catalog.ReplaceOrder([P("c.jpg"), P("b.jpg"), P("a.jpg")]));
        Assert.True(catalog.ReplaceOrder([P("c.jpg"), P("z.jpg"), P("a.jpg")]));

        Assert.Equal([P("c.jpg"), P("z.jpg"), P("a.jpg")], catalog.Paths);
    }

    [Fact]
    public void RenameEntry_AfterReplaceOrder_RenamesInThePermutedPosition()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        Assert.True(catalog.ReplaceOrder([P("c.jpg"), P("a.jpg"), P("b.jpg")]));

        Assert.True(catalog.RenameEntry(P("a.jpg"), P("z.jpg")));

        Assert.Equal([P("c.jpg"), P("z.jpg"), P("b.jpg")], catalog.Paths);
        Assert.Equal(1, catalog.IndexOf(P("z.jpg")));
    }

    [Fact]
    public void Reset_AfterRename_RebuildsFromScratch()
    {
        var catalog = Build("a.jpg", "b.jpg");
        Assert.True(catalog.RenameEntry(P("a.jpg"), P("z.jpg")));

        catalog.Reset([P("m.jpg"), P("n.jpg"), P("z.jpg")]);

        Assert.Equal(3, catalog.Count);
        Assert.Equal(2, catalog.IndexOf(P("z.jpg")));
        Assert.Equal(-1, catalog.IndexOf(P("a.jpg")));
        Assert.Equal(0, catalog.CurrentIndex);
    }

    [Fact]
    public void Remove_AfterRename_RemovesTheRenamedEntryAndNotAnother()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));

        catalog.Remove(P("z.jpg"));

        Assert.Equal([P("a.jpg"), P("c.jpg")], catalog.Paths);
        Assert.Null(catalog.Find(P("z.jpg")));
    }

    [Fact]
    public void Remove_OldNameAfterRename_IsNoOp()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));
        var version = catalog.StructuralVersion;

        catalog.Remove(P("b.jpg"));

        Assert.Equal([P("a.jpg"), P("z.jpg"), P("c.jpg")], catalog.Paths);
        Assert.Equal(version, catalog.StructuralVersion);
    }

    [Fact]
    public void Restore_AfterRename_DoesNotResurrectTheOldName()
    {
        var catalog = Build("a.jpg", "b.jpg", "c.jpg");
        Assert.True(catalog.RenameEntry(P("b.jpg"), P("z.jpg")));
        catalog.Remove(P("z.jpg"));

        // Undo restores the path it removed (the new name), never the pre-rename spelling.
        Assert.True(catalog.Restore(P("z.jpg"), 1));

        Assert.Equal([P("a.jpg"), P("z.jpg"), P("c.jpg")], catalog.Paths);
        Assert.Equal(-1, catalog.IndexOf(P("b.jpg")));
    }

#if DEBUG
    [Fact]
    public void RenameEntry_FromAnotherThreadAfterBind_ThrowsInDebug()
    {
        var catalog = Build("a.jpg", "b.jpg");
        catalog.BindToCurrentThread();

        Exception? error = null;
        var worker = new Thread(() =>
        {
            try { catalog.RenameEntry(P("a.jpg"), P("z.jpg")); }
            catch (Exception ex) { error = ex; }
        });
        worker.Start();
        worker.Join();

        Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(P("a.jpg"), catalog.PathAt(0));
    }
#endif
}
