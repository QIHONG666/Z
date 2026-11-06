using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ZflDesktopPet {
    public class AnimationSpec {
        public string name { get; set; }
        public int row { get; set; }
        public int count { get; set; }
        public int[] durations { get; set; }
    }
    public class SpriteManifest {
        public string name { get; set; }
        public string version { get; set; }
        public int cellWidth { get; set; }
        public int cellHeight { get; set; }
        public int columns { get; set; }
        public AnimationSpec[] states { get; set; }
    }
    public class Preferences {
        public double Scale { get; set; }
        public bool Wander { get; set; }
        public bool Topmost { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool HasPosition { get; set; }
        public Preferences() { Scale=1.0; Wander=true; Topmost=true; }
    }
    internal static class Native {
        [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int x,y; public POINT(int a,int b){x=a;y=b;} }
        [StructLayout(LayoutKind.Sequential)] internal struct SIZE { public int cx,cy; public SIZE(int a,int b){cx=a;cy=b;} }
        [StructLayout(LayoutKind.Sequential,Pack=1)] internal struct BLEND { public byte op,flags,alpha,format; }
        [DllImport("user32.dll",SetLastError=true)] internal static extern bool UpdateLayeredWindow(IntPtr w,IntPtr dc,ref POINT dst,ref SIZE size,IntPtr src,ref POINT origin,int color,ref BLEND blend,int flags);
        [DllImport("user32.dll")] internal static extern IntPtr GetDC(IntPtr w);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr w,IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] internal static extern IntPtr GetWindowLongPtr(IntPtr w,int n);
        [DllImport("user32.dll")] internal static extern int GetGuiResources(IntPtr process,int kind);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] internal static extern IntPtr FindWindow(string cls,string title);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr w,int msg,IntPtr a,IntPtr b);
    }

    public sealed class PetForm : Form {
        public const string WindowTitle="zfl桌宠";
        public const int BringBackMessage=0x8000+17;
        readonly SpriteManifest manifest;
        readonly Bitmap atlas;
        readonly Icon petIcon;
        readonly Dictionary<string,Bitmap[]> cache=new Dictionary<string,Bitmap[]>();
        readonly Dictionary<string,AnimationSpec> specs=new Dictionary<string,AnimationSpec>();
        readonly Preferences preferences;
        readonly string configPath;
        readonly bool testing;
        readonly Random random=new Random();
        readonly Stopwatch stopwatch=Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        readonly ContextMenuStrip menu=new ContextMenuStrip();
        readonly NotifyIcon tray=new NotifyIcon();
        ToolStripMenuItem wanderItem,topItem,waveItem,jumpItem;
        string animation="idle";
        int frame;
        double elapsedMs,clock,lastTick,nextWander=12,walkUntil;
        bool once,dragging,suppressClick,cleaned;
        Point dragMouse,dragWindow;
        int dragDistance;
        public int RenderCount { get; private set; }

        static Stream Resource(string name) {
            Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if(stream==null) throw new InvalidDataException("Missing embedded resource: "+name);
            return stream;
        }
        public PetForm(string configDirectory,bool selfTest) {
            testing=selfTest;
            configPath=Path.Combine(configDirectory,"settings.json");
            preferences=selfTest ? new Preferences() : LoadPreferences(configPath);
            using(Stream s=Resource("Zfl.Animations.json"))
            using(StreamReader reader=new StreamReader(s))
                manifest=new JavaScriptSerializer().Deserialize<SpriteManifest>(reader.ReadToEnd());
            using(Stream s=Resource("Zfl.Sprites.png"))
            using(Bitmap image=new Bitmap(s)) atlas=new Bitmap(image);
            using(Stream s=Resource("Zfl.Pet.ico")) petIcon=new Icon(s);
            if(manifest.cellWidth!=288 || manifest.cellHeight!=352 || manifest.states.Length!=5 || atlas.Width!=manifest.columns*manifest.cellWidth || atlas.Height!=5*manifest.cellHeight)
                throw new InvalidDataException("Invalid sprite manifest dimensions.");
            foreach(AnimationSpec spec in manifest.states) {
                if(spec.count<1 || spec.count>manifest.columns || spec.durations.Length!=spec.count) throw new InvalidDataException("Invalid frame count.");
                specs.Add(spec.name,spec);
            }
            Text=WindowTitle;
            FormBorderStyle=FormBorderStyle.None;
            ShowInTaskbar=false;
            AutoScaleMode=AutoScaleMode.None;
            StartPosition=FormStartPosition.Manual;
            TopMost=preferences.Topmost;
            Icon=petIcon;
            DoubleBuffered=true;
            BuildMenu();
            ContextMenuStrip=menu;
            BuildFrames();
            if(preferences.HasPosition) Location=Clamp(new Point(preferences.X,preferences.Y)); else MoveToCorner();
            tray.Icon=petIcon;
            tray.Text=WindowTitle+" · 右键打开菜单";
            tray.ContextMenuStrip=menu;
            tray.DoubleClick+=delegate { MoveToCorner(); BeginAnimation("waving",true); };
            tray.Visible=!testing;
            timer.Interval=30;
            timer.Tick+=delegate {
                double now=stopwatch.Elapsed.TotalSeconds;
                double delta=Math.Min(.15,Math.Max(0,now-lastTick)); lastTick=now;
                Advance(delta);
            };
        }
        protected override CreateParams CreateParams {
            get { CreateParams p=base.CreateParams; p.ExStyle|=0x80000|0x80|0x08000000; return p; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnShown(EventArgs e) {
            base.OnShown(e); lastTick=stopwatch.Elapsed.TotalSeconds; RenderFrame(); timer.Start();
        }
        public Point Clamp(Point desired) {
            Rectangle work=Screen.FromPoint(new Point(desired.X+Width/2,desired.Y+Height/2)).WorkingArea;
            return new Point(Math.Max(work.Left,Math.Min(desired.X,work.Right-Width)),Math.Max(work.Top,Math.Min(desired.Y,work.Bottom-Height)));
        }
        void MoveToCorner() {
            Rectangle work=Screen.FromPoint(Cursor.Position).WorkingArea;
            Location=Clamp(new Point(work.Right-Width-24,work.Bottom-Height-8));
            if(IsHandleCreated && cache.Count>0) RenderFrame();
        }
        void BuildMenu() {
            ToolStripMenuItem heading=new ToolStripMenuItem("zfl · 白裙小伙伴"); heading.Enabled=false; menu.Items.Add(heading);
            menu.Items.Add(new ToolStripSeparator());
            waveItem=new ToolStripMenuItem("挥挥手（单击）",null,delegate { BeginAnimation("waving",true); }); menu.Items.Add(waveItem);
            jumpItem=new ToolStripMenuItem("跳一下（双击）",null,delegate { BeginAnimation("jumping",true); }); menu.Items.Add(jumpItem);
            wanderItem=new ToolStripMenuItem("自动走动"); wanderItem.Checked=preferences.Wander; wanderItem.CheckOnClick=true;
            wanderItem.CheckedChanged+=delegate { preferences.Wander=wanderItem.Checked; if(!preferences.Wander) BeginAnimation("idle",false); nextWander=clock+10; }; menu.Items.Add(wanderItem);
            topItem=new ToolStripMenuItem("置顶显示"); topItem.Checked=preferences.Topmost; topItem.CheckOnClick=true;
            topItem.CheckedChanged+=delegate { TopMost=topItem.Checked; preferences.Topmost=TopMost; }; menu.Items.Add(topItem);
            ToolStripMenuItem sizes=new ToolStripMenuItem("桌宠大小");
            double[] factors={.75,1.0,1.25}; string[] labels={"小","标准","大"};
            for(int i=0;i<factors.Length;i++) { double value=factors[i]; sizes.DropDownItems.Add(labels[i],null,delegate { ChangeScale(value); }); } menu.Items.Add(sizes);
            menu.Items.Add("回到屏幕角落",null,delegate { MoveToCorner(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("关于与操作",null,delegate { MessageBox.Show("zfl桌宠 1.0.1\n\n拖动：按住人物移动\n单击：挥手\n双击：跳跃\n右键：设置或退出\n托盘图标：右键菜单，双击找回桌宠\n\n资源已内置，可离线运行。",WindowTitle,MessageBoxButtons.OK,MessageBoxIcon.Information); });
            menu.Items.Add("退出",null,delegate { Close(); });
        }
        void BuildFrames() {
            foreach(Bitmap[] frames in cache.Values) foreach(Bitmap image in frames) image.Dispose(); cache.Clear();
            int w=(int)Math.Round(manifest.cellWidth*preferences.Scale),h=(int)Math.Round(manifest.cellHeight*preferences.Scale);
            ClientSize=new Size(w,h);
            foreach(AnimationSpec spec in manifest.states) {
                Bitmap[] images=new Bitmap[spec.count];
                for(int i=0;i<spec.count;i++) {
                    Bitmap image=new Bitmap(w,h,PixelFormat.Format32bppPArgb);
                    using(Graphics g=Graphics.FromImage(image)) {
                        g.CompositingMode=CompositingMode.SourceCopy;
                        g.InterpolationMode=InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode=PixelOffsetMode.HighQuality;
                        g.DrawImage(atlas,new Rectangle(0,0,w,h),new Rectangle(i*manifest.cellWidth,spec.row*manifest.cellHeight,manifest.cellWidth,manifest.cellHeight),GraphicsUnit.Pixel);
                    }
                    images[i]=image;
                }
                cache.Add(spec.name,images);
            }
        }
        void ChangeScale(double scale) {
            preferences.Scale=scale; BuildFrames(); frame=0; Location=Clamp(Location); if(IsHandleCreated) RenderFrame();
        }
        void BeginAnimation(string name,bool single) {
            animation=name; once=single; frame=0; elapsedMs=0;
            if(IsHandleCreated && cache.Count>0) RenderFrame();
        }
        public void Advance(double delta) {
            clock+=delta;
            if(dragging) return;
            bool changed=false;
            if(preferences.Wander && !once && animation=="idle" && clock>=nextWander) {
                BeginAnimation(random.Next(2)==0?"running-right":"running-left",false); walkUntil=clock+3+random.NextDouble()*3; changed=true;
            }
            if(!once && animation.StartsWith("running-")) {
                if(!preferences.Wander || clock>=walkUntil) {
                    BeginAnimation("idle",false); nextWander=clock+8+random.NextDouble()*12;
                } else {
                    int direction=animation=="running-right"?1:-1;
                    int step=Math.Max(1,(int)Math.Round(48*preferences.Scale*delta));
                    Point requested=new Point(Left+direction*step,Top),bounded=Clamp(requested);
                    Location=bounded;
                    if(bounded.X!=requested.X) BeginAnimation(direction>0?"running-left":"running-right",false);
                }
                changed=true;
            }
            elapsedMs+=delta*1000;
            while(elapsedMs>=specs[animation].durations[frame]) {
                elapsedMs-=specs[animation].durations[frame]; frame++; changed=true;
                if(frame>=specs[animation].count) {
                    if(once) { animation="idle"; once=false; frame=0; elapsedMs=0; nextWander=clock+10; }
                    else frame=0;
                }
            }
            if(changed && IsHandleCreated) RenderFrame();
        }
        Bitmap CurrentFrame { get { return cache[animation][frame]; } }
        bool Hit(Point p) { return p.X>=0 && p.Y>=0 && p.X<Width && p.Y<Height && CurrentFrame.GetPixel(p.X,p.Y).A>16; }
        public void RenderFrame() {
            Bitmap bitmap=CurrentFrame;
            IntPtr screen=Native.GetDC(IntPtr.Zero),memory=Native.CreateCompatibleDC(screen),handle=IntPtr.Zero,old=IntPtr.Zero;
            try {
                handle=bitmap.GetHbitmap(Color.FromArgb(0)); old=Native.SelectObject(memory,handle);
                Native.POINT destination=new Native.POINT(Left,Top),origin=new Native.POINT(0,0);
                Native.SIZE size=new Native.SIZE(bitmap.Width,bitmap.Height);
                Native.BLEND blend=new Native.BLEND(); blend.alpha=255; blend.format=1;
                if(!Native.UpdateLayeredWindow(Handle,screen,ref destination,ref size,memory,ref origin,0,ref blend,2))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"UpdateLayeredWindow failed");
                RenderCount++;
            } finally {
                if(old!=IntPtr.Zero) Native.SelectObject(memory,old);
                if(handle!=IntPtr.Zero) Native.DeleteObject(handle);
                Native.DeleteDC(memory); Native.ReleaseDC(IntPtr.Zero,screen);
            }
        }
        protected override void WndProc(ref Message m) {
            if(m.Msg==0x21) { m.Result=new IntPtr(3); return; }
            if(m.Msg==0x84 && cache.Count>0) {
                long raw=m.LParam.ToInt64(); Point point=PointToClient(new Point((short)(raw&0xffff),(short)((raw>>16)&0xffff)));
                m.Result=new IntPtr(Hit(point)?1:-1); return;
            }
            if(m.Msg==BringBackMessage) { MoveToCorner(); BeginAnimation("waving",true); return; }
            base.WndProc(ref m);
            if((m.Msg==0x7e || m.Msg==0x2e0) && cache.Count>0) { Location=Clamp(Location); if(IsHandleCreated) RenderFrame(); }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            base.OnMouseDown(e);
            if(e.Button==MouseButtons.Left) { dragging=true; Capture=true; dragMouse=Cursor.Position; dragWindow=Location; dragDistance=0; BeginAnimation("idle",false); }
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            base.OnMouseMove(e);
            if(dragging) { Point p=Cursor.Position; int dx=p.X-dragMouse.X,dy=p.Y-dragMouse.Y; dragDistance=Math.Max(dragDistance,Math.Abs(dx)+Math.Abs(dy)); Location=Clamp(new Point(dragWindow.X+dx,dragWindow.Y+dy)); RenderFrame(); }
        }
        protected override void OnMouseUp(MouseEventArgs e) {
            base.OnMouseUp(e);
            if(e.Button==MouseButtons.Left) { dragging=false; Capture=false; nextWander=clock+12; if(dragDistance<6 && !suppressClick) BeginAnimation("waving",true); suppressClick=false; }
        }
        protected override void OnMouseDoubleClick(MouseEventArgs e) {
            base.OnMouseDoubleClick(e); if(e.Button==MouseButtons.Left) { suppressClick=true; dragging=false; Capture=false; BeginAnimation("jumping",true); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if(!Capture) dragging=false; }
        static Preferences LoadPreferences(string path) {
            try {
                Preferences p=new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(path));
                if(p==null || (p.Scale!=.75 && p.Scale!=1 && p.Scale!=1.25)) return new Preferences(); return p;
            } catch { return new Preferences(); }
        }
        void SavePreferences() {
            preferences.X=Left; preferences.Y=Top; preferences.HasPosition=true;
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
            File.WriteAllText(configPath+".tmp",new JavaScriptSerializer().Serialize(preferences));
            if(File.Exists(configPath)) File.Delete(configPath);
            File.Move(configPath+".tmp",configPath);
        }
        protected override void OnFormClosing(FormClosingEventArgs e) { timer.Stop(); try { SavePreferences(); } catch { } base.OnFormClosing(e); }
        protected override void Dispose(bool disposing) {
            if(disposing && !cleaned) {
                cleaned=true; timer.Dispose(); tray.Visible=false; tray.Dispose(); menu.Dispose();
                foreach(Bitmap[] images in cache.Values) foreach(Bitmap image in images) image.Dispose(); cache.Clear(); atlas.Dispose(); petIcon.Dispose();
            }
            base.Dispose(disposing);
        }
        public Dictionary<string,object> SelfTest(string screenshot) {
            Dictionary<string,object> results=new Dictionary<string,object>();
            Action<string,bool> check=delegate(string label,bool value) { results[label]=value; if(!value) throw new InvalidOperationException("Self-test failed: "+label); };
            IntPtr nativeHandle=Handle;
            check("embedded_31_frames",specs["idle"].count+specs["running-right"].count+specs["running-left"].count+specs["waving"].count+specs["jumping"].count==31);
            check("native_layered_window",(Native.GetWindowLongPtr(nativeHandle,-20).ToInt64()&0x80000)!=0);
            RenderFrame(); check("native_alpha_render",RenderCount>0);
            check("transparent_corner_passthrough",!Hit(new Point(0,0)));
            bool bodyHit=false; for(int y=0;y<Height && !bodyHit;y+=4) for(int x=0;x<Width && !bodyHit;x+=4) bodyHit=Hit(new Point(x,y));
            check("opaque_character_hit_test",bodyHit);
            waveItem.PerformClick(); check("click_wave",animation=="waving" && once); Advance(2); check("wave_returns_to_idle",animation=="idle");
            jumpItem.PerformClick(); check("double_click_jump",animation=="jumping" && once); Advance(2); check("jump_returns_to_idle",animation=="idle");
            Rectangle work=Screen.PrimaryScreen.WorkingArea; Location=new Point(work.Left+30,work.Top+30);
            BeginAnimation("running-right",false); walkUntil=clock+6; int oldX=Left; Advance(.1); check("walk_moves_window",Left>oldX);
            wanderItem.PerformClick(); check("roaming_can_pause",!preferences.Wander && animation=="idle"); oldX=Left; Advance(3); check("paused_position_stable",Left==oldX);
            bool oldTop=TopMost; topItem.PerformClick(); check("topmost_toggle",TopMost!=oldTop); topItem.PerformClick();
            ChangeScale(.75); check("small_size",Width==216 && Height==264); ChangeScale(1.25); check("large_size",Width==360 && Height==440); ChangeScale(1);
            Point bounded=Clamp(new Point(work.Right+500,work.Bottom+500)); check("screen_bounds",bounded.X+Width<=work.Right && bounded.Y+Height<=work.Bottom);
            int before=Native.GetGuiResources(Process.GetCurrentProcess().Handle,0);
            for(int i=0;i<80;i++) RenderFrame();
            int after=Native.GetGuiResources(Process.GetCurrentProcess().Handle,0);
            check("gdi_handles_released",after-before<=4);
            SavePreferences(); Preferences restored=LoadPreferences(configPath); check("settings_roundtrip",restored.HasPosition && restored.Scale==preferences.Scale && restored.Wander==preferences.Wander);
            if(!String.IsNullOrEmpty(screenshot)) CurrentFrame.Save(screenshot,ImageFormat.Png);
            results["window_title"]=Text; results["pet_name"]=manifest.name; results["product_name"]=FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductName; results["render_calls"]=RenderCount; results["gdi_handle_delta"]=after-before; results["process_64_bit"]=Environment.Is64BitProcess; results["ok"]=true;
            return results;
        }
    }
    public static class Program {
        [STAThread] public static int Main(string[] args) {
            bool test=Array.IndexOf(args,"--self-test")>=0;
            string config=Value(args,"--config-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ZflDeskPet");
            string report=Value(args,"--report"),screenshot=Value(args,"--screenshot");
            try {
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { Native.SetProcessDPIAware(); }
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                if(test) {
                    using(PetForm form=new PetForm(config,true)) {
                        Dictionary<string,object> result=form.SelfTest(screenshot);
                        if(report!=null) File.WriteAllText(report,new JavaScriptSerializer().Serialize(result));
                    }
                    return 0;
                }
                bool created;
                using(Mutex mutex=new Mutex(true,@"Local\ZflDesktopPet",out created)) {
                    if(!created) { IntPtr window=Native.FindWindow(null,PetForm.WindowTitle); if(window!=IntPtr.Zero) Native.PostMessage(window,PetForm.BringBackMessage,IntPtr.Zero,IntPtr.Zero); return 0; }
                    try { using(PetForm form=new PetForm(config,false)) Application.Run(form); }
                    finally { mutex.ReleaseMutex(); }
                }
                return 0;
            } catch(Exception ex) {
                if(report!=null) File.WriteAllText(report,new JavaScriptSerializer().Serialize(new { ok=false,error=ex.ToString() }));
                if(!test) MessageBox.Show("桌宠未能启动：\n"+ex.Message,"zfl桌宠",MessageBoxButtons.OK,MessageBoxIcon.Error);
                return 1;
            }
        }
        static string Value(string[] args,string key) { int i=Array.IndexOf(args,key); return i>=0 && i+1<args.Length?args[i+1]:null; }
    }
}
