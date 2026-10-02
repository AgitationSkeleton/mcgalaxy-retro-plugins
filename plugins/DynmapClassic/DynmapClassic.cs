//reference System.dll
//reference System.Core.dll
//reference System.Drawing.dll
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using MCGalaxy;
using MCGalaxy.Blocks;
using MCGalaxy.Config;
using MCGalaxy.Events.LevelEvents;
using MCGalaxy.Events.PlayerEvents;
using MCGalaxy.Levels.IO;
using MCGalaxy.Maths;

public sealed class DynmapClassic : Plugin {
    public override string name { get { return "DynmapClassic"; } }
    public override string creator { get { return "Vio's Arcade"; } }
    public override string MCGalaxy_Version { get { return "1.9.0.0"; } }

    const string Root = "plugins/DynmapClassic", Web = Root + "/web", Renders = Root + "/renders";
    const int Port = 48123, Scale = 4, Border = 512, BorderScale = 1;
    HttpListener http; Thread httpThread, renderThread; volatile bool running;
    Bitmap atlas; readonly object renderLock = new object();
    int[] lightHeight;
    readonly Dictionary<string, WorldMeta> metadata = new Dictionary<string, WorldMeta>(StringComparer.OrdinalIgnoreCase);

    public override void Load(bool startup) {
        Directory.CreateDirectory(Renders);
        atlas = new Bitmap(Root + "/terrain.png");
        running = true;
        string prefix = Environment.GetEnvironmentVariable("DYNMAPCLASSIC_PREFIX");
        if (String.IsNullOrEmpty(prefix)) prefix = "http://localhost:" + Port + "/";
        if (!prefix.EndsWith("/")) prefix += "/";
        http = new HttpListener(); http.Prefixes.Add(prefix); http.Start();
        httpThread = new Thread(HttpLoop); httpThread.IsBackground = true; httpThread.Start();
        OnLevelAddedEvent.Register(LevelAdded, Priority.Low);
        renderThread = new Thread(RenderAll); renderThread.IsBackground = true; renderThread.Start();
        Logger.Log(LogType.SystemActivity, "[DynmapClassic] Listening at {0}, using configured terrain.png", prefix);
    }
    public override void Unload(bool shutdown) {
        running = false; OnLevelAddedEvent.Unregister(LevelAdded);
        try { http.Stop(); http.Close(); } catch { }
        try { if (renderThread != null) renderThread.Join(3000); } catch { }
        if (atlas != null) atlas.Dispose();
    }
    void LevelAdded(Level level) { if (level != null) StartRender(level.name); }
    void StartRender(string world) { Thread t = new Thread(delegate() { RenderWorld(world); }); t.IsBackground = true; t.Start(); }
    void RenderAll() { string[] worlds=LevelInfo.AllMapNames(),first={"main","redchanit","hell","classic","flat","jesse5576","parkergreatgamer"};foreach(string wanted in first)foreach(string world in worlds)if(world.Equals(wanted,StringComparison.OrdinalIgnoreCase)){if(!running)return;RenderWorld(world);break;}foreach(string world in worlds){if(!running)break;bool done=false;foreach(string wanted in first)if(world.Equals(wanted,StringComparison.OrdinalIgnoreCase)){done=true;break;}if(!done)RenderWorld(world);}Logger.Log(LogType.SystemActivity,"[DynmapClassic] Full render pass complete");}

    void HttpLoop() {
        while (running) try { HttpListenerContext c = http.GetContext(); ThreadPool.QueueUserWorkItem(delegate { Handle(c); }); }
        catch (HttpListenerException) { } catch (ObjectDisposedException) { } catch (Exception ex) { Logger.LogError(ex); }
    }
    void Handle(HttpListenerContext c) {
        try {
            string p = Uri.UnescapeDataString(c.Request.Url.AbsolutePath);
            if (p == "/") { FileResponse(c, Web + "/index.html", "text/html; charset=utf-8"); return; }
            if (p == "/api/state") { Text(c, StateJson(), "application/json; charset=utf-8"); return; }
            if (p.StartsWith("/web/")) { Static(c, p.Substring(5)); return; }
            if (p.StartsWith("/render/")) { RenderImage(c, p); return; }
            if (p.StartsWith("/tiles/")) { Tile(c, p); return; }
            c.Response.StatusCode = 404; Text(c, "Not found", "text/plain");
        } catch (Exception ex) { Logger.LogError(ex); try { c.Response.StatusCode = 500; c.Response.Close(); } catch { } }
    }
    void Static(HttpListenerContext c, string rel) {
        rel = rel.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(Web, rel)), basePath = Path.GetFullPath(Web) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) { c.Response.StatusCode = 403; c.Response.Close(); return; }
        FileResponse(c, full, Mime(full));
    }
    void Tile(HttpListenerContext c, string path) {
        string[] a = path.Split(new[]{'/'}, StringSplitOptions.RemoveEmptyEntries);
        int x, y; if (a.Length != 5 || !int.TryParse(a[3], out x) || !int.TryParse(Path.GetFileNameWithoutExtension(a[4]), out y)) { c.Response.StatusCode=400;c.Response.Close();return; }
        string world=a[1], view=a[2]; bool border=c.Request.QueryString["border"]=="1";
        if (!ValidWorld(world) || (view!="surface"&&view!="flat"&&view!="cave")) { c.Response.StatusCode=404;c.Response.Close();return; }
        string master=MasterPath(world,view,border); if (!File.Exists(master)) RenderWorld(world);
        if (!File.Exists(master)) { c.Response.StatusCode=503;c.Response.Close();return; }
        using (Bitmap src=new Bitmap(master)) using(Bitmap dst=new Bitmap(256,256,PixelFormat.Format32bppArgb)) using(Graphics g=Graphics.FromImage(dst)) {
            g.Clear(Color.Transparent); g.DrawImageUnscaled(src,-x*256,-y*256); using(MemoryStream ms=new MemoryStream()){dst.Save(ms,ImageFormat.Png);Bytes(c,ms.ToArray(),"image/png");}
        }
    }
    void RenderImage(HttpListenerContext c, string path) {
        string[] a=path.Split(new[]{'/'},StringSplitOptions.RemoveEmptyEntries);
        if(a.Length!=3){c.Response.StatusCode=400;c.Response.Close();return;}
        string world=a[1],view=Path.GetFileNameWithoutExtension(a[2]);bool border=c.Request.QueryString["border"]=="1";
        if(!ValidWorld(world)||(view!="surface"&&view!="flat"&&view!="cave")){c.Response.StatusCode=404;c.Response.Close();return;}
        string file=MasterPath(world,view,border);if(!File.Exists(file))RenderWorld(world);FileResponse(c,file,"image/png");
    }
    bool ValidWorld(string world) { foreach(string n in LevelInfo.AllMapNames()) if(n.Equals(world,StringComparison.OrdinalIgnoreCase)) return true; return false; }

    void RenderWorld(string world) {
        lock (renderLock) {
            Level temp=null;
            try {
                Level lvl=LevelInfo.FindExact(world); if(lvl==null){temp=IMapImporter.Decode(LevelInfo.MapPath(world),world,false);lvl=temp;if(lvl!=null)Level.LoadMetadata(lvl);}
                if(lvl==null)return;
                WorldMeta m=new WorldMeta();m.Name=world;m.W=lvl.Width;m.H=lvl.Height;m.L=lvl.Length;
                RenderSet(lvl,m,false); RenderSet(lvl,m,true); lock(metadata){metadata[world]=m;}
            } catch(Exception ex){Logger.LogError("[DynmapClassic] Rendering "+world,ex);} finally {if(temp!=null)temp.Dispose();}
        }
    }
    void RenderSet(Level lvl, WorldMeta m, bool border) {
        int pad=border?Border:0;
        lightHeight=BuildLightHeight(lvl);
        m.SetEnvironment(lvl);
        int renderScale=border?BorderScale:Scale;
        using(Bitmap flat=RenderRay(lvl,pad,"flat",renderScale)) Save(flat,MasterPath(lvl.name,"flat",border));
        using(Bitmap cave=RenderRay(lvl,pad,"cave",renderScale)) Save(cave,MasterPath(lvl.name,"cave",border));
        using(Bitmap iso=RenderRay(lvl,pad,"surface",renderScale)) Save(iso,MasterPath(lvl.name,"surface",border));
        Dictionary<string,Size> set=border?m.Border:m.Normal;
        using(Bitmap b=new Bitmap(MasterPath(lvl.name,"flat",border)))set["flat"]=b.Size;
        using(Bitmap b=new Bitmap(MasterPath(lvl.name,"cave",border)))set["cave"]=b.Size;
        using(Bitmap b=new Bitmap(MasterPath(lvl.name,"surface",border)))set["surface"]=b.Size;
    }
    Bitmap RenderRay(Level l,int pad,string view,int renderScale) {
        Basis q=Basis.For(view); double minU=Double.MaxValue,maxU=Double.MinValue,minV=Double.MaxValue,maxV=Double.MinValue;
        double x0=-pad,x1=l.Width+pad,y0=0,y1=l.Height,z0=-pad,z1=l.Length+pad;
        foreach(double x in new[]{x0,x1})foreach(double y in new[]{y0,y1})foreach(double z in new[]{z0,z1}){double u=q.Ux*x+q.Uy*y+q.Uz*z,v=q.Vx*x+q.Vy*y+q.Vz*z;minU=Math.Min(minU,u);maxU=Math.Max(maxU,u);minV=Math.Min(minV,v);maxV=Math.Max(maxV,v);}
        int width=Math.Max(1,(int)Math.Ceiling((maxU-minU)*renderScale)+2),height=Math.Max(1,(int)Math.Ceiling((maxV-minV)*renderScale)+2);
        Bitmap b=new Bitmap(width,height,PixelFormat.Format32bppArgb);
        BitmapData bd=b.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);int[] pixels=new int[width*height];
        for(int py=0;py<height;py++)for(int px=0;px<width;px++){double u=minU+(px+.5)/renderScale,v=maxV-(py+.5)/renderScale;pixels[py*width+px]=Trace(l,pad,q,u,v,view=="cave").ToArgb();}
        System.Runtime.InteropServices.Marshal.Copy(pixels,0,bd.Scan0,pixels.Length);b.UnlockBits(bd);return b;
    }
    Color Trace(Level l,int pad,Basis q,double u,double v,bool cave) {
        double cx=(l.Width)/2.0,cy=l.Height/2.0,cz=l.Length/2.0;
        double centerD=q.Dx*cx+q.Dy*cy+q.Dz*cz,far=Math.Sqrt(l.Width*l.Width+l.Height*l.Height+l.Length*l.Length)+pad*4+4;
        double ox=q.Ux*u+q.Vx*v+q.Dx*(centerD+far),oy=q.Uy*u+q.Vy*v+q.Dy*(centerD+far),oz=q.Uz*u+q.Vz*v+q.Dz*(centerD+far);
        double dx=-q.Dx,dy=-q.Dy,dz=-q.Dz,t0=0,t1=far*2;
        if(!Clip(ref t0,ref t1,ox,dx,-pad,l.Width+pad)||!Clip(ref t0,ref t1,oy,dy,0,l.Height)||!Clip(ref t0,ref t1,oz,dz,-pad,l.Length+pad))return Color.Transparent;
        double t=t0+1e-7,ex=ox+dx*t,ey=oy+dy*t,ez=oz+dz*t;int x=(int)Math.Floor(ex),y=(int)Math.Floor(ey),z=(int)Math.Floor(ez),axis=1;
        int sx=dx>0?1:-1,sy=dy>0?1:-1,sz=dz>0?1:-1;double tx=Next(ex,x,dx,sx),ty=Next(ey,y,dy,sy),tz=Next(ez,z,dz,sz),dtx=dx==0?Double.PositiveInfinity:Math.Abs(1/dx),dty=dy==0?Double.PositiveInfinity:Math.Abs(1/dy),dtz=dz==0?Double.PositiveInfinity:Math.Abs(1/dz);
        int ar=0,ag=0,ab=0,aa=0;bool caveAir=true;ushort lastId=Block.Air;
        while(t<=t1&&x>=-pad&&x<l.Width+pad&&y>=0&&y<l.Height&&z>=-pad&&z<l.Length+pad){bool outside=x<0||z<0||x>=l.Width||z>=l.Length;ushort id=outside?OutsideBlock(x,y,z,l):l.GetBlock((ushort)x,(ushort)y,(ushort)z);BlockDefinition def=Definition(l,id);bool hidden=CaveHidden(id,def);
            if(cave){if(!hidden){caveAir=false;}else if(!caveAir){int mult=axis==0?224:axis==2?256:160;return CaveColor(y,l.Height,mult);}}
            else if(id!=Block.Air&&def!=null&&def.BlockDraw!=DrawType.Gas){bool sprite=IsSprite(l,id),internalFace=id==lastId&&(def.BlockDraw==DrawType.Translucent||def.BlockDraw==DrawType.Transparent||def.BlockDraw==DrawType.TransparentThick);Color c=Color.Transparent;if(sprite)c=ApplyLight(l,def,x,y,z,1,dx,dy,dz,SampleSprite(def,x,y,z,ox,oy,oz,dx,dy,dz,t,t+Math.Min(tx,Math.Min(ty,tz))));else if(!internalFace){double hx=ox+dx*t,hy=oy+dy*t,hz=oz+dz*t;c=ApplyLight(l,def,x,y,z,axis,dx,dy,dz,Sample(def,axis,dx,dy,dz,hx,hy,hz));}if(c.A>0){int alpha2=c.A*(255-aa)/255,talpha=aa+alpha2;if(talpha>0){ar=(c.R*alpha2+ar*aa)/talpha;ag=(c.G*alpha2+ag*aa)/talpha;ab=(c.B*alpha2+ab*aa)/talpha;aa=talpha;}if(aa>=254)return Color.FromArgb(aa,ar,ag,ab);}}
            lastId=id;
            if(tx<=ty&&tx<=tz){t+=tx;ty-=tx;tz-=tx;tx=dtx;x+=sx;axis=0;}else if(ty<=tz){t+=ty;tx-=ty;tz-=ty;ty=dty;y+=sy;axis=1;}else{t+=tz;tx-=tz;ty-=tz;tz=dtz;z+=sz;axis=2;}
        }return Color.FromArgb(aa,ar,ag,ab);
    }
    static bool Clip(ref double a,ref double b,double o,double d,double lo,double hi){if(Math.Abs(d)<1e-12)return o>=lo&&o<=hi;double p=(lo-o)/d,q=(hi-o)/d;if(p>q){double n=p;p=q;q=n;}a=Math.Max(a,p);b=Math.Min(b,q);return a<=b;}
    static double Next(double p,int cell,double d,int step){if(Math.Abs(d)<1e-12)return Double.PositiveInfinity;return ((step>0?cell+1:cell)-p)/d;}
    BlockDefinition Definition(Level l,ushort id){if(id==Block.Air)return null;BlockDefinition d=l.GetBlockDef(id);ushort core=Block.Convert(id);if(d==null&&core<Block.CPE_COUNT)d=DefaultSet.MakeCustomBlock(core);return d;}
    Color Sample(BlockDefinition d,int axis,double dx,double dy,double dz,double x,double y,double z){ushort tex=axis==1?(dy<0?d.TopTex:d.BottomTex):axis==0?(dx>0?d.LeftTex:d.RightTex):(dz>0?d.FrontTex:d.BackTex);int size=atlas.Width/16,rows=atlas.Height/size;if(tex>=16*rows)return Color.Magenta;double fu,fv;if(axis==0){fu=Frac(z);fv=1-Frac(y);}else if(axis==1){fu=Frac(x);fv=Frac(z);}else{fu=Frac(x);fv=1-Frac(y);}int px=(tex%16)*size+Math.Min(size-1,(int)(fu*size)),py=(tex/16)*size+Math.Min(size-1,(int)(fv*size));Color c=atlas.GetPixel(px,py);if(d.BlockDraw==DrawType.Translucent)c=Color.FromArgb(Math.Min((int)c.A,128),c);return c;}
    bool IsSprite(Level l,ushort id){if(l.GetBlockDef(id)!=null)return l.GetBlockDef(id).Shape==0;ushort core=Block.Convert(id);return core<Block.CPE_COUNT&&DefaultSet.Draw(core)==DrawType.Sprite;}
    Color SampleSprite(BlockDefinition d,int bx,int by,int bz,double ox,double oy,double oz,double dx,double dy,double dz,double enter,double exit){double[] hits=new double[2];hits[0]=Math.Abs(dx-dz)<1e-12?Double.PositiveInfinity:((oz-bz)-(ox-bx))/(dx-dz);hits[1]=Math.Abs(dx+dz)<1e-12?Double.PositiveInfinity:(1-(ox-bx)-(oz-bz))/(dx+dz);Array.Sort(hits);foreach(double h in hits){if(h<enter-1e-7||h>exit+1e-7)continue;double fy=oy+dy*h-by,fx=ox+dx*h-bx;if(fy<0||fy>=1||fx<0||fx>=1)continue;Color c=SampleTexture(d.TopTex,fx,1-fy);if(c.A>0)return c;}return Color.Transparent;}
    Color SampleTexture(ushort tex,double u,double v){int size=atlas.Width/16,rows=atlas.Height/size;if(tex>=16*rows)return Color.Magenta;int px=(tex%16)*size+Math.Min(size-1,Math.Max(0,(int)(u*size))),py=(tex/16)*size+Math.Min(size-1,Math.Max(0,(int)(v*size)));return atlas.GetPixel(px,py);}
    int[] BuildLightHeight(Level l){int[] h=new int[l.Width*l.Length];for(int z=0;z<l.Length;z++)for(int x=0;x<l.Width;x++){int y;for(y=l.Height-1;y>=0;y--){ushort id=l.GetBlock((ushort)x,(ushort)y,(ushort)z);if(!l.LightPasses(id))break;}h[x+z*l.Width]=y;}return h;}
    Color ApplyLight(Level l,BlockDefinition d,int x,int y,int z,int axis,double dx,double dy,double dz,Color src){if(src.A==0||d.FullBright)return src;bool outside=x<0||z<0||x>=l.Width||z>=l.Length;int sampleY=y+(axis==1?(dy<0?1:-1):0);bool lit=outside||sampleY>lightHeight[x+z*l.Width];Color tint=ParseColor(lit?l.Config.LightColor:l.Config.ShadowColor,lit?Color.White:Color.FromArgb(155,155,155));double face=axis==0?.6:axis==2?.8:(dy>0?.5:1);return Color.FromArgb(src.A,Clamp(src.R*tint.R/255.0*face),Clamp(src.G*tint.G/255.0*face),Clamp(src.B*tint.B/255.0*face));}
    static Color ParseColor(string s,Color fallback){if(String.IsNullOrEmpty(s))return fallback;try{return ColorTranslator.FromHtml(s[0]=='#'?s:"#"+s);}catch{return fallback;}}
    static int Clamp(double v){return Math.Max(0,Math.Min(255,(int)v));}
    static double Frac(double v){return v-Math.Floor(v);}
    bool CaveHidden(ushort id,BlockDefinition d){if(id==Block.Air||id==Block.Water||id==Block.StillWater)return true;ushort b=Block.Convert(id);if(b==Block.Log||b==Block.Leaves||b==Block.Glass||b==Block.Snow||b==Block.Ice)return true;return d!=null&&(d.BlockDraw==DrawType.Gas||d.BlockDraw==DrawType.Transparent||d.BlockDraw==DrawType.TransparentThick||d.BlockDraw==DrawType.Translucent);}
    static Color CaveColor(int y,int height,int mult){int sea=height/2,max=Math.Max(1,height-1),r,g,b;if(y<sea){r=0;g=64+192*y/Math.Max(1,sea);b=255-255*y/Math.Max(1,sea);}else{r=255*(y-sea)/Math.Max(1,max-sea);g=255;b=0;}return Color.FromArgb(255,r*mult/256,g*mult/256,b*mult/256);}
    sealed class Basis {public double Ux,Uy,Uz,Vx,Vy,Vz,Dx,Dy,Dz;public static Basis For(string v){if(v=="flat")return new Basis{Uz=-1,Vx=-1,Dy=1};double inc=(v=="cave"?60:30)*Math.PI/180,az=135*Math.PI/180;Basis q=new Basis();q.Ux=-Math.Sin(az);q.Uz=Math.Cos(az);q.Vx=Math.Cos(az)*Math.Sin(inc);q.Vy=Math.Cos(inc);q.Vz=Math.Sin(az)*Math.Sin(inc);q.Dx=q.Uy*q.Vz-q.Uz*q.Vy;q.Dy=q.Uz*q.Vx-q.Ux*q.Vz;q.Dz=q.Ux*q.Vy-q.Uy*q.Vx;return q;}}
    ushort BorderBlock(int x,int z,Level l){return Block.StillWater;}
    ushort OutsideBlock(int x,int y,int z,Level l){int edge=l.GetEdgeLevel(),offset=l.Config.SidesOffset==EnvConfig.ENV_USE_DEFAULT?-2:l.Config.SidesOffset,sides=edge+offset;ushort water=l.Config.HorizonBlock==Block.Invalid?Block.StillWater:l.Config.HorizonBlock,ground=l.Config.EdgeBlock==Block.Invalid?Block.Bedrock:l.Config.EdgeBlock;if(y==edge-1)return water;if(y==sides-1)return ground;bool wall=x==-1||x==l.Width||z==-1||z==l.Length;if(wall&&y>=Math.Min(0,sides)&&y<Math.Max(0,sides))return ground;return Block.Air;}
    void Save(Bitmap b,string path){Directory.CreateDirectory(Path.GetDirectoryName(path));string tmp=path+".tmp";b.Save(tmp,ImageFormat.Png);if(File.Exists(path))File.Delete(path);File.Move(tmp,path);}
    string MasterPath(string w,string v,bool border){return Renders+"/"+w+"/"+v+(border?"-border":"")+".png";}

    string StateJson(){StringBuilder s=new StringBuilder("{\"worlds\":[");string[] worlds=LevelInfo.AllMapNames();for(int i=0;i<worlds.Length;i++){WorldMeta m;lock(metadata){metadata.TryGetValue(worlds[i],out m);}if(m==null){Vec3U16 d=IMapImporter.GetFor(LevelInfo.MapPath(worlds[i])).ReadDimensions(LevelInfo.MapPath(worlds[i]));m=new WorldMeta{Name=worlds[i],W=d.X,H=d.Y,L=d.Z};m.SetEnvironment(LevelInfo.GetConfig(worlds[i]));}if(i>0)s.Append(',');s.Append(m.Json());}s.Append("],\"players\":[");Player[] ps=PlayerInfo.Online.Items;bool first=true;foreach(Player p in ps){if(p==null||p.level==null)continue;if(!first)s.Append(',');first=false;AppendEntity(s,p.name,p.name,p.level.name,p.Pos);}s.Append("],\"bots\":[");first=true;foreach(string world in worlds){Level lvl=LevelInfo.FindExact(world);if(lvl!=null){PlayerBot[] bots=lvl.Bots.Items;foreach(PlayerBot bot in bots){if(!first)s.Append(',');first=false;AppendEntity(s,bot.name,bot.SkinName,world,bot.Pos);}}else{string path="extra/bots/"+world+".json";if(!File.Exists(path))continue;try{JsonArray arr=(JsonArray)new JsonReader(File.ReadAllText(path)).Parse();foreach(object raw in arr){JsonObject o=raw as JsonObject;if(o==null)continue;if(!first)s.Append(',');first=false;string name=Val(o,"Name"),skin=Val(o,"Skin");int x=Num(o,"X"),y=Num(o,"Y"),z=Num(o,"Z");AppendEntity(s,name,skin,world,new Position(x,y,z));}}catch(Exception ex){Logger.LogError("[DynmapClassic] Reading bots for "+world,ex);}}}return s.Append("],\"mobs\":").Append(MobsJson()).Append('}').ToString();}
    static string MobsJson(){try{foreach(Assembly a in AppDomain.CurrentDomain.GetAssemblies()){Type t=a.GetType("AmbientMobs",false);if(t==null)continue;MethodInfo m=t.GetMethod("DynmapJson",BindingFlags.Public|BindingFlags.Static);if(m!=null)return (string)m.Invoke(null,null);}}catch(Exception ex){Logger.LogError("[DynmapClassic] Reading ambient mobs",ex);}return "[]";}
    void AppendEntity(StringBuilder s,string name,string skin,string world,Position pos){s.AppendFormat(CultureInfo.InvariantCulture,"{{\"name\":\"{0}\",\"skin\":\"{1}\",\"world\":\"{2}\",\"x\":{3},\"y\":{4},\"z\":{5}}}",J(name),J(String.IsNullOrEmpty(skin)?name:skin),J(world),pos.X/32.0,pos.Y/32.0,pos.Z/32.0);}
    static string Val(JsonObject o,string key){object v;return o.TryGetValue(key,out v)&&v!=null?v.ToString():"";}
    static int Num(JsonObject o,string key){int n;return Int32.TryParse(Val(o,key),NumberStyles.Integer,CultureInfo.InvariantCulture,out n)?n:0;}
    static string J(string x){return (x??"").Replace("\\","\\\\").Replace("\"","\\\"");}
    void FileResponse(HttpListenerContext c,string f,string mime){if(!File.Exists(f)){c.Response.StatusCode=404;c.Response.Close();return;}Bytes(c,File.ReadAllBytes(f),mime);}
    void Text(HttpListenerContext c,string s,string mime){Bytes(c,Encoding.UTF8.GetBytes(s),mime);}
    void Bytes(HttpListenerContext c,byte[] b,string mime){c.Response.ContentType=mime;c.Response.ContentLength64=b.Length;c.Response.OutputStream.Write(b,0,b.Length);c.Response.Close();}
    string Mime(string f){string e=Path.GetExtension(f).ToLowerInvariant();return e==".js"?"application/javascript":e==".css"?"text/css":e==".png"?"image/png":e==".html"?"text/html":"application/octet-stream";}
    sealed class WorldMeta {public string Name;public int W,H,L;public string Sky="99CCFF",Fog="FFFFFF",Light="FFFFFF",Shadow="9B9B9B";public Dictionary<string,Size> Normal=new Dictionary<string,Size>(),Border=new Dictionary<string,Size>();
        public void SetEnvironment(Level l){SetEnvironment(l.Config);}
        public void SetEnvironment(LevelConfig c){Sky=Env(c.SkyColor,"99CCFF");Fog=Env(c.FogColor,"FFFFFF");Light=Env(c.LightColor,"FFFFFF");Shadow=Env(c.ShadowColor,"9B9B9B");}
        static string Env(string s,string d){if(String.IsNullOrEmpty(s))return d;return s.TrimStart('#');}
        string Sizes(Dictionary<string,Size> d,bool border){int pad=border?DynmapClassic.Border:0,scale=border?DynmapClassic.BorderScale:Scale;Size flat=d.ContainsKey("flat")?d["flat"]:new Size((W+pad*2)*scale,(L+pad*2)*scale);Size iso=d.ContainsKey("surface")?d["surface"]:flat;return string.Format(CultureInfo.InvariantCulture,"{{\"flat\":{{\"width\":{0},\"height\":{1}}},\"cave\":{{\"width\":{0},\"height\":{1}}},\"surface\":{{\"width\":{2},\"height\":{3}}}}}",flat.Width,flat.Height,iso.Width,iso.Height);}
        public string Json(){return string.Format(CultureInfo.InvariantCulture,"{{\"name\":\"{0}\",\"width\":{1},\"height\":{2},\"length\":{3},\"sky\":\"#{6}\",\"fog\":\"#{7}\",\"light\":\"#{8}\",\"shadow\":\"#{9}\",\"views\":{4},\"borderViews\":{5}}}",J(Name),W,H,L,Sizes(Normal,false),Sizes(Border,true),Sky,Fog,Light,Shadow);}}
}
