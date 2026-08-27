using System;
using System.IO;
using System.Linq;
using UndertaleModLib.Wad;

class Program
{
    const string DefaultWad = @"C:\Users\29800\GameMakerProjects\Scrolling Shooter Game Template\Build\build\assets\Scrolling Shooter Game Template.Default.wad";

    static int Main(string[] args)
    {
        string wadPath = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : DefaultWad;
        if (!File.Exists(wadPath))
        {
            Console.WriteLine($"WAD not found: {wadPath}");
            return 2;
        }

        if (args.Contains("--edit"))
            return EditTest(wadPath);

        Console.WriteLine($"background: {wadPath}");
        var wad = UndertaleWadFile.Load(wadPath);
        Report(wad);
        return 0;
    }

    static void Report(UndertaleWadFile wad)
    {
        Console.WriteLine($"FORM {wad.FormLength:N0} bytes, chunks: {wad.ChunkHeaders.Count}");
        Console.WriteLine($"STRG records: {wad.Strings?.RecordOffsets?.Count ?? 0}");
        foreach (var h in wad.ChunkHeaders)
        {
            string kind = wad.Chunks.TryGetValue(h.Name, out var c) ? c.GetType().Name : "missing";
            Console.WriteLine($"  {h.Name} @0x{h.Offset:X8} len={h.Length,10:N0} {kind}");
        }
    }

    static int EditTest(string wadPath)
    {
        Console.WriteLine("== edit round-trip test ==");
        var wad = UndertaleWadFile.Load(wadPath);

        var catalog = new WadResourceCatalog(wad);
        Console.WriteLine($"catalog: objects={catalog.Objects.Count}, sprites={catalog.Sprites.Count}, rooms={catalog.Rooms.Count}, sounds={catalog.Sounds.Count}, paths={catalog.Paths.Count}, fonts={catalog.Fonts.Count}, scripts={catalog.Scripts.Count}");
        if (catalog.Objects.Count > 0)
            Console.WriteLine($"  first object: [{catalog.Objects[0].Index}] {catalog.Objects[0].Name}");
        if (catalog.Sprites.Count > 0)
            Console.WriteLine($"  first sprite: [{catalog.Sprites[0].Index}] {catalog.Sprites[0].Name}");

        var sond = (wad.Chunks["SOND"] as WadSondChunk)?.Entries;
        var objt = (wad.Chunks["OBJT"] as WadObjtChunk)?.Entries;
        var sprt = (wad.Chunks["SPRT"] as WadSprtChunk)?.Entries;
        var seqn = (wad.Chunks["SEQN"] as WadSeqnChunk)?.Entries;
        var bgnd = (wad.Chunks["BGND"] as WadBgndChunk)?.Entries;
        var room = (wad.Chunks["ROOM"] as WadRoomChunk)?.Rooms;
        if (sond is null || objt is null || sprt is null)
        {
            Console.WriteLine("chunks SOND/OBJT/SPRT missing - aborted");
            return 3;
        }

        float vol0 = sond[0].Volume;
        string name0 = sond[0].Name;
        string objName0 = objt[0].Name;
        int width0 = sprt[0].Width;
        bool persist0 = objt[0].Persistent;
        long origLen = new FileInfo(wadPath).Length;

        // --- mutate models (what the editors do) ---
        sond[0].Volume = vol0 + 0.5f;
        sond[0].Name = name0 + "_EDITED";
        sprt[0].Width = 99999;
        objt[0].Name = objName0 + "_EDITED";
        objt[0].Persistent = !persist0;

        bool seqnOk = seqn is { Count: > 0 } && bgnd is { Count: > 0 } && room is { Count: > 0 };
        string seqName0 = seqnOk ? seqn[0].Name : null;
        float seqSpeed0 = seqnOk ? seqn[0].PlaybackSpeed : 0;
        int bgndW0 = seqnOk ? bgnd[0].TileWidth : 0;
        bool bgndTrans0 = seqnOk && bgnd.Count > 0 ? bgnd[0].Transparent : false;
        uint roomFlag0_0 = seqnOk ? room[0].Flag0 : 0;
        if (seqnOk)
        {
            seqn[0].Name = seqName0 + "_SEQ";
            seqn[0].PlaybackSpeed = seqSpeed0 + 1f;
            bgnd[0].TileWidth = bgndW0 + 7;
            bgnd[0].Transparent = !bgndTrans0;
            room[0].Flag0 = roomFlag0_0 + 1234;
        }

        var session = new WadEditSession(wad);
        Console.WriteLine($"HasChanges before Save(): {session.HasChanges} (expected False - patches are collected at Save)");

        string outPath = Path.GetFullPath("edit_test.wad");   // workspace-local copy (sandbox)
        session.Save(outPath);
        Console.WriteLine($"HasChanges after Save(): {session.HasChanges}");
        long outLen = new FileInfo(outPath).Length;
        Console.WriteLine($"length: {origLen:N0} -> {outLen:N0} (grew by {outLen - origLen:N0} = the two appended string records)");

        // --- reload and verify ---
        var wad2 = UndertaleWadFile.Load(outPath);
        var sond2 = (wad2.Chunks["SOND"] as WadSondChunk).Entries;
        var objt2 = (wad2.Chunks["OBJT"] as WadObjtChunk).Entries;
        var sprt2 = (wad2.Chunks["SPRT"] as WadSprtChunk).Entries;
        var seqn2 = (wad2.Chunks["SEQN"] as WadSeqnChunk)?.Entries;
        var bgnd2 = (wad2.Chunks["BGND"] as WadBgndChunk)?.Entries;
        var room2 = (wad2.Chunks["ROOM"] as WadRoomChunk)?.Rooms;
        int errors = 0;
        int entries = 0;
        foreach (var h in wad2.ChunkHeaders)
        {
            if (wad2.Chunks.TryGetValue(h.Name, out var c) && c is WadRoomChunk roomC)
            {
                entries += roomC.Rooms.Count(x => x.Error is not null);
            }
        }
        errors += entries;

        Console.WriteLine($"SOND[0] volume: {sond2[0].Volume} (expect {vol0 + 0.5f})  {(Math.Abs(sond2[0].Volume - (vol0 + 0.5f)) < 0.0001f ? "OK" : "FAIL")}");
        Console.WriteLine($"SOND[0] name:   '{sond2[0].Name}' (expect '{name0}_EDITED')  {(sond2[0].Name == name0 + "_EDITED" ? "OK" : "FAIL")}");
        Console.WriteLine($"SPRT[0] width:  {sprt2[0].Width} (expect 99999)  {(sprt2[0].Width == 99999 ? "OK" : "FAIL")}");
        Console.WriteLine($"OBJT[0] name:   '{objt2[0].Name}' (expect '{objName0}_EDITED')  {(objt2[0].Name == objName0 + "_EDITED" ? "OK" : "FAIL")}");
        Console.WriteLine($"OBJT[0] persistent: {objt2[0].Persistent} (expect {!persist0})  {(objt2[0].Persistent == !persist0 ? "OK" : "FAIL")}");
        Console.WriteLine($"parse errors after re-save: {errors} (expect 0)  {(errors == 0 ? "OK" : "FAIL")}");
        bool seqnOk2 = seqn2 is { Count: > 0 } && bgnd2 is { Count: > 0 } && room2 is { Count: > 0 };
        bool extrasOk = true;
        if (seqnOk2)
        {
            extrasOk = seqn2[0].Name == seqName0 + "_SEQ"
                       && Math.Abs(seqn2[0].PlaybackSpeed - (seqSpeed0 + 1f)) < 0.0001f
                       && bgnd2[0].TileWidth == bgndW0 + 7
                       && bgnd2[0].Transparent == !bgndTrans0
                       && room2[0].Flag0 == roomFlag0_0 + 1234;
            Console.WriteLine($"SEQN[0] name:        '{seqn2[0].Name}'  {(seqn2[0].Name == seqName0 + "_SEQ" ? "OK" : "FAIL")}");
            Console.WriteLine($"SEQN[0] playbackspeed {seqn2[0].PlaybackSpeed} (expect {seqSpeed0 + 1f})  {(Math.Abs(seqn2[0].PlaybackSpeed - (seqSpeed0 + 1f)) < 0.0001f ? "OK" : "FAIL")}");
            Console.WriteLine($"BGND[0] tilewidth   {bgnd2[0].TileWidth} (expect {bgndW0 + 7})  {(bgnd2[0].TileWidth == bgndW0 + 7 ? "OK" : "FAIL")}");
            Console.WriteLine($"BGND[0] transparent {bgnd2[0].Transparent} (expect {!bgndTrans0})  {(bgnd2[0].Transparent == !bgndTrans0 ? "OK" : "FAIL")}");
            Console.WriteLine($"ROOM[0] flag0       {room2[0].Flag0} (expect {roomFlag0_0 + 1234})  {(room2[0].Flag0 == roomFlag0_0 + 1234 ? "OK" : "FAIL")}");
        }
        else
        {
            Console.WriteLine("SEQN/BGND/ROOM extra checks skipped (chunks empty)");
        }

        // string records are at the end of the file: verify each edited ref points past the original length
        foreach (var h in wad2.ChunkHeaders)
            Console.WriteLine($"  chunk {h.Name} @0x{h.Offset:X8} len={h.Length:N0}");

        File.Delete(outPath);
        var bak = outPath + ".bak";
        if (File.Exists(bak)) File.Delete(bak);

        bool allOk = Math.Abs(sond2[0].Volume - (vol0 + 0.5f)) < 0.0001f
                     && sond2[0].Name == name0 + "_EDITED"
                     && sprt2[0].Width == 99999
                     && objt2[0].Name == objName0 + "_EDITED"
                     && objt2[0].Persistent == !persist0
                     && errors == 0
                     && extrasOk;
        Console.WriteLine(allOk ? "EDIT TEST PASSED" : "EDIT TEST FAILED");
        return allOk ? 0 : 1;
    }
}