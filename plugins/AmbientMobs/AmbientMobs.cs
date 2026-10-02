//reference System.dll
//reference System.Core.dll
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using MCGalaxy;
using MCGalaxy.Blocks;
using MCGalaxy.Commands;
using MCGalaxy.Events.LevelEvents;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Maths;
using MCGalaxy.Tasks;

public sealed class AmbientMobs : Plugin {
    public override string name { get { return "AmbientMobs"; } }
    public override string creator { get { return "Vio's Arcade"; } }
    public override string MCGalaxy_Version { get { return "1.9.5.3"; } }

    const int DespawnDistance = 32, OwnerlessTicks = 600;
    static readonly string[] Models = { "zombie", "skeleton", "pig", "creeper", "spider", "sheep" };
    static readonly object Sync = new object();
    static readonly List<AmbientEntity> Mobs = new List<AmbientEntity>();
    static readonly Dictionary<string, bool> WorldSettings = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<Player, HashSet<AmbientEntity>> Viewers = new Dictionary<Player, HashSet<AmbientEntity>>();
    static readonly HashSet<string> GrazedGrass = new HashSet<string>();
    static readonly HashSet<string> HiddenPlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    static readonly Random Rng = new Random();
    static readonly FieldInfo ExtendedPositionsField = typeof(Entity).GetField("hasExtPositions", BindingFlags.Instance | BindingFlags.NonPublic);
    const string SettingsPath = "plugins/AmbientMobs-worlds.properties";
    const string HiddenPlayersPath = "plugins/AmbientMobs-hidden-players.txt";
    SchedulerTask tickTask;
    int visibilityTicks;
    readonly CmdMobs command = new CmdMobs();

    public override void Load(bool startup) {
        LoadSettings();
        LoadHiddenPlayers();
        Command.Register(command);
        OnJoinedLevelEvent.Register(HandleJoinedLevel, Priority.Low);
        OnLevelUnloadEvent.Register(HandleLevelUnload, Priority.Low);
        OnPlayerDisconnectEvent.Register(HandleDisconnect, Priority.Low);
        tickTask = Server.MainScheduler.QueueRepeat(Tick, null, TimeSpan.FromMilliseconds(50));
        Logger.Log(LogType.SystemActivity, "[AmbientMobs] Loaded (faithful c0.30 Survival Test spawning and peaceful AI)");
    }

    public override void Unload(bool shutdown) {
        Command.Unregister(command);
        if (tickTask != null) Server.MainScheduler.Cancel(tickTask);
        OnJoinedLevelEvent.Unregister(HandleJoinedLevel);
        OnLevelUnloadEvent.Unregister(HandleLevelUnload);
        OnPlayerDisconnectEvent.Unregister(HandleDisconnect);
        lock (Sync) {
            for (int i = Mobs.Count - 1; i >= 0; i--) Despawn(Mobs[i]);
            Mobs.Clear();
            Viewers.Clear();
        }
    }

    void HandleJoinedLevel(Player p, Level previous, Level level, ref bool announce) {
        lock (Sync) SyncVisibility(p);
    }
    void HandleLevelUnload(Level level, ref bool cancel) { RemoveWorld(level); }
    void HandleDisconnect(Player p, string reason) { lock (Sync) Viewers.Remove(p); }

    void Tick(SchedulerTask task) {
        lock (Sync) {
            for (int i = Mobs.Count - 1; i >= 0; i--) {
                AmbientEntity mob = Mobs[i];
                if (!mob.Tick()) { Despawn(mob); Mobs.RemoveAt(i); }
            }
            PushNearbyMobs();
            MaintainPopulation();
            if (++visibilityTicks >= 10) { visibilityTicks = 0; SyncVisibility(); }
        }
    }

    void MaintainPopulation() {
        Level[] levels = LevelInfo.Loaded.Items;
        foreach (Level level in levels) {
            if (!Enabled(level.name)) continue;
            Player[] players = RealPlayers(level);
            if (players.Length == 0) continue;
            int density = PopulationDensity(level), cap = density * 20;
            TrimPopulation(level, cap);
            if (density < 1 || Count(level) >= density * 20 || Rng.Next(100) >= density) continue;
            SpawnPass(level, density, players);
        }
    }

    void SpawnPass(Level level, int passes, Player[] players) {
        int cap = PopulationCap(level);
        for (int pass = 0; pass < passes; pass++) {
            string model = Models[Rng.Next(6)];
            int baseX = Rng.Next(level.Width), baseY = (int)(Math.Min(Rng.NextDouble(), Rng.NextDouble()) * level.Height), baseZ = Rng.Next(level.Length);
            if (Solid(level, baseX, baseY, baseZ) || Liquid(level, baseX, baseY, baseZ) || (SkyLit(level,baseX,baseY,baseZ) && Rng.Next(5)!=0)) continue;
            for (int cluster = 0; cluster < 3; cluster++) {
                int x = baseX, y = baseY, z = baseZ;
                for (int attempt = 0; attempt < 3; attempt++) {
                    if (Count(level) >= cap) return;
                    x += Rng.Next(6) - Rng.Next(6); z += Rng.Next(6) - Rng.Next(6);
                    if (x < 0 || z < 1 || y < 0 || y >= level.Height - 2 || x >= level.Width || z >= level.Length) continue;
                    if (!Solid(level, x, y - 1, z) || Solid(level, x, y, z) || Solid(level, x, y + 1, z)) continue;
                    bool near = false;
                    foreach (Player p in players) { double dx=x+.5-p.Pos.X/32.0,dy=y+1-p.Pos.Y/32.0,dz=z+.5-p.Pos.Z/32.0;if(dx*dx+dy*dy+dz*dz<256){near=true;break;} }
                    if (near) continue;
                    AmbientEntity mob = new AmbientEntity(level, model, Rng.Next()); Position pos;
                    if (!FindSpawn(mob, x, y, z, out pos)) continue;
                    mob.SetInitialPos(pos); mob.SetYawPitch((byte)Rng.Next(256), mob.DefaultPitch);
                    Mobs.Add(mob); Spawn(mob);
                }
            }
        }
    }

    static bool FindSpawn(AmbientEntity mob, int x, int y, int z, out Position pos) {
        Level level = mob.Level;
        Position candidate = new Position(x * 32 + 16, y * 32 + Entities.CharacterHeight, z * 32 + 16);
        AABB body = mob.ModelBB.OffsetPosition(candidate);
        if (AABB.IntersectsSolidBlocks(body, level) || IntersectsLiquid(level, body, 0)) { pos=default(Position);return false; }
        pos = candidate; return true;
    }
    static bool Solid(Level l,int x,int y,int z){if(x<0||y<0||z<0||x>=l.Width||y>=l.Height||z>=l.Length)return true;return CollideType.IsSolid(l.CollideType(l.GetBlock((ushort)x,(ushort)y,(ushort)z)));}
    static bool Liquid(Level l,int x,int y,int z){if(x<0||y<0||z<0||x>=l.Width||y>=l.Height||z>=l.Length)return false;byte c=l.CollideType(l.GetBlock((ushort)x,(ushort)y,(ushort)z));return c==CollideType.SwimThrough||c==CollideType.LiquidWater||c==CollideType.LiquidLava;}
    static bool SkyLit(Level l,int x,int y,int z){for(int yy=y+1;yy<l.Height;yy++)if(!l.LightPasses(l.GetBlock((ushort)x,(ushort)yy,(ushort)z)))return false;return true;}
    static bool IntersectsLiquid(Level l,AABB b,byte wanted){Vec3S32 min=b.BlockMin,max=b.BlockMax;for(int y=min.Y;y<=max.Y;y++)for(int z=min.Z;z<=max.Z;z++)for(int x=min.X;x<=max.X;x++){if(x<0||y<0||z<0||x>=l.Width||y>=l.Height||z>=l.Length)continue;byte c=l.CollideType(l.GetBlock((ushort)x,(ushort)y,(ushort)z));if(wanted==0?(c==CollideType.SwimThrough||c==CollideType.LiquidWater||c==CollideType.LiquidLava):c==wanted)return true;}return false;}

    static void Spawn(AmbientEntity mob) { }
    static void Despawn(AmbientEntity mob) {
        foreach (KeyValuePair<Player, HashSet<AmbientEntity>> pair in Viewers) if (pair.Value.Remove(mob)) Entities.Despawn(pair.Key, mob);
    }
    static double Distance2(Player p, AmbientEntity mob) { double dx=(p.Pos.X-mob.Pos.X)/32.0,dy=(p.Pos.Y-mob.Pos.Y)/32.0,dz=(p.Pos.Z-mob.Pos.Z)/32.0;return dx*dx+dy*dy+dz*dz; }
    static void SyncVisibility() { foreach (Player p in PlayerInfo.Online.Items) if (p != null) SyncVisibility(p); }
    static void SyncVisibility(Player p) {
        HashSet<AmbientEntity> shown; if (!Viewers.TryGetValue(p, out shown)) { shown = new HashSet<AmbientEntity>(); Viewers[p] = shown; }
        if (HiddenPlayers.Contains(p.name)) {
            foreach (AmbientEntity mob in new List<AmbientEntity>(shown)) Entities.Despawn(p, mob);
            shown.Clear(); return;
        }
        string client = p.Session.ClientName() ?? "";
        bool legacyCef = client.IndexOf("cef", StringComparison.OrdinalIgnoreCase) >= 0;
        int visibleLimit = legacyCef ? 24 : 192;
        List<AmbientEntity> nearby = new List<AmbientEntity>();
        foreach (AmbientEntity mob in Mobs) if (mob.Level == p.level && Distance2(p,mob) <= 4096) nearby.Add(mob);
        nearby.Sort(delegate(AmbientEntity a, AmbientEntity b) { return Distance2(p,a).CompareTo(Distance2(p,b)); });
        HashSet<AmbientEntity> wanted = new HashSet<AmbientEntity>();
        for (int i=0; i<nearby.Count && i<visibleLimit; i++) wanted.Add(nearby[i]);
        List<AmbientEntity> remove = new List<AmbientEntity>(); foreach (AmbientEntity mob in shown) if (!wanted.Contains(mob)) remove.Add(mob);
        foreach (AmbientEntity mob in remove) { Entities.Despawn(p,mob); shown.Remove(mob); }
        int additions = 0;
        foreach (AmbientEntity mob in wanted) if (!shown.Contains(mob)) {
            if (legacyCef && additions >= 1) break;
            if (p.EntityList.Add(mob,mob.Pos,mob.Rot,true)) {
                shown.Add(mob); additions++;
                if (legacyCef) Logger.Log(LogType.SystemActivity, "[AmbientMobs] CEF spawn to {0}: {1} #{2} at {3}", p.name, mob.Model, mob.RuntimeId, mob.Pos);
            }
        }
    }
    static void RemoveWorld(Level level) {
        lock (Sync) for (int i = Mobs.Count - 1; i >= 0; i--) if (Mobs[i].Level == level) {
            Despawn(Mobs[i]); Mobs.RemoveAt(i);
        }
        lock (Sync) GrazedGrass.RemoveWhere(delegate(string key) { return key.StartsWith(level.name + "\n", StringComparison.OrdinalIgnoreCase); });
    }
    static int Count(Level level) { int n = 0; foreach (AmbientEntity m in Mobs) if (m.Level == level) n++; return n; }
    static string BucketKey(AmbientEntity mob, int dx, int dz) { return mob.Level.name + "\n" + (mob.Pos.X / 64 + dx) + "\n" + (mob.Pos.Z / 64 + dz); }
    static void PushNearbyMobs() {
        Dictionary<string, List<AmbientEntity>> buckets = new Dictionary<string, List<AmbientEntity>>();
        foreach (AmbientEntity mob in Mobs) { string key=BucketKey(mob,0,0); List<AmbientEntity> list; if(!buckets.TryGetValue(key,out list)){list=new List<AmbientEntity>();buckets[key]=list;} list.Add(mob); }
        foreach (AmbientEntity mob in Mobs) for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++){List<AmbientEntity> list;if(!buckets.TryGetValue(BucketKey(mob,dx,dz),out list))continue;foreach(AmbientEntity other in list)if(other.RuntimeId>mob.RuntimeId)mob.PushApart(other);}
    }
    static int PopulationDensity(Level level) {
        int width = Math.Min((int)level.Width, 512), height = Math.Min((int)level.Height, 64), length = Math.Min((int)level.Length, 512);
        return width * height * length / 64 / 64 / 64;
    }
    static int PopulationCap(Level level) { return PopulationDensity(level) * 20; }
    static void TrimPopulation(Level level, int cap) {
        for (int i=Mobs.Count-1; i>=0 && Count(level)>cap; i--) if (Mobs[i].Level==level) { Despawn(Mobs[i]); Mobs.RemoveAt(i); }
    }
    static string GrassKey(Level level, int x, int y, int z) { return level.name + "\n" + x + "\n" + y + "\n" + z; }
    static bool Enabled(string world) { bool enabled; return !WorldSettings.TryGetValue(world, out enabled) || enabled; }
    static void LoadSettings() {
        WorldSettings.Clear();
        if (!File.Exists(SettingsPath)) return;
        foreach (string raw in File.ReadAllLines(SettingsPath)) {
            string line = raw.Trim(); if (line.Length == 0 || line.StartsWith("#")) continue;
            int split = line.IndexOf('='); if (split <= 0) continue;
            bool enabled; if (Boolean.TryParse(line.Substring(split + 1).Trim(), out enabled)) WorldSettings[line.Substring(0, split).Trim()] = enabled;
        }
    }
    static void SaveSettings() {
        List<string> lines = new List<string>();
        lines.Add("# Per-world ambient mob spawning; worlds not listed default to true");
        List<string> worlds = new List<string>(WorldSettings.Keys); worlds.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string world in worlds) lines.Add(world + "=" + WorldSettings[world].ToString().ToLowerInvariant());
        File.WriteAllLines(SettingsPath, lines.ToArray());
    }
    static void LoadHiddenPlayers() {
        HiddenPlayers.Clear(); if (!File.Exists(HiddenPlayersPath)) return;
        foreach (string raw in File.ReadAllLines(HiddenPlayersPath)) { string name=raw.Trim(); if(name.Length>0 && !name.StartsWith("#")) HiddenPlayers.Add(name); }
    }
    static void SaveHiddenPlayers() {
        List<string> names = new List<string>(HiddenPlayers); names.Sort(StringComparer.OrdinalIgnoreCase);
        names.Insert(0, "# Players who opted out of client-side ambient mob rendering");
        File.WriteAllLines(HiddenPlayersPath, names.ToArray());
    }
    static void SetEnabled(Level level, bool enabled) {
        lock (Sync) {
            WorldSettings[level.name] = enabled; SaveSettings();
            if (!enabled) RemoveWorld(level);
        }
    }

    public sealed class CmdMobs : Command2 {
        public override string name { get { return "Mobs"; } }
        public override string type { get { return CommandTypes.Information; } }
        public override LevelPermission defaultRank { get { return LevelPermission.Guest; } }
        public override void Use(Player p, string message, CommandData data) {
            string arg = (message ?? "").Trim().ToLowerInvariant();
            if (arg == "hide" || arg == "show") {
                if (p.IsConsole) { p.Message("&WThis option is only available to players."); return; }
                lock (Sync) {
                    bool hide = arg == "hide";
                    if (hide) HiddenPlayers.Add(p.name); else HiddenPlayers.Remove(p.name);
                    SaveHiddenPlayers(); SyncVisibility(p);
                    p.Message("&SAmbient mobs are now {0} &Sfor you.", hide ? "&chidden" : "&ashown");
                }
                return;
            }
            if (arg == "on" || arg == "off") {
                if (p.IsConsole || p.level == null) { p.Message("&WUse this setting in a world as its realm owner."); return; }
                if (p.Rank < LevelPermission.Admin && !LevelInfo.IsRealmOwner(p.level, p.name)) { p.Message("&WOnly this realm's owner may change mob spawning."); return; }
                bool enabled = arg == "on"; SetEnabled(p.level, enabled);
                p.Message("&SAmbient mobs are now {0} &Son {1}&S.", enabled ? "&aenabled" : "&cdisabled", p.level.ColoredName); return;
            }
            lock (Sync) {
                int total = Mobs.Count;
                if (p.level != null) p.Message("&TAmbient mobs: &f{0}&S/&f{1} &Son {2} &S({3}); &f{4} &Sserver-wide; rendering {5}&S.", Count(p.level), PopulationCap(p.level), p.level.ColoredName, Enabled(p.level.name) ? "&aenabled" : "&cdisabled", total, HiddenPlayers.Contains(p.name) ? "&chidden" : "&ashown");
                else p.Message("&TAmbient mobs: &f{0} &Sserver-wide.", total);
            }
            p.Message("&T/Mobs on&S or &T/Mobs off &S- realm owners control spawning in their current world.");
            p.Message("&T/Mobs hide&S or &T/Mobs show &S- controls mob rendering only for you.");
        }
        public override void Help(Player p) {
            p.Message("&T/Mobs &H- Shows current-world and total ambient mob counts.");
            p.Message("&T/Mobs on|off &H- Enables or disables spawning for a realm you own.");
            p.Message("&T/Mobs hide|show &H- Hides or shows ambient mobs only for you.");
        }
    }
    static Player[] RealPlayers(Level level) {
        List<Player> found = new List<Player>();
        foreach (Player p in PlayerInfo.Online.Items) if (p != null && p.level == level) found.Add(p);
        return found.ToArray();
    }

    public static string DynmapJson() {
        lock (Sync) {
            StringBuilder s = new StringBuilder("[");
            for (int i = 0; i < Mobs.Count; i++) {
                AmbientEntity m = Mobs[i]; if (i != 0) s.Append(',');
                Position p = m.Pos;
                s.AppendFormat(CultureInfo.InvariantCulture,
                    "{{\"id\":\"{0}\",\"model\":\"{1}\",\"world\":\"{2}\",\"x\":{3},\"y\":{4},\"z\":{5}}}",
                    m.RuntimeId, m.Model, Escape(m.Level.name), p.X / 32.0, p.Y / 32.0, p.Z / 32.0);
            }
            return s.Append(']').ToString();
        }
    }
    static string Escape(string value) { return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }

    public sealed class AmbientEntity : Entity {
        static int nextId;
        readonly Level level;
        readonly Random random;
        double xxa, yya, yawAcceleration, velocityX, velocityY, velocityZ;
        int inactiveTicks;
        Position previous;
        public readonly int RuntimeId;
        int grazingTicks;

        public AmbientEntity(Level level, string model, int seed) {
            this.level = level; random = new Random(seed); RuntimeId = ++nextId;
            untracked = true; autoBroadcastPosition = true;
            if (ExtendedPositionsField != null) ExtendedPositionsField.SetValue(this, true);
            SkinName = model == "humanoid" ? "human" : model;
            SetModel(model); previous = Pos;
        }
        public override Level Level { get { return level; } }
        public override bool RestrictsScale { get { return false; } }
        public override bool CanSeeEntity(Entity other) { return true; }
        public override bool SharesTabListWith(Player target) { return false; }
        public override string GetTabListName() { return ""; }
        public override string GetTabListNick(Player target) { return ""; }
        public override string GetTabListSuffix() { return null; }
        public override string GetTabListGroup() { return ""; }
        public override byte GetTabListRank() { return 0; }
        public override string GetSpawnModel(Player target) { return Model; }
        public override string GetSpawnSkin(Player target) { return SkinName; }
        public override string GetSpawnName(Player target) { return ""; }
        public byte DefaultPitch { get { return Orientation.DegreesToPacked(Model=="creeper"?45:(Model=="zombie"?30:0)); } }

        public bool Tick() {
            Player nearest = null; double distance2 = Double.MaxValue;
            foreach (Player p in PlayerInfo.Online.Items) if (p != null && p.level == level) {
                double dx = (p.Pos.X - Pos.X) / 32.0, dz = (p.Pos.Z - Pos.Z) / 32.0;
                double d = dx * dx + dz * dz; if (d < distance2) { distance2 = d; nearest = p; }
            }
            if (nearest == null) return false;
            inactiveTicks++;
            if(inactiveTicks>OwnerlessTicks&&random.Next(800)==0){if(distance2<DespawnDistance*DespawnDistance)inactiveTicks=0;else return false;}

            double speed = Model == "skeleton" ? 0.3 : (Model == "zombie" ? 1.0 : 0.7);
            bool grazing = false;
            if(Model=="sheep"){
                double rr=Orientation.PackedToDegrees(Rot.RotY)*Math.PI/180.0;int gx=(int)(Pos.X/32.0+Math.Sin(rr)*.7),gy=Pos.Y/32-2,gz=(int)(Pos.Z/32.0-Math.Cos(rr)*.7);
                if(grazingTicks>0){grazing=true;if(!IsGrass(gx,gy,gz))grazingTicks=0;else if(++grazingTicks==60){GrazedGrass.Add(GrassKey(level,gx,gy,gz));grazingTicks=0;}}
                else if(IsGrass(gx,gy,gz)){grazingTicks=1;grazing=true;}
            }
            if(!grazing && random.NextDouble() < 0.07) { xxa = (random.NextDouble() - 0.5) * speed; yya = random.NextDouble() * speed; }
            bool jump = !grazing && random.NextDouble() < 0.01;
            if(!grazing && random.NextDouble() < 0.04) yawAcceleration = (random.NextDouble() - 0.5) * 60.0;
            if(grazing){xxa=0;yya=0;}
            bool water=InLiquid(CollideType.LiquidWater)||InLiquid(CollideType.SwimThrough),lava=InLiquid(CollideType.LiquidLava);
            if((water||lava))jump=random.NextDouble()<0.8;
            xxa *= 0.98; yya *= 0.98; yawAcceleration *= 0.9;

            double yaw = Orientation.PackedToDegrees(Rot.RotY) + yawAcceleration;
            while (yaw < 0) yaw += 360; while (yaw >= 360) yaw -= 360;
            SetYawPitch(Orientation.DegreesToPacked((int)yaw), grazing?Orientation.DegreesToPacked(40+(grazingTicks/2%2)*10):DefaultPitch);
            double radians = yaw * Math.PI / 180.0;
            double impulse = (water||lava)?0.02:(IsGrounded() ? 0.10 : 0.02);
            double inputLength = Math.Sqrt(xxa * xxa + yya * yya);
            if(inputLength >= 0.01) {
                if(inputLength < 1.0) inputLength = 1.0;
                double scale = impulse / inputLength;
                double strafe = xxa * scale, forward = yya * scale;
                velocityX += strafe * Math.Cos(radians) + forward * Math.Sin(radians);
                velocityZ += strafe * Math.Sin(radians) - forward * Math.Cos(radians);
            }
            if(jump){if(water||lava)velocityY+=0.04;else if(IsGrounded())velocityY=0.42;}
            Move(velocityX * 32.0, velocityY * 32.0, velocityZ * 32.0);
            if(water){velocityX*=.8;velocityY=velocityY*.8-.02;velocityZ*=.8;}
            else if(lava){velocityX*=.5;velocityY=velocityY*.5-.02;velocityZ*=.5;}
            else {velocityX*=.91;velocityY=velocityY*.98-.08;velocityZ*=.91;if(IsGrounded()){velocityX*=.6;velocityZ*=.6;}}
            previous = Pos; return true;
        }

        bool IsGrass(int x,int y,int z){return x>=0&&y>=0&&z>=0&&x<level.Width&&y<level.Height&&z<level.Length&&!GrazedGrass.Contains(GrassKey(level,x,y,z))&&Block.Convert(level.GetBlock((ushort)x,(ushort)y,(ushort)z))==Block.Grass;}
        bool InLiquid(byte type){return IntersectsLiquid(level,ModelBB.OffsetPosition(Pos).Adjust(0,-13,0),type);}
        bool IsGrounded() { return AABB.IntersectsSolidBlocks(ModelBB.OffsetPosition(Pos).Offset(0, -2, 0), level); }
        public void PushApart(AmbientEntity other){double dx=(other.Pos.X-Pos.X)/32.0,dz=(other.Pos.Z-Pos.Z)/32.0,d2=dx*dx+dz*dz;if(d2<.01||d2>2.25)return;double d=Math.Sqrt(d2),force=.05/d;velocityX-=dx/d*force;velocityZ-=dz/d*force;other.velocityX+=dx/d*force;other.velocityZ+=dz/d*force;}
        void Move(double dx, double dy, double dz) {
            Position start=Pos,next=start;bool wasGrounded=IsGrounded();int mx=(int)Math.Round(dx),my=(int)Math.Round(dy),mz=(int)Math.Round(dz);
            int ay=MoveAxis(ref next,my,1),ax=MoveAxis(ref next,mx,0),az=MoveAxis(ref next,mz,2);
            if(ay!=my)velocityY=0;
            if((ax!=mx||az!=mz)&&wasGrounded){Position step=start;int up=MoveAxis(ref step,16,1),sx=MoveAxis(ref step,mx,0),sz=MoveAxis(ref step,mz,2);MoveAxis(ref step,-16,1);int normal=ax*ax+az*az,raised=sx*sx+sz*sz;if(up==16&&raised>normal){next=step;ax=sx;az=sz;}}
            if(ax!=mx)velocityX=0;if(az!=mz)velocityZ=0;
            if (next.X < 16 || next.Z < 16 || next.X >= level.Width * 32 - 16 || next.Z >= level.Length * 32 - 16) { velocityX=0; velocityZ=0; return; }
            Pos=next;
        }
        int MoveAxis(ref Position p,int amount,int axis){int sign=Math.Sign(amount),moved=0;for(int i=0;i<Math.Abs(amount);i++){Position n=p;if(axis==0)n.X+=sign;else if(axis==1)n.Y+=sign;else n.Z+=sign;if(AABB.IntersectsSolidBlocks(ModelBB.OffsetPosition(n),level))break;p=n;moved+=sign;}return moved;}
    }
}
