//reference System.dll
//reference System.Core.dll
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MCGalaxy;
using MCGalaxy.Blocks;
using MCGalaxy.Commands;
using MCGalaxy.Generator;

public sealed class IndevWorldGen : Plugin {
    public override string name { get { return "IndevWorldGen"; } }
    public override string creator { get { return "Vio's Arcade"; } }
    public override string MCGalaxy_Version { get { return "1.9.0.0"; } }

    internal const string Root = "plugins/IndevWorldGen";
    static readonly CmdIndevGen cmd = new CmdIndevGen();

    public override void Load(bool startup) {
        Directory.CreateDirectory(Root + "/work");
        MapGenBiome.Biomes["IndevRaw"] = new MapGenBiome();
        MapGen.Register("Indev", GenType.Advanced, Generate,
            "&HOriginal Minecraft Indev (2010-02-23) generator. Seed: [type] [theme] <numeric seed>.");
        Command.Register(cmd);
    }


    public override void Unload(bool shutdown) {
        Command.Unregister(cmd);
        MapGen.Generators.RemoveAll(delegate(MapGen g) { return g.Theme.CaselessEq("Indev"); });
        MapGenBiome.Biomes.Remove("IndevRaw");
    }

    internal static bool Generate(Player p, Level lvl, MapGenArgs args) {
        string type = "inland", theme = "normal";
        long seed = DateTime.UtcNow.Ticks;
        string[] parts = args.Args.SplitSpaces();
        if (parts.Length > 0 && parts[0].Length > 0) type = NormalType(parts[0]);
        if (parts.Length > 1) theme = NormalTheme(parts[1]);
        if (parts.Length > 2 && !Int64.TryParse(parts[2], out seed)) seed = JavaHash(parts[2]);
        if (type == null || theme == null) { p.Message("&WInvalid Indev type or theme."); return false; }
        args.Biome = "IndevRaw";

        string output = Root + "/work/" + Guid.NewGuid().ToString("N") + ".bin";
        try {
            RunBridge(output, lvl.Width, lvl.Length, lvl.Height, TypeId(type), ThemeId(theme), seed, lvl.name);
            Import(output, lvl);
            Logger.Log(LogType.SystemActivity, "[IndevWorldGen] Generated {0}: {1}, {2}, seed {3}", lvl.name, type, theme, seed);
            return true;
        } catch (Exception ex) {
            Logger.LogError("[IndevWorldGen] Generation failed", ex);
            p.Message("&WIndev generation failed. See the server error log.");
            return false;
        } finally { try { File.Delete(output); } catch { } }
    }

    static void RunBridge(string output, int width, int length, int height, int type, int theme, long seed, string name) {
        string bridge = Path.GetFullPath(Root + "/IndevGeneratorBridge.jar");
        string indev = Path.GetFullPath(Root + "/in-20100223.jar");
        ProcessStartInfo psi = new ProcessStartInfo("java");
        psi.Arguments = "-cp " + Q(bridge) + " IndevGeneratorBridge " + Q(indev) + " " + Q(Path.GetFullPath(output)) +
            " " + width + " " + length + " " + height + " " + type + " " + theme + " " + seed + " " + Q(name);
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        using (Process proc = Process.Start(psi)) {
            string stdout = proc.StandardOutput.ReadToEnd(), stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(900000)) { proc.Kill(); throw new Exception("Indev generator timed out"); }
            if (proc.ExitCode != 0) throw new Exception("Java bridge exited " + proc.ExitCode + ": " + stderr);
            if (stdout.Length > 0) Logger.Log(LogType.SystemActivity, "[IndevWorldGen] " + stdout.Trim().Replace('\n', ' '));
        }
    }

    static void Import(string path, Level lvl) {
        using (BinaryReader r = new BinaryReader(File.OpenRead(path))) {
            if (ReadI32(r) != 0x494E4456 || ReadI32(r) != 1) throw new InvalidDataException("Invalid bridge output");
            int width = ReadI32(r), height = ReadI32(r), length = ReadI32(r);
            if (width != lvl.Width || height != lvl.Height || length != lvl.Length) throw new InvalidDataException("Dimension mismatch");
            lvl.spawnx = Clamp16(ReadI32(r), width); lvl.spawny = Clamp16(ReadI32(r), height); lvl.spawnz = Clamp16(ReadI32(r), length);
            int waterHeight = ReadI32(r), groundHeight = ReadI32(r), clouds = ReadI32(r);
            int sky = ReadI32(r), fog = ReadI32(r), cloud = ReadI32(r), waterType = ReadI32(r);
            ReadI32(r); ReadI32(r); // Original skylight values have no exact Classic protocol equivalent.
            int count = ReadI32(r);
            byte[] blocks = r.ReadBytes(count);
            if (count != lvl.blocks.Length || blocks.Length != count) throw new InvalidDataException("Block count mismatch");

            EnsureIndevBlocks(lvl);
            for (int i = 0; i < blocks.Length; i++) {
                byte b = blocks[i];
                if (b == 56) b = Block.GoldOre;
                if (b < 50) { lvl.blocks[i] = b; continue; }
                ushort mapped = b == 50 ? (ushort)66 : b == 52 ? (ushort)67 : b == 54 ? (ushort)68 : Block.Stone;
                int x = i % width, yz = i / width, z = yz % length, y = yz / length;
                lvl.SetBlock((ushort)x, (ushort)y, (ushort)z, mapped);
            }
            lvl.Config.SkyColor = sky.ToString("X6"); lvl.Config.FogColor = fog.ToString("X6");
            lvl.Config.CloudColor = cloud.ToString("X6"); lvl.Config.CloudsHeight = clouds;
            lvl.Config.EdgeLevel = waterHeight; lvl.Config.SidesOffset = groundHeight - waterHeight;
            lvl.Config.HorizonBlock = waterType == 11 ? Block.StillLava : Block.StillWater;
            lvl.Config.EdgeBlock = Block.Dirt;
            BlockDefinition.Save(false, lvl);
        }
    }

    static void EnsureIndevBlocks(Level lvl) {
        AddDef(lvl, 66, "Indev Torch", 80, Block.Yellow, true, true);
        AddDef(lvl, 67, "Indev Mob Spawner", 65, Block.Cobblestone, false, false);
        AddDef(lvl, 68, "Indev Chest", 26, Block.Wood, false, false);
    }
    static void AddDef(Level lvl, ushort raw, string name, ushort tex, byte fallback, bool sprite, bool bright) {
        BlockDefinition d = DefaultSet.MakeCustomBlock(fallback);
        d.SetBlock(Block.FromRaw(raw));
        d.Name = name; d.TopTex = d.BottomTex = d.LeftTex = d.RightTex = d.FrontTex = d.BackTex = tex;
        d.FallBack = fallback; d.FullBright = bright; d.Brightness = bright ? 14 : 0;
        if (sprite) { d.Shape = 0; d.BlockDraw = DrawType.Sprite; d.CollideType = CollideType.WalkThrough; d.BlocksLight = false; }
        BlockDefinition.Add(d, lvl.CustomBlockDefs, lvl);
    }

    internal static string NormalType(string s) { s = s.ToLower(); if (s == "classic") return "inland"; return s == "inland" || s == "island" || s == "floating" || s == "flat" ? s : null; }
    internal static string NormalTheme(string s) { s = s.ToLower(); return s == "normal" || s == "hell" || s == "paradise" || s == "woods" ? s : null; }
    static int TypeId(string s) { return s == "island" ? 1 : s == "floating" ? 2 : s == "flat" ? 3 : 0; }
    static int ThemeId(string s) { return s == "hell" ? 1 : s == "paradise" ? 2 : s == "woods" ? 3 : 0; }
    static long JavaHash(string s) { int h = 0; unchecked { foreach (char c in s) h = 31 * h + c; } return h; }
    static ushort Clamp16(int n, int max) { return (ushort)Math.Max(0, Math.Min(max - 1, n)); }
    static string Q(string s) { return "\"" + s.Replace("\"", "\\\"") + "\""; }
    static int ReadI32(BinaryReader r) { byte[] b = r.ReadBytes(4); if (b.Length != 4) throw new EndOfStreamException(); Array.Reverse(b); return BitConverter.ToInt32(b, 0); }
}

public sealed class CmdIndevGen : Command2 {
    public override string name { get { return "IndevGen"; } }
    public override string shortcut { get { return "IGen"; } }
    public override string type { get { return CommandTypes.World; } }
    public override LevelPermission defaultRank { get { return LevelPermission.Admin; } }

    public override void Use(Player p, string message, CommandData data) {
        string[] a = message.SplitSpaces();
        if (a.Length < 1 || a[0].Length == 0) { Help(p); return; }
        string kind = a.Length > 1 ? IndevWorldGen.NormalType(a[1]) : "inland";
        string shape = a.Length > 2 ? a[2].ToLower() : "square";
        string size = a.Length > 3 ? a[3].ToLower() : "normal";
        string theme = a.Length > 4 ? IndevWorldGen.NormalTheme(a[4]) : "normal";
        string seed = a.Length > 5 ? a[5] : DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
        if (kind == null || theme == null || (shape != "square" && shape != "long" && shape != "deep") ||
            (size != "small" && size != "normal" && size != "huge")) { Help(p); return; }

        int baseSize = size == "small" ? 128 : size == "huge" ? 512 : 256;
        int width = baseSize, length = baseSize, height = 64;
        if (shape == "long") { width /= 2; length *= 2; }
        if (shape == "deep") { width /= 2; length = width; height = 256; }
        string[] dims = { a[0], width.ToString(), height.ToString(), length.ToString() };
        ushort w = 0, h = 0, l = 0;
        if (!MapGen.GetDimensions(p, dims, 1, ref w, ref h, ref l)) return;
        Level lvl = null;
        try {
            lvl = MapGen.Generate(p, MapGen.Find("Indev"), a[0], w, h, l, kind + " " + theme + " " + seed);
            if (lvl != null) lvl.Save(true);
        } finally { if (lvl != null) lvl.Dispose(); Server.DoGC(); }
    }

    public override void Help(Player p) {
        p.Message("&T/IndevGen [name] [type] [shape] [size] [theme] <seed>");
        p.Message("&HType: inland/classic, island, floating, flat");
        p.Message("&HShape: square, long, deep. Size: small, normal, huge.");
        p.Message("&HTheme: normal, hell, paradise, woods. Defaults reproduce the Indev menu defaults.");
    }
}
