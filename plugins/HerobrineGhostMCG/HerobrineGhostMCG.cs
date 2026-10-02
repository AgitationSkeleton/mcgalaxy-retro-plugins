//reference System.dll
//reference System.Core.dll
using System;
using System.Collections.Generic;
using MCGalaxy;
using MCGalaxy.Commands;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Maths;
using MCGalaxy.Tasks;

public sealed class HerobrineGhostMCG : Plugin {
    public override string name { get { return "HerobrineGhostMCG"; } }
    public override string creator { get { return "Vio's Arcade"; } }
    public override string MCGalaxy_Version { get { return "1.9.0.0"; } }

    const double ChancePerCheck = 0.02;
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);
    static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(90);
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(4);
    static readonly Random Rng = new Random();
    static readonly object Sync = new object();
    static readonly Dictionary<string, DateTime> LastSightings = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<Player, GhostEntity> Active = new Dictionary<Player, GhostEntity>();
    static SchedulerTask checkTask;
    readonly Command command = new CmdHerobrineGhost();

    public override void Load(bool startup) {
        Command.Register(command);
        OnJoinedLevelEvent.Register(JoinedLevel, Priority.Low);
        OnPlayerDisconnectEvent.Register(Disconnected, Priority.Low);
        checkTask = Server.MainScheduler.QueueRepeat(CheckPlayers, null, CheckInterval);
        Logger.Log(LogType.SystemActivity, "[HerobrineGhostMCG] Loaded (packet-only, viewer-specific apparitions)");
    }
    public override void Unload(bool shutdown) {
        Command.Unregister(command);
        OnJoinedLevelEvent.Unregister(JoinedLevel);
        OnPlayerDisconnectEvent.Unregister(Disconnected);
        if (checkTask != null) Server.MainScheduler.Cancel(checkTask);
        foreach (Player p in new List<Player>(Active.Keys)) Despawn(p);
    }

    static void CheckPlayers(SchedulerTask task) {
        DateTime now = DateTime.UtcNow;
        foreach (Player p in PlayerInfo.Online.Items) {
            if (!Eligible(p, now) || Rng.NextDouble() > ChancePerCheck) continue;
            SpawnFor(p, false);
        }
    }

    static bool Eligible(Player p, DateTime now) {
        if (p == null || p.level == null || p.Loading) return false;
        lock (Sync) {
            if (Active.ContainsKey(p)) return false;
            DateTime last;
            if (LastSightings.TryGetValue(p.name, out last) && now - last < Cooldown) return false;
        }
        foreach (Player other in PlayerInfo.Online.Items) {
            if (other == null || other == p || other.level != p.level) continue;
            double dx = (other.Pos.X - p.Pos.X) / 32.0, dy = (other.Pos.Y - p.Pos.Y) / 32.0, dz = (other.Pos.Z - p.Pos.Z) / 32.0;
            if (dx * dx + dy * dy + dz * dz < 4096.0) return false;
        }
        return true;
    }

    internal static bool SpawnFor(Player viewer, bool forced) {
        if (viewer == null || viewer.level == null) return false;
        if (!forced && !Eligible(viewer, DateTime.UtcNow)) return false;
        Position pos;
        if (!FindPosition(viewer, out pos)) {
            if (forced) viewer.Message("&WNo valid distant sighting position was found.");
            return false;
        }
        Orientation rot = LookAt(pos, viewer.Pos);
        GhostEntity ghost = new GhostEntity(viewer.level);
        ghost.SetInitialPos(pos); ghost.Rot = rot; ghost._lastRot = rot;
        if (!viewer.EntityList.Add(ghost, pos, rot, false)) return false;
        lock (Sync) { Active[viewer] = ghost; LastSightings[viewer.name] = DateTime.UtcNow; }
        GhostState state = new GhostState(viewer, ghost);
        state.Tracker = Server.MainScheduler.QueueRepeat(Track, state, TimeSpan.FromMilliseconds(250));
        ghost.Tracker = state.Tracker;
        Server.MainScheduler.QueueOnce(Expire, state, Lifetime);
        Logger.Log(LogType.SystemActivity, "[HerobrineGhostMCG] Sighting for {0} on {1}", viewer.name, viewer.level.name);
        return true;
    }

    static bool FindPosition(Player p, out Position result) {
        Level lvl = p.level;
        for (int attempt = 0; attempt < 24; attempt++) {
            double distance = 25.0 + Rng.NextDouble() * 25.0, angle = Rng.NextDouble() * Math.PI * 2.0;
            int x = p.Pos.X / 32 + (int)Math.Round(Math.Cos(angle) * distance);
            int z = p.Pos.Z / 32 + (int)Math.Round(Math.Sin(angle) * distance);
            if (x < 1 || z < 1 || x >= lvl.Width - 1 || z >= lvl.Length - 1) continue;
            for (int y = lvl.Height - 2; y >= 1; y--) {
                ushort ground = lvl.GetBlock((ushort)x, (ushort)y, (ushort)z);
                ushort feet = lvl.GetBlock((ushort)x, (ushort)(y + 1), (ushort)z);
                ushort head = y + 2 < lvl.Height ? lvl.GetBlock((ushort)x, (ushort)(y + 2), (ushort)z) : Block.Invalid;
                if (!Solid(ground) || feet != Block.Air || head != Block.Air) continue;
                result = new Position(x * 32 + 16, (y + 1) * 32 + Entities.CharacterHeight, z * 32 + 16);
                return true;
            }
        }
        result = default(Position); return false;
    }
    static bool Solid(ushort block) {
        return block != Block.Air && block != Block.Water && block != Block.StillWater &&
               block != Block.Lava && block != Block.StillLava && block != Block.Invalid;
    }
    static Orientation LookAt(Position from, Position to) {
        Vec3F32 direction;
        direction.X = to.X - from.X;
        direction.Y = to.Y - from.Y;
        direction.Z = to.Z - from.Z;
        direction = Vec3F32.Normalise(direction);
        byte yaw, pitch;
        DirUtils.GetYawPitch(direction, out yaw, out pitch);
        return new Orientation(yaw, pitch);
    }
    static void Track(SchedulerTask task) {
        GhostState state = (GhostState)task.State;
        GhostEntity current;
        lock (Sync) {
            if (!Active.TryGetValue(state.Viewer, out current) || current != state.Ghost ||
                state.Viewer.level != state.Ghost.Level) {
                Server.MainScheduler.Cancel(task); return;
            }
        }
        state.Ghost.Rot = LookAt(state.Ghost.Pos, state.Viewer.Pos);
    }
    static void Expire(SchedulerTask task) {
        GhostState state = (GhostState)task.State;
        if (state.Tracker != null) Server.MainScheduler.Cancel(state.Tracker);
        lock (Sync) {
            GhostEntity current;
            if (!Active.TryGetValue(state.Viewer, out current) || current != state.Ghost) return;
            Active.Remove(state.Viewer);
        }
        state.Viewer.EntityList.Remove(state.Ghost, false);
    }
    static void Despawn(Player p) {
        GhostEntity ghost;
        lock (Sync) { if (!Active.TryGetValue(p, out ghost)) return; Active.Remove(p); }
        if (ghost.Tracker != null) Server.MainScheduler.Cancel(ghost.Tracker);
        if (p.EntityList != null) p.EntityList.Remove(ghost, false);
    }
    static void JoinedLevel(Player p, Level prev, Level level, ref bool announce) { Despawn(p); }
    static void Disconnected(Player p, string reason) { Despawn(p); }

    sealed class GhostState {
        public Player Viewer; public GhostEntity Ghost; public SchedulerTask Tracker;
        public GhostState(Player p, GhostEntity g) { Viewer = p; Ghost = g; }
    }
    sealed class GhostEntity : Entity {
        readonly Level level;
        public SchedulerTask Tracker;
        public GhostEntity(Level level) { this.level = level; untracked = true; autoBroadcastPosition = true; SkinName = "https://minotar.net/skin/MHF_Herobrine.png"; SetModel("humanoid"); }
        public override Level Level { get { return level; } }
        public override bool RestrictsScale { get { return false; } }
        public override bool CanSeeEntity(Entity other) { return true; }
        public override bool SharesTabListWith(Player target) { return false; }
        public override string GetTabListName() { return ""; }
        public override string GetTabListNick(Player target) { return ""; }
        public override string GetTabListSuffix() { return null; }
        public override string GetTabListGroup() { return ""; }
        public override byte GetTabListRank() { return 0; }
        public override string GetSpawnModel(Player target) { return "humanoid"; }
        public override string GetSpawnSkin(Player target) { return SkinName; }
        public override string GetSpawnName(Player target) { return ""; }
    }
}

public sealed class CmdHerobrineGhost : Command2 {
    public override string name { get { return "HerobrineGhost"; } }
    public override string shortcut { get { return "HBGhost"; } }
    public override string type { get { return CommandTypes.Other; } }
    public override LevelPermission defaultRank { get { return LevelPermission.Admin; } }
    public override void Use(Player p, string message, CommandData data) {
        if (message.CaselessEq("test")) { if (HerobrineGhostMCG.SpawnFor(p, true)) p.Message("&8You feel watched."); return; }
        Help(p);
    }
    public override void Help(Player p) { p.Message("&T/HerobrineGhost test &H- creates a private test sighting."); }
}
