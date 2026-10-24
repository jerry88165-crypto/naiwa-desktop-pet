using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Serialization;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace NaiwaDesktopPet {
    // Keep behavior independent of the window so the shipped executable can test it.
    public sealed class PetBehavior {
        public const int Idle = 1, LastPose = 8, Laugh = 9;
        public int Pose = Idle;
        public int ClickIndex, DragIndex;
        public bool Dragging;
        public double ReturnAt = Double.PositiveInfinity;
        public double LaughDuration = 4.2;
        double dragNext;
        public int Click(double now) {
            Pose = 2 + ClickIndex; ClickIndex = (ClickIndex + 1) % 8;
            ReturnAt = now + (Pose == Laugh ? LaughDuration : 3.0); return Pose;
        }
        public void Select(int pose, double now) {
            Pose = pose; Dragging = false;
            ReturnAt = pose == Idle ? Double.PositiveInfinity : now + (pose == Laugh ? LaughDuration : 3.0);
        }
        public void BeginDrag(double now) {
            Dragging = true; Pose = 2 + DragIndex; DragIndex = (DragIndex + 1) % 7;
            dragNext = now + .24; ReturnAt = Double.PositiveInfinity;
        }
        public void StepDrag(double now) {
            if (Dragging && now >= dragNext) {
                Pose = 2 + DragIndex; DragIndex = (DragIndex + 1) % 7; dragNext = now + .24;
            }
        }
        public void EndDrag(double now) { Dragging = false; ReturnAt = now + 1.4; }
        public void Tick(double now) { if (!Dragging && now >= ReturnAt) Select(Idle, now); }
        public static Point Clamp(Point p, Size window, IList<Rect> screens) {
            if (screens.Count == 0) return p;
            Rect pet = new Rect(p, window); Rect best = screens[0]; double score = -1;
            Point center = new Point(p.X + window.Width / 2, p.Y + window.Height / 2);
            double nearest = Double.PositiveInfinity;
            foreach (Rect screen in screens) {
                Rect overlap = Rect.Intersect(screen, pet);
                double area = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height;
                double dx = center.X - Math.Max(screen.Left, Math.Min(center.X, screen.Right));
                double dy = center.Y - Math.Max(screen.Top, Math.Min(center.Y, screen.Bottom));
                double distance = dx * dx + dy * dy;
                if (area > score || (area == score && distance < nearest)) {
                    best = screen; score = area; nearest = distance;
                }
            }
            double x = Math.Max(best.Left, Math.Min(p.X, best.Right - window.Width));
            double y = Math.Max(best.Top, Math.Min(p.Y, best.Bottom - window.Height));
            return new Point(x, y);
        }
    }

    public sealed class PetAssets {
        readonly Dictionary<int, BitmapSource> poses = new Dictionary<int, BitmapSource>();
        readonly Dictionary<int, byte[]> pixels = new Dictionary<int, byte[]>(); // A8 only.
        readonly Dictionary<BitmapSource, byte[]> framePixels = new Dictionary<BitmapSource, byte[]>(); // Static A8 only.
        BitmapSource hitFrameA, hitFrameB;
        byte[] hitPixelsA, hitPixelsB;
        int? uniqueLaughFrames;
        public readonly List<BitmapSource> LaughFrames = new List<BitmapSource>();
        public double LaughFps = 1.0 / .14, LaughDuration = 4.2;
        public bool LaughSourceMotion;
        bool? explicitLaughBlend;
        public bool BlendLaughFrames { get { return explicitLaughBlend.HasValue ? explicitLaughBlend.Value : !LaughSourceMotion; } }
        public bool HasLaughSound;
        string cachedLaughSound;
        internal bool SoundWasExtracted { get { return cachedLaughSound != null; } }
        int stripFrameCount = 4;
        bool preserveLaughCanvas;
        public PetAssets() {
            for (int i = 1; i <= PetBehavior.LastPose; i++) Add(i, Trim(Load("pose" + i + ".png", true)));
            List<BitmapSource> cells = LoadLaughSequence();
            if (cells.Count > 0) { LaughFps = 12; LaughSourceMotion = true; }
            ReadAnimationSettings("laugh_animation.txt");
            ReadAnimationSettings("laugh_frames/animation.txt");
            BitmapSource laugh = Load("laugh.png", false);
            BitmapSource strip = Load("laugh_strip.png", false);
            if (cells.Count == 0 && strip != null) {
                if (strip.PixelWidth % stripFrameCount != 0) throw new InvalidDataException("Laugh strip width must divide evenly by stripFrames.");
                for (int i = 0; i < stripFrameCount; i++) {
                    CroppedBitmap frame = new CroppedBitmap(strip, new Int32Rect(i * strip.PixelWidth / stripFrameCount, 0, strip.PixelWidth / stripFrameCount, strip.PixelHeight));
                    frame.Freeze(); cells.Add(frame);
                }
            }
            if (cells.Count > 0) PrepareLaughFrames(cells);
            if (LaughFrames.Count > 0) laugh = LaughFrames[0];
            else if (laugh != null) laugh = Trim(laugh);
            if (laugh == null) throw new FileNotFoundException("Missing laugh frame sequence, laugh.png, or laugh_strip.png.");
            Add(PetBehavior.Laugh, laugh);
            if (LaughFrames.Count == 0) LaughFrames.Add(laugh);
            using (Stream sound = OpenAsset("laugh_sound.mp4")) HasLaughSound = sound != null && sound.Length > 0;
        }
        byte[] FramePixels(BitmapSource bitmap) {
            byte[] cached;
            if (framePixels.TryGetValue(bitmap, out cached)) return cached;
            if (Object.ReferenceEquals(bitmap, hitFrameA)) return hitPixelsA;
            if (Object.ReferenceEquals(bitmap, hitFrameB)) return hitPixelsB;
            // Only current and previous hit-test frames need managed alpha copies.
            hitFrameB = hitFrameA; hitPixelsB = hitPixelsA;
            hitFrameA = bitmap; hitPixelsA = GetAlpha(bitmap); return hitPixelsA;
        }
        public int LaughFrameIndexAt(double seconds) {
            int index = (int)Math.Floor(Math.Max(0, seconds) * LaughFps + 1e-9);
            return LaughSourceMotion ? Math.Min(index, LaughFrames.Count - 1) : index % LaughFrames.Count;
        }
        public int UniqueLaughFrameCount() {
            if (uniqueLaughFrames.HasValue) return uniqueLaughFrames.Value;
            HashSet<string> fingerprints = new HashSet<string>(StringComparer.Ordinal);
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                foreach (BitmapSource frame in LaughFrames) fingerprints.Add(Convert.ToBase64String(sha.ComputeHash(GetPixels(frame))));
            uniqueLaughFrames = fingerprints.Count; return fingerprints.Count;
        }
        public string GetLaughSoundPath() {
            if (cachedLaughSound != null) return cachedLaughSound;
            using (Stream sound = OpenAsset("laugh_sound.mp4")) {
                if (sound == null) return null;
                string hash;
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(sound)).Replace("-", "").Substring(0, 20);
                string folder = Path.Combine(PetSettings.Folder, "cache"); Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "laugh-" + hash + ".mp4");
                if (!File.Exists(path)) {
                    sound.Position = 0;
                    using (FileStream output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) sound.CopyTo(output);
                }
                cachedLaughSound = path; return path;
            }
        }
        void ReadAnimationSettings(string name) {
            using (Stream stream = OpenAsset(name)) {
                if (stream == null) return;
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8)) {
                    string line;
                    while ((line = reader.ReadLine()) != null) {
                        string[] pair = line.Split(new char[] { '=' }, 2); if (pair.Length != 2) continue;
                        string key = pair[0].Trim().ToLowerInvariant(), value = pair[1].Trim().ToLowerInvariant();
                        double number;
                        if (Double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number)) {
                            if (key == "fps" && number >= 1 && number <= 60) LaughFps = number;
                            if (key == "duration" && number >= 1 && number <= 30) LaughDuration = number;
                            if (key == "stripframes" && number >= 1 && number <= 120 && number == Math.Floor(number)) stripFrameCount = (int)number;
                        }
                        if (key == "crop") preserveLaughCanvas = value == "none" || value == "preserve";
                        if (key == "motion") LaughSourceMotion = value == "source" || value == "frames";
                        if (key == "blend") {
                            if (value == "none" || value == "off") explicitLaughBlend = false;
                            else if (value == "crossfade" || value == "on") explicitLaughBlend = true;
                            else explicitLaughBlend = null;
                        }
                    }
                }
            }
        }
        static List<BitmapSource> LoadLaughSequence() {
            const string prefix = "Naiwa.Assets.laugh_frames.";
            List<string> names = new List<string>();
            foreach (string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (resource.StartsWith(prefix + "frame", StringComparison.Ordinal) && resource.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) names.Add(resource.Substring(prefix.Length));
            if (names.Count == 0) {
                string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", "laugh_frames");
                if (Directory.Exists(folder)) foreach (string path in Directory.GetFiles(folder, "frame*.png")) names.Add(Path.GetFileName(path));
            }
            names.Sort(StringComparer.Ordinal); List<BitmapSource> frames = new List<BitmapSource>();
            foreach (string name in names) frames.Add(Load("laugh_frames/" + name, true));
            return frames;
        }
        void PrepareLaughFrames(List<BitmapSource> cells) {
            int width = cells[0].PixelWidth, height = cells[0].PixelHeight;
            int left = width, top = height, right = 0, bottom = 0;
            foreach (BitmapSource frame in cells) {
                if (frame.PixelWidth != width || frame.PixelHeight != height) throw new InvalidDataException("Laugh frames must share one canvas size and baseline.");
                Int32Rect bounds = AlphaBounds(frame); left = Math.Min(left, bounds.X); top = Math.Min(top, bounds.Y);
                right = Math.Max(right, bounds.X + bounds.Width); bottom = Math.Max(bottom, bounds.Y + bounds.Height);
            }
            // Shared crop keeps motion, scale, and baseline consistent throughout the source clip.
            Int32Rect shared = preserveLaughCanvas ? new Int32Rect(0, 0, width, height) : new Int32Rect(left, top, right - left, bottom - top);
            foreach (BitmapSource cell in cells) { CroppedBitmap cropped = new CroppedBitmap(cell, shared); cropped.Freeze(); LaughFrames.Add(cropped); }
        }
        static byte[] GetPixels(BitmapSource bitmap) {
            byte[] rgba = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(rgba, bitmap.PixelWidth * 4, 0); return rgba;
        }
        static byte[] GetAlpha(BitmapSource bitmap) {
            byte[] rgba = GetPixels(bitmap), alpha = new byte[bitmap.PixelWidth * bitmap.PixelHeight];
            for (int i = 0; i < alpha.Length; i++) alpha[i] = rgba[i * 4 + 3];
            return alpha;
        }
        static Int32Rect AlphaBounds(BitmapSource bitmap) {
            byte[] rgba = GetPixels(bitmap); int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
            int left = w, top = h, right = -1, bottom = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (rgba[(y * w + x) * 4 + 3] > 24) {
                left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            if (right < left) throw new InvalidDataException("A sprite is fully transparent.");
            left = Math.Max(0, left - 2); top = Math.Max(0, top - 2);
            right = Math.Min(w - 1, right + 2); bottom = Math.Min(h - 1, bottom + 2);
            return new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        }
        static BitmapSource Trim(BitmapSource bitmap) { CroppedBitmap trimmed = new CroppedBitmap(bitmap, AlphaBounds(bitmap)); trimmed.Freeze(); return trimmed; }
        static Stream OpenAsset(string name) {
            Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Naiwa.Assets." + name.Replace('/', '.').Replace('\\', '.'));
            if (stream == null) {
                string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets", name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(local)) stream = File.OpenRead(local);
            }
            return stream;
        }
        static BitmapSource Load(string name, bool required) {
            Stream stream = OpenAsset(name);
            if (stream == null) { if (required) throw new FileNotFoundException("Missing sprite " + name); return null; }
            using (stream) {
                PngBitmapDecoder decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                FormatConvertedBitmap bitmap = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
                bitmap.Freeze();
                // Invisible generation noise must not occupy the layered window's input surface.
                byte[] clean = GetPixels(bitmap);
                for (int i = 0; i < clean.Length; i += 4) if (clean[i + 3] <= 24) {
                    clean[i] = 0; clean[i + 1] = 0; clean[i + 2] = 0; clean[i + 3] = 0;
                }
                BitmapSource normalized = BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY, PixelFormats.Bgra32, null, clean, bitmap.PixelWidth * 4);
                normalized.Freeze(); return normalized;
            }
        }
        void Add(int id, BitmapSource bitmap) {
            poses.Add(id, bitmap); byte[] alpha = GetAlpha(bitmap); pixels.Add(id, alpha); framePixels.Add(bitmap, alpha);
        }
        public BitmapSource Get(int id) { return poses[id]; }
        public bool HasTransparency(int id) {
            return HasTransparency(poses[id]);
        }
        public bool HasTransparency(BitmapSource bitmap) {
            byte[] alpha = FramePixels(bitmap); int transparent = 0, opaque = 0;
            for (int i = 0; i < alpha.Length; i++) { if (alpha[i] < 16) transparent++; if (alpha[i] > 240) opaque++; }
            return transparent > alpha.Length / 100 && opaque > alpha.Length / 100;
        }
        public bool HasCleanAlpha(BitmapSource bitmap) {
            byte[] alpha = FramePixels(bitmap);
            for (int i = 0; i < alpha.Length; i++) if (alpha[i] > 0 && alpha[i] <= 24) return false;
            return true;
        }
        public bool Hit(int id, double x, double y, double boxWidth, double boxHeight) {
            return Hit(poses[id], x, y, boxWidth, boxHeight);
        }
        public bool Hit(BitmapSource b, double x, double y, double boxWidth, double boxHeight) {
            double ratio = Math.Min(boxWidth / b.PixelWidth, boxHeight / b.PixelHeight);
            double left = (boxWidth - b.PixelWidth * ratio) / 2;
            double top = boxHeight - b.PixelHeight * ratio;
            int px = (int)((x - left) / ratio), py = (int)((y - top) / ratio);
            return px >= 0 && py >= 0 && px < b.PixelWidth && py < b.PixelHeight && FramePixels(b)[py * b.PixelWidth + px] > 24;
        }
        public Point OpaquePoint(int id, double boxWidth, double boxHeight) {
            BitmapSource b = poses[id]; byte[] rgba = pixels[id];
            double ratio = Math.Min(boxWidth / b.PixelWidth, boxHeight / b.PixelHeight);
            double left = (boxWidth - b.PixelWidth * ratio) / 2, top = boxHeight - b.PixelHeight * ratio;
            for (int y = b.PixelHeight / 2; y < b.PixelHeight; y++) for (int x = 0; x < b.PixelWidth; x++)
                if (rgba[y * b.PixelWidth + x] > 240) return new Point(left + (x + .5) * ratio, top + (y + .5) * ratio);
            throw new InvalidDataException("Sprite has no opaque point.");
        }
    }

    public sealed class PetSettings {
        public double Left = Double.NaN, Top = Double.NaN, Scale = 1.0;
        public bool AlwaysOnTop = true, Breathing = true, SoundEnabled = false;
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NaiwaDesktopPet"); } }
        public static PetSettings Read() {
            try { using (FileStream s = File.OpenRead(Path.Combine(Folder, "settings.xml"))) return (PetSettings)new XmlSerializer(typeof(PetSettings)).Deserialize(s); }
            catch { return new PetSettings(); }
        }
        public void Save() {
            try {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, "settings.xml"), temp = path + ".new";
                using (FileStream s = File.Create(temp)) new XmlSerializer(typeof(PetSettings)).Serialize(s, this);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } catch { /* A read-only settings directory must never stop the pet. */ }
        }
    }

    public sealed class PetWindow : Window {
        const double BaseHeight = 280, WidthRatio = 1.28, BubbleHeight = 46;
        readonly PetAssets assets;
        readonly PetBehavior behavior = new PetBehavior();
        readonly PetSettings settings;
        readonly Image sprite = new Image();
        readonly Image previousSprite = new Image();
        readonly Border bubble = new Border();
        readonly TextBlock bubbleText = new TextBlock();
        readonly ScaleTransform squash = new ScaleTransform(1, 1);
        readonly RotateTransform sway = new RotateTransform();
        readonly TranslateTransform bounce = new TranslateTransform();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Canvas canvas = new Canvas();
        readonly string[] sayings = { "", "奶娃在这里陪你", "接招！奶娃出拳～", "嗯哼，听你说", "怎么啦？", "耶！今天也要开心", "看我的小拳拳", "嘿，打个招呼！", "换个角度，倒着看你～", "哈哈哈哈，笑到肚子疼！" };
        Forms.NotifyIcon tray;
        MediaPlayer laughAudio;
        bool laughAudioReady;
        HwndSource source;
        Point downCursor, downWindow;
        bool pressing, moving, closing, renderingSubscribed;
        int shownPose = 0;
        double bubbleUntil, poseStarted;
        double frameTransitionAt = Double.NegativeInfinity;
        EventWaitHandle showEvent;
        RegisteredWaitHandle showWait;
        internal bool AudioPlayerCreated { get { return laughAudio != null; } }
        internal bool SharedFadeGeometry { get { return Object.ReferenceEquals(sprite.RenderTransform, previousSprite.RenderTransform) && sprite.Width == previousSprite.Width && sprite.Height == previousSprite.Height; } }
        public PetWindow(PetAssets images) {
            assets = images; behavior.LaughDuration = assets.LaughDuration; settings = PetSettings.Read();
            settings.Scale = Finite(settings.Scale) ? Math.Max(.55, Math.Min(1.8, settings.Scale)) : 1;
            Title = "奶娃桌宠"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
            ShowActivated = false; Topmost = settings.AlwaysOnTop; Focusable = false;
            UseLayoutRounding = true; SnapsToDevicePixels = true;
            Content = canvas; canvas.Background = Brushes.Transparent;
            sprite.Stretch = Stretch.Uniform; sprite.VerticalAlignment = VerticalAlignment.Bottom;
            sprite.HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(sprite, BitmapScalingMode.HighQuality);
            TransformGroup transforms = new TransformGroup(); transforms.Children.Add(squash); transforms.Children.Add(sway); transforms.Children.Add(bounce);
            sprite.RenderTransform = transforms; sprite.RenderTransformOrigin = new Point(.5, .88);
            previousSprite.Stretch = Stretch.Uniform; previousSprite.IsHitTestVisible = false;
            previousSprite.RenderTransform = transforms; previousSprite.RenderTransformOrigin = sprite.RenderTransformOrigin;
            previousSprite.Visibility = Visibility.Collapsed;
            RenderOptions.SetBitmapScalingMode(previousSprite, BitmapScalingMode.HighQuality);
            canvas.Children.Add(previousSprite);
            canvas.Children.Add(sprite);
            bubble.Background = new SolidColorBrush(Color.FromArgb(242, 255, 251, 230));
            bubble.BorderBrush = new SolidColorBrush(Color.FromRgb(215, 173, 62)); bubble.BorderThickness = new Thickness(1);
            bubble.CornerRadius = new CornerRadius(14); bubble.Padding = new Thickness(12, 7, 12, 7);
            bubble.IsHitTestVisible = false; bubble.Visibility = Visibility.Collapsed;
            bubbleText.FontFamily = new FontFamily("Microsoft YaHei UI"); bubbleText.FontSize = 12;
            bubbleText.Foreground = new SolidColorBrush(Color.FromRgb(81, 63, 29)); bubbleText.TextAlignment = TextAlignment.Center;
            bubble.Child = bubbleText; canvas.Children.Add(bubble);
            ApplySize(false);
            SourceInitialized += OnSourceInitialized;
            Loaded += delegate {
                PlaceInitially(); UpdatePose(); CreateTray();
                showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\NaiwaDesktopPet.Show");
                showWait = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate { Dispatcher.BeginInvoke(new Action(ShowAndFind)); }, null, -1, false);
                UpdateRenderingSubscription();
                Say("点我换姿势 · 拖动搬家 · 右键菜单", 5);
            };
            MouseLeftButtonDown += OnLeftDown; MouseMove += OnMove; MouseLeftButtonUp += OnLeftUp;
            LostMouseCapture += delegate { if (pressing) FinishPress(false); };
            MouseRightButtonUp += delegate(object sender, MouseButtonEventArgs e) { ShowMenu(); e.Handled = true; };
            MouseWheel += delegate(object sender, MouseWheelEventArgs e) { SetScale(settings.Scale + (e.Delta > 0 ? .08 : -.08)); e.Handled = true; };
            IsVisibleChanged += delegate { if (!IsVisible) StopLaughAudio(); UpdateRenderingSubscription(); };
            Closed += delegate {
                closing = true; UpdateRenderingSubscription(); Save();
                StopLaughAudio(); if (laughAudio != null) laughAudio.Close();
                if (showWait != null) showWait.Unregister(null); if (showEvent != null) showEvent.Dispose();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
            };
        }
        void UpdateRenderingSubscription() {
            bool enabled = IsLoaded && IsVisible && !closing;
            if (enabled == renderingSubscribed) return;
            if (enabled) CompositionTarget.Rendering += Animate; else CompositionTarget.Rendering -= Animate;
            renderingSubscribed = enabled;
        }
        static bool Finite(double v) { return !Double.IsNaN(v) && !Double.IsInfinity(v); }
        double ArtHeight { get { return BaseHeight * settings.Scale; } }
        void ApplySize(bool preserveFeet) {
            double footX = Left + Width / 2, footY = Top + Height;
            Width = ArtHeight * WidthRatio; Height = ArtHeight + BubbleHeight;
            LayoutSprite();
            if (preserveFeet) { Left = footX - Width / 2; Top = footY - Height; ClampWindow(); }
        }
        void LayoutSprite() {
            BitmapSource bitmap = (BitmapSource)(sprite.Source ?? assets.Get(behavior.Pose));
            double ratio = Math.Min(Width / bitmap.PixelWidth, ArtHeight / bitmap.PixelHeight);
            sprite.Width = bitmap.PixelWidth * ratio; sprite.Height = bitmap.PixelHeight * ratio;
            Canvas.SetLeft(sprite, (Width - sprite.Width) / 2); Canvas.SetTop(sprite, BubbleHeight + ArtHeight - sprite.Height);
            previousSprite.Width = sprite.Width; previousSprite.Height = sprite.Height;
            Canvas.SetLeft(previousSprite, Canvas.GetLeft(sprite)); Canvas.SetTop(previousSprite, Canvas.GetTop(sprite));
        }
        IList<Rect> Screens() {
            List<Rect> list = new List<Rect>();
            Matrix matrix = source == null ? Matrix.Identity : source.CompositionTarget.TransformFromDevice;
            foreach (Forms.Screen screen in Forms.Screen.AllScreens) {
                Drawing.Rectangle area = screen.WorkingArea;
                Point a = matrix.Transform(new Point(area.Left, area.Top)); Point b = matrix.Transform(new Point(area.Right, area.Bottom));
                list.Add(new Rect(a, b));
            }
            return list;
        }
        void PlaceInitially() {
            if (Finite(settings.Left) && Finite(settings.Top)) { Left = settings.Left; Top = settings.Top; }
            else {
                Drawing.Rectangle area = Forms.Screen.PrimaryScreen.WorkingArea;
                Point end = source.CompositionTarget.TransformFromDevice.Transform(new Point(area.Right, area.Bottom));
                Left = end.X - Width - 20; Top = end.Y - Height - 12;
            }
            ClampWindow();
        }
        void ClampWindow() { Point p = PetBehavior.Clamp(new Point(Left, Top), new Size(Width, Height), Screens()); Left = p.X; Top = p.Y; }
        void Save() { if (!IsLoaded) return; settings.Left = Left; settings.Top = Top; settings.Save(); }
        void SetScale(double value) {
            settings.Scale = Math.Round(Math.Max(.55, Math.Min(1.8, value)), 2); ApplySize(true); Save();
            Say("奶娃大小 " + (int)Math.Round(settings.Scale * 100) + "%", 1.6);
        }
        void OnSourceInitialized(object sender, EventArgs e) {
            source = (HwndSource)PresentationSource.FromVisual(this);
            IntPtr hwnd = source.Handle;
            long style = Native.GetWindowLongPtr(hwnd, -20).ToInt64();
            Native.SetWindowLongPtr(hwnd, -20, new IntPtr(style | 0x80L | 0x08000000L)); // Tool window; never activates on interaction.
            source.AddHook(WindowHook);
        }
        IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
            if (msg == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            if (msg == 0x84 && !pressing) {
                long packed = lParam.ToInt64(); int x = (short)(packed & 0xffff), y = (short)((packed >> 16) & 0xffff);
                Point point = PointFromScreen(new Point(x, y));
                if (!HitSprite(point)) {
                    handled = true; return new IntPtr(-1); // Empty sprite pixels pass through.
                }
            }
            return IntPtr.Zero;
        }
        bool HitSprite(Point windowPoint) {
            return HitImage(sprite, windowPoint) || (previousSprite.Visibility == Visibility.Visible && previousSprite.Opacity > .2 && HitImage(previousSprite, windowPoint));
        }
        bool HitImage(Image image, Point windowPoint) {
            if (image.Source == null) return false;
            Point local = new Point(windowPoint.X - Canvas.GetLeft(image), windowPoint.Y - Canvas.GetTop(image));
            Point origin = new Point(image.Width * .5, image.Height * .88);
            GeneralTransform inverse = image.RenderTransform.Inverse;
            if (inverse != null) {
                local.Offset(-origin.X, -origin.Y); local = inverse.Transform(local); local.Offset(origin.X, origin.Y);
            }
            return assets.Hit((BitmapSource)image.Source, local.X, local.Y, image.Width, image.Height);
        }
        Point CursorPosition() { Native.POINT p; Native.GetCursorPos(out p); return source.CompositionTarget.TransformFromDevice.Transform(new Point(p.X, p.Y)); }
        void OnLeftDown(object sender, MouseButtonEventArgs e) {
            Point p = e.GetPosition(this);
            if (!HitSprite(p)) return;
            pressing = true; moving = false; downCursor = CursorPosition(); downWindow = new Point(Left, Top);
            Mouse.Capture(this); e.Handled = true;
        }
        void OnMove(object sender, MouseEventArgs e) {
            if (!pressing) return;
            Point cursor = CursorPosition(); Vector delta = cursor - downCursor;
            if (!moving && delta.Length > 6) { moving = true; behavior.BeginDrag(clock.Elapsed.TotalSeconds); bubble.Visibility = Visibility.Collapsed; UpdatePose(); }
            if (moving) { Left = downWindow.X + delta.X; Top = downWindow.Y + delta.Y; behavior.StepDrag(clock.Elapsed.TotalSeconds); UpdatePose(); }
        }
        void OnLeftUp(object sender, MouseButtonEventArgs e) { if (pressing) FinishPress(true); e.Handled = true; }
        void FinishPress(bool allowClick) {
            bool wasMoving = moving; pressing = false; moving = false; Mouse.Capture(null);
            if (wasMoving) { behavior.EndDrag(clock.Elapsed.TotalSeconds); ClampWindow(); Save(); }
            else if (allowClick) { behavior.Click(clock.Elapsed.TotalSeconds); UpdatePose(); Say(sayings[behavior.Pose], behavior.Pose == PetBehavior.Laugh ? Math.Max(1, assets.LaughDuration - .4) : 2.5); }
        }
        void UpdatePose() {
            if (shownPose == behavior.Pose) return;
            StopLaughAudio();
            shownPose = behavior.Pose; poseStarted = clock.Elapsed.TotalSeconds;
            sprite.Source = assets.Get(shownPose);
            sprite.Opacity = 1; previousSprite.Source = null; previousSprite.Visibility = Visibility.Collapsed;
            frameTransitionAt = Double.NegativeInfinity;
            LayoutSprite();
            if (shownPose == PetBehavior.Laugh) StartLaughAudio();
        }
        void StartLaughAudio() {
            if (!settings.SoundEnabled || !assets.HasLaughSound || !IsVisible || closing) return;
            try {
                if (laughAudio == null) {
                    string path = assets.GetLaughSoundPath(); if (path == null) return;
                    laughAudio = new MediaPlayer(); laughAudio.Volume = .45;
                    laughAudio.MediaOpened += delegate {
                        laughAudioReady = true;
                        if (settings.SoundEnabled && IsVisible && !closing && behavior.Pose == PetBehavior.Laugh) {
                            laughAudio.Position = TimeSpan.Zero; laughAudio.Play();
                        }
                    };
                    laughAudio.MediaFailed += delegate { laughAudioReady = false; StopLaughAudio(); };
                    laughAudio.Open(new Uri(path, UriKind.Absolute));
                    return;
                }
                if (laughAudioReady) { laughAudio.Position = TimeSpan.Zero; laughAudio.Play(); }
            } catch { /* A missing optional codec must never interrupt the pet. */ }
        }
        void StopLaughAudio() { if (laughAudio != null) { try { laughAudio.Stop(); } catch { } } }
        void SelectPose(int pose) {
            bool restart = behavior.Pose == pose && pose != PetBehavior.Idle;
            behavior.Select(pose, clock.Elapsed.TotalSeconds); if (restart) shownPose = 0;
            UpdatePose(); Say(sayings[pose], pose == PetBehavior.Laugh ? Math.Max(1, assets.LaughDuration - .4) : 2.3);
        }
        void Say(string text, double seconds) {
            bubbleText.Text = text; bubble.Measure(new Size(Width, BubbleHeight));
            Canvas.SetLeft(bubble, Math.Max(0, (Width - bubble.DesiredSize.Width) / 2)); Canvas.SetTop(bubble, 3);
            bubbleUntil = clock.Elapsed.TotalSeconds + seconds; bubble.Visibility = Visibility.Visible;
        }
        void Animate(object sender, EventArgs e) {
            if (closing || !IsVisible) return;
            double now = clock.Elapsed.TotalSeconds; behavior.Tick(now); behavior.StepDrag(now); UpdatePose();
            if (now > bubbleUntil) bubble.Visibility = Visibility.Collapsed;
            double t = now - poseStarted;
            squash.ScaleX = 1; squash.ScaleY = 1; sway.Angle = 0; bounce.X = 0; bounce.Y = 0;
            if (behavior.Pose == PetBehavior.Laugh) {
                BitmapSource frame = assets.LaughFrames[assets.LaughFrameIndexAt(t)];
                if (!Object.ReferenceEquals(sprite.Source, frame)) {
                    if (assets.BlendLaughFrames) {
                        previousSprite.Source = sprite.Source; previousSprite.Visibility = Visibility.Visible;
                        previousSprite.Opacity = 1; sprite.Source = frame; sprite.Opacity = 0; frameTransitionAt = now;
                    } else {
                        sprite.Source = frame; sprite.Opacity = 1; previousSprite.Source = null;
                        previousSprite.Visibility = Visibility.Collapsed; frameTransitionAt = Double.NegativeInfinity;
                    }
                }
                double fade = assets.BlendLaughFrames ? Math.Min(1, (now - frameTransitionAt) / Math.Min(.09, .65 / assets.LaughFps)) : 1;
                sprite.Opacity = fade; previousSprite.Opacity = 1 - fade;
                if (fade >= 1) previousSprite.Visibility = Visibility.Collapsed;
                if (!assets.LaughSourceMotion) {
                    double shake = Math.Sin(t * 23);
                    squash.ScaleX = 1 + .012 * shake; squash.ScaleY = 1 - .016 * shake;
                    sway.Angle = Math.Sin(t * 9) * 1.6; bounce.Y = -Math.Abs(Math.Sin(t * 14)) * 2 * settings.Scale;
                    bounce.X = Math.Sin(t * 22) * .6 * settings.Scale;
                }
            } else if (behavior.Dragging) {
                sway.Angle = Math.Sin(now * 12) * 5; squash.ScaleY = 1 + .018 * Math.Sin(now * 14);
            } else if (behavior.Pose == PetBehavior.Idle && settings.Breathing) {
                double breath = Math.Sin(now * 1.65); squash.ScaleY = 1 + .007 * breath; squash.ScaleX = 1 - .0025 * breath;
            } else if (behavior.Pose != PetBehavior.Idle) {
                double decay = Math.Exp(-t * 3.8); bounce.Y = -Math.Abs(Math.Sin(t * 10)) * 13 * decay;
                sway.Angle = Math.Sin(t * 10) * 2 * decay;
            }
        }
        MenuItem Item(string title, Action action) {
            MenuItem item = new MenuItem(); item.Header = title; item.Click += delegate { action(); }; return item;
        }
        void ShowMenu() {
            ContextMenu menu = new ContextMenu(); menu.FontFamily = new FontFamily("Microsoft YaHei UI"); menu.FontSize = 13;
            menu.Items.Add(Item("返回待机（图 1）", delegate { SelectPose(1); }));
            MenuItem poses = new MenuItem(); poses.Header = "换个姿势";
            string[] names = { "", "", "图 2 · 出招", "图 3 · 抱臂", "图 4 · 歪头", "图 5 · 比耶", "图 6 · 小拳拳", "图 7 · 打招呼", "图 8 · 倒立 / 倒着看你" };
            for (int i = 2; i <= PetBehavior.LastPose; i++) { int id = i; poses.Items.Add(Item(names[id], delegate { SelectPose(id); })); }
            menu.Items.Add(poses); menu.Items.Add(Item("捧腹大笑", delegate { SelectPose(PetBehavior.Laugh); })); menu.Items.Add(new Separator());
            MenuItem scaleMenu = new MenuItem(); scaleMenu.Header = "调整大小（也可滚动鼠标）";
            double[] scales = { .65, .85, 1, 1.25, 1.5 };
            foreach (double scale in scales) { double s = scale; scaleMenu.Items.Add(Item(((int)(s * 100)) + "%" + (s == 1 ? " · 默认" : ""), delegate { SetScale(s); })); }
            menu.Items.Add(scaleMenu);
            MenuItem breathe = Item("待机呼吸", delegate { settings.Breathing = !settings.Breathing; Save(); }); breathe.IsCheckable = true; breathe.IsChecked = settings.Breathing; menu.Items.Add(breathe);
            MenuItem sound = Item("参考视频原声", delegate {
                settings.SoundEnabled = !settings.SoundEnabled;
                if (!settings.SoundEnabled) StopLaughAudio(); else if (behavior.Pose == PetBehavior.Laugh) StartLaughAudio(); Save();
            }); sound.IsCheckable = true; sound.IsChecked = settings.SoundEnabled; sound.IsEnabled = assets.HasLaughSound; menu.Items.Add(sound);
            MenuItem top = Item("始终置顶", delegate { settings.AlwaysOnTop = !settings.AlwaysOnTop; Topmost = settings.AlwaysOnTop; Save(); }); top.IsCheckable = true; top.IsChecked = settings.AlwaysOnTop; menu.Items.Add(top);
            menu.Items.Add(Item("回到屏幕右下角", ResetPosition));
            menu.Items.Add(Item("暂时隐藏（托盘可找回）", delegate { Hide(); }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("操作说明", delegate { MessageBox.Show("单击：按图 2 → 3 → 4 → 5 → 6 → 7 → 8 → 大笑依次互动。\n拖动：移动奶娃，同时轮换图 2～8。\n松开后稍等：自动回到图 1 待机。\n鼠标滚轮：调整大小。右键：更多设置。\n参考视频原声：默认关闭，右键菜单可开启。\n\n大小和位置会自动记住。托盘图标可以找回奶娃。", "奶娃桌宠", MessageBoxButton.OK, MessageBoxImage.Information); }));
            menu.Items.Add(Item("退出奶娃", Close)); menu.IsOpen = true;
        }
        void ResetPosition() { settings.Left = Double.NaN; settings.Top = Double.NaN; PlaceInitially(); SelectPose(1); Save(); }
        public void ShowAndFind() { if (closing) return; Show(); ClampWindow(); SelectPose(1); Say("奶娃回来啦！", 2); }
        void CreateTray() {
            tray = new Forms.NotifyIcon(); tray.Text = "奶娃桌宠 · 双击找回奶娃";
            using (Drawing.Bitmap bitmap = new Drawing.Bitmap(32, 32)) {
                using (Drawing.Graphics g = Drawing.Graphics.FromImage(bitmap)) {
                    g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias; g.Clear(Drawing.Color.Transparent);
                    using (Drawing.Brush b = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 211, 63))) g.FillEllipse(b, 2, 1, 28, 30);
                    using (Drawing.Brush b = new Drawing.SolidBrush(Drawing.Color.FromArgb(128, 177, 74))) { g.FillEllipse(b, 8, 8, 8, 9); g.FillEllipse(b, 20, 8, 8, 9); }
                    g.FillEllipse(Drawing.Brushes.Black, 11, 11, 4, 5); g.FillEllipse(Drawing.Brushes.Black, 23, 11, 4, 5);
                    g.DrawArc(Drawing.Pens.SaddleBrown, 12, 15, 12, 9, 5, 155);
                }
                IntPtr h = bitmap.GetHicon(); using (Drawing.Icon icon = Drawing.Icon.FromHandle(h)) tray.Icon = (Drawing.Icon)icon.Clone(); Native.DestroyIcon(h);
            }
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("显示奶娃", null, delegate { Dispatcher.BeginInvoke(new Action(ShowAndFind)); });
            menu.Items.Add("返回待机", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); SelectPose(1); })); });
            menu.Items.Add("捧腹大笑", null, delegate { Dispatcher.BeginInvoke(new Action(delegate { Show(); SelectPose(PetBehavior.Laugh); })); });
            menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, delegate { Dispatcher.BeginInvoke(new Action(Close)); });
            tray.ContextMenuStrip = menu; tray.DoubleClick += delegate { ShowAndFind(); }; tray.Visible = true;
        }
    }

    static class Native {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr h, int n);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")] static extern IntPtr GetWindowLong64(IntPtr h, int n);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong32(IntPtr h, int n, int v);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")] static extern IntPtr SetWindowLong64(IntPtr h, int n, IntPtr v);
        public static IntPtr GetWindowLongPtr(IntPtr h, int n) { return IntPtr.Size == 8 ? GetWindowLong64(h, n) : new IntPtr(GetWindowLong32(h, n)); }
        public static IntPtr SetWindowLongPtr(IntPtr h, int n, IntPtr v) { return IntPtr.Size == 8 ? SetWindowLong64(h, n, v) : new IntPtr(SetWindowLong32(h, n, v.ToInt32())); }
    }

    public static class Program {
        static void Check(bool condition, string name, List<string> checks) { if (!condition) throw new Exception("FAIL: " + name); checks.Add(name); }
        static string Escape(string s) { return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n"); }
        static void WriteJson(string path, bool ok, List<string> checks, string error, PetAssets assets = null) {
            StringBuilder b = new StringBuilder(); b.Append("{\n  \"ok\": ").Append(ok ? "true" : "false").Append(",\n  \"checks\": [");
            for (int i = 0; i < checks.Count; i++) { if (i > 0) b.Append(", "); b.Append('"').Append(Escape(checks[i])).Append('"'); }
            b.Append("],\n  \"error\": \"").Append(Escape(error ?? "")).Append('"');
            if (assets != null) b.Append(",\n  \"laugh\": { \"frames\": ").Append(assets.LaughFrames.Count)
                .Append(", \"fps\": ").Append(assets.LaughFps.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"duration\": ").Append(assets.LaughDuration.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(", \"sourceMotion\": ").Append(assets.LaughSourceMotion ? "true" : "false")
                .Append(", \"soundEmbedded\": ").Append(assets.HasLaughSound ? "true" : "false")
                .Append(", \"frameWidth\": ").Append(assets.LaughFrames[0].PixelWidth)
                .Append(", \"frameHeight\": ").Append(assets.LaughFrames[0].PixelHeight)
                .Append(", \"uniqueFrames\": ").Append(assets.UniqueLaughFrameCount())
                .Append(", \"crossfade\": ").Append(assets.BlendLaughFrames ? "true" : "false")
                .Append(", \"renderClock\": \"CompositionTarget.Rendering\" }");
            b.Append("\n}\n");
            File.WriteAllText(path, b.ToString(), new UTF8Encoding(false));
        }
        static int Smoke(string path, PetAssets assets) {
            List<string> checks = new List<string>();
            try {
                PetBehavior b = new PetBehavior(); Check(b.Pose == 1, "idle-is-pose1", checks);
                for (int i = 2; i <= PetBehavior.Laugh; i++) Check(b.Click(i * 10) == i, "click-sequence-pose" + i, checks);
                Check(b.Click(100) == 2, "click-sequence-wraps", checks);
                b.Tick(102.99); Check(b.Pose == 2, "interaction-holds-before-timeout", checks);
                b.Tick(103.01); Check(b.Pose == 1, "interaction-returns-to-pose1", checks);
                b.Select(PetBehavior.Laugh, 100); b.Tick(104.1); Check(b.Pose == PetBehavior.Laugh, "laugh-runs-longer", checks); b.Tick(104.3); Check(b.Pose == 1, "laugh-returns-to-idle", checks);
                b.LaughDuration = 6.5; b.Select(PetBehavior.Laugh, 100); b.Tick(106.4); Check(b.Pose == PetBehavior.Laugh, "custom-laugh-duration-holds", checks); b.Tick(106.6); Check(b.Pose == 1, "custom-laugh-duration-returns-to-idle", checks);
                b.BeginDrag(110); Check(b.Pose == 2 && b.Dragging, "drag-starts-with-interaction", checks);
                for (int i = 3; i <= PetBehavior.LastPose; i++) { b.StepDrag(110 + (i - 2) * .25); Check(b.Pose == i, "drag-cycles-pose" + i, checks); }
                b.StepDrag(111.75); Check(b.Pose == 2, "drag-wraps-without-laugh", checks);
                int clicks = b.ClickIndex; b.EndDrag(112); Check(b.ClickIndex == clicks, "drag-does-not-advance-click-sequence", checks);
                b.Tick(113.5); Check(b.Pose == 1, "drag-release-returns-to-idle", checks);
                b.LaughDuration = assets.LaughDuration; b.Select(PetBehavior.Laugh, 200);
                b.Tick(200 + assets.LaughDuration - .001); Check(b.Pose == PetBehavior.Laugh, "actual-clip-holds-until-configured-duration", checks);
                b.Tick(200 + assets.LaughDuration + .001); Check(b.Pose == PetBehavior.Idle, "actual-clip-returns-to-idle-at-configured-duration", checks);
                List<Rect> screens = new List<Rect> { new Rect(-1920, 0, 1920, 1040), new Rect(0, 0, 1920, 1040) };
                Point p = PetBehavior.Clamp(new Point(-2400, 900), new Size(360, 330), screens);
                Check(p == new Point(-1920, 710), "clamp-negative-monitor", checks);
                p = PetBehavior.Clamp(new Point(1800, 900), new Size(360, 330), screens);
                Check(p == new Point(1560, 710), "clamp-right-monitor", checks);
                p = PetBehavior.Clamp(new Point(-850, 200), new Size(360, 330), screens);
                Check(p == new Point(-850, 200), "preserve-valid-secondary-position", checks);
                for (int i = 1; i <= PetBehavior.Laugh; i++) {
                    Check(assets.Get(i).PixelWidth > 20 && assets.Get(i).PixelHeight > 20, "sprite-decodes-" + i, checks);
                    Check(assets.HasTransparency(i), "sprite-has-transparent-background-" + i, checks);
                    Check(assets.HasCleanAlpha(assets.Get(i)), "sprite-no-invisible-alpha-noise-" + i, checks);
                    Check(!assets.Hit(i, -1, -1, 360, 280), "sprite-empty-region-passes-through-" + i, checks);
                    Check(!assets.Hit(i, 0, 0, 360, 280), "sprite-corner-passes-through-" + i, checks);
                    Point opaque = assets.OpaquePoint(i, 360, 280);
                    Check(assets.Hit(i, opaque.X, opaque.Y, 360, 280), "sprite-opaque-body-receives-click-" + i, checks);
                }
                Check(assets.LaughFrames.Count > 0, "laugh-animation-ready", checks);
                Check(assets.LaughFps >= 1 && assets.LaughFps <= 60, "laugh-frame-rate-valid", checks);
                Check(assets.LaughDuration >= 1 && assets.LaughDuration <= 30, "laugh-duration-valid", checks);
                Check(assets.LaughFrameIndexAt(0) == 0, "laugh-timeline-starts-at-first-frame", checks);
                if (assets.LaughSourceMotion) {
                    bool correct = true;
                    for (int i = 0; i < assets.LaughFrames.Count; i++) if (assets.LaughFrameIndexAt((i + .5) / assets.LaughFps) != i) correct = false;
                    Check(correct, "source-timeline-selects-each-native-frame-at-correct-time", checks);
                    Check(assets.LaughFrameIndexAt(assets.LaughFrames.Count / assets.LaughFps + 1) == assets.LaughFrames.Count - 1, "source-timeline-does-not-repeat-before-idle", checks);
                    if (assets.LaughFps >= 29 && assets.LaughFrames.Count > 10) {
                        Check(!assets.BlendLaughFrames, "native-motion-has-no-crossfade-ghosts", checks);
                        Check(assets.UniqueLaughFrameCount() == assets.LaughFrames.Count, "native-motion-contains-unique-real-source-frames", checks);
                        double step = 1.0 / assets.LaughFps;
                        Check(assets.LaughFrameIndexAt(step * .99) == 0 && assets.LaughFrameIndexAt(step * 1.01) == 1, "native-frame-change-respects-thirty-hz-boundary", checks);
                    }
                }
                for (int i = 0; i < assets.LaughFrames.Count; i++) {
                    Check(assets.HasTransparency(assets.LaughFrames[i]), "laugh-frame-transparent-" + i, checks);
                    Check(assets.HasCleanAlpha(assets.LaughFrames[i]), "laugh-frame-no-invisible-alpha-noise-" + i, checks);
                    Check(assets.LaughFrames[i].PixelWidth == assets.LaughFrames[0].PixelWidth && assets.LaughFrames[i].PixelHeight == assets.LaughFrames[0].PixelHeight, "laugh-shared-frame-size-" + i, checks);
                }
                PetWindow probe = new PetWindow(assets);
                Check(probe.AllowsTransparency && !probe.ShowInTaskbar && probe.WindowStyle == WindowStyle.None, "native-transparent-window-constructs", checks);
                Check(probe.Width > 0 && probe.Height > 0 && !probe.IsVisible, "window-initializes-without-showing", checks);
                Check(!new PetSettings().SoundEnabled, "reference-sound-defaults-to-off", checks);
                Check(!probe.AudioPlayerCreated, "headless-never-creates-audio-player", checks);
                Check(!assets.SoundWasExtracted, "headless-never-extracts-reference-audio", checks);
                Check(probe.SharedFadeGeometry, "crossfade-shares-size-and-transform", checks);
                probe.Close();
                WriteJson(path, true, checks, null, assets); return 0;
            } catch (Exception error) { WriteJson(path, false, checks, error.ToString(), assets); return 1; }
        }
        static void Preview(string path, PetAssets assets) {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) {
                for (int i = 1; i <= PetBehavior.Laugh; i++) {
                    double x = ((i - 1) % 3) * 300, y = ((i - 1) / 3) * 350;
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(243, 245, 239)), null, new Rect(x + 8, y + 8, 284, 334), 18, 18);
                    BitmapSource image = i == PetBehavior.Laugh ? assets.LaughFrames[assets.LaughFrames.Count / 2] : assets.Get(i);
                    double ratio = Math.Min(260.0 / image.PixelWidth, 280.0 / image.PixelHeight);
                    double w = image.PixelWidth * ratio, h = image.PixelHeight * ratio;
                    dc.DrawImage(image, new Rect(x + 150 - w / 2, y + 300 - h, w, h));
                    FormattedText text = new FormattedText(i == PetBehavior.Laugh ? "捧腹大笑" : "图 " + i + (i == 1 ? " · 待机" : (i == 8 ? " · 倒立" : " · 互动")), System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 16, Brushes.DarkSlateGray);
                    dc.DrawText(text, new Point(x + 150 - text.Width / 2, y + 310));
                }
            }
            RenderTargetBitmap target = new RenderTargetBitmap(900, 1050, 96, 96, PixelFormats.Pbgra32); target.Render(visual);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using (FileStream output = File.Create(path)) encoder.Save(output);
        }
        static RenderTargetBitmap RenderLaughFrame(PetAssets assets, double time) {
            const double artHeight = 280, bubbleHeight = 46;
            double width = artHeight * 1.28, height = artHeight + bubbleHeight;
            int frameIndex = assets.LaughFrameIndexAt(time); BitmapSource frame = assets.LaughFrames[frameIndex];
            double ratio = Math.Min(width / frame.PixelWidth, artHeight / frame.PixelHeight);
            double w = frame.PixelWidth * ratio, h = frame.PixelHeight * ratio;
            double left = (width - w) / 2, top = bubbleHeight + artHeight - h;
            double sx = 1, sy = 1, angle = 0, dx = 0, dy = 0;
            if (!assets.LaughSourceMotion) {
                double shake = Math.Sin(time * 23); sx = 1 + .012 * shake; sy = 1 - .016 * shake;
                angle = Math.Sin(time * 9) * 1.6; dy = -Math.Abs(Math.Sin(time * 14)) * 2; dx = Math.Sin(time * 22) * .6;
            }
            Matrix motion = Matrix.Identity; motion.Translate(-w * .5, -h * .88); motion.Scale(sx, sy); motion.Rotate(angle);
            motion.Translate(left + w * .5 + dx, top + h * .88 + dy);
            DrawingVisual visual = new DrawingVisual(); RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (DrawingContext dc = visual.RenderOpen()) {
                dc.PushTransform(new MatrixTransform(motion));
                double elapsed = time - Math.Floor(time * assets.LaughFps + 1e-9) / assets.LaughFps;
                double fadeDuration = Math.Min(.09, .65 / assets.LaughFps);
                double fade = assets.BlendLaughFrames && time >= 1.0 / assets.LaughFps ? Math.Min(1, elapsed / fadeDuration) : 1;
                if (fade < 1) {
                    int previous = (frameIndex + assets.LaughFrames.Count - 1) % assets.LaughFrames.Count;
                    dc.PushOpacity(1 - fade); dc.DrawImage(assets.LaughFrames[previous], new Rect(0, 0, w, h)); dc.Pop();
                }
                dc.PushOpacity(fade); dc.DrawImage(frame, new Rect(0, 0, w, h)); dc.Pop(); dc.Pop();
            }
            RenderTargetBitmap target = new RenderTargetBitmap((int)Math.Ceiling(width), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            target.Render(visual); target.Freeze(); return target;
        }
        static void RenderLaughFrames(string directory, PetAssets assets, double fps) {
            if (fps < 1 || fps > 60 || Double.IsNaN(fps)) throw new ArgumentException("Preview fps must be between 1 and 60.");
            Directory.CreateDirectory(directory); int count = (int)Math.Ceiling(assets.LaughDuration * fps);
            StringBuilder indices = new StringBuilder(), names = new StringBuilder();
            for (int i = 0; i < count; i++) {
                double time = i / fps; string name = "frame" + i.ToString("0000") + ".png";
                PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(RenderLaughFrame(assets, time)));
                using (FileStream output = File.Create(Path.Combine(directory, name))) encoder.Save(output);
                if (i > 0) { indices.Append(","); names.Append(","); }
                indices.Append(assets.LaughFrameIndexAt(time)); names.Append('"').Append(name).Append('"');
            }
            string metadata = "{\n  \"fps\": " + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\n  \"duration\": " + assets.LaughDuration.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\n  \"frameCount\": " + count + ",\n  \"width\": 359,\n  \"height\": 326,\n  \"sourceFrames\": " + assets.LaughFrames.Count +
                ",\n  \"sourceUniqueFrames\": " + assets.UniqueLaughFrameCount() + ",\n  \"sourceFps\": " + assets.LaughFps.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\n  \"sourceMotionOnly\": " + (assets.LaughSourceMotion ? "true" : "false") +
                ",\n  \"crossfade\": " + (assets.BlendLaughFrames ? "true" : "false") +
                ",\n  \"sourceFrameIndices\": [" + indices + "],\n  \"files\": [" + names + "]\n}\n";
            File.WriteAllText(Path.Combine(directory, "animation.json"), metadata, new UTF8Encoding(false));
        }
        [STAThread] public static int Main(string[] args) {
            string testPath = null;
            try {
                Native.SetProcessDPIAware();
                if (args.Length > 0 && args[0] == "--smoke-test") {
                    testPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "smoke-test.json");
                    return Smoke(testPath, new PetAssets());
                }
                if (args.Length > 0 && args[0] == "--preview") {
                    Preview(args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preview.png"), new PetAssets()); return 0;
                }
                if (args.Length > 0 && args[0] == "--render-laugh-frames") {
                    if (args.Length < 2) throw new ArgumentException("--render-laugh-frames requires an output directory.");
                    double fps = args.Length > 2 ? Double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture) : 30;
                    RenderLaughFrames(Path.GetFullPath(args[1]), new PetAssets(), fps); return 0;
                }
                bool created;
                using (Mutex mutex = new Mutex(true, "Local\\NaiwaDesktopPet.Singleton", out created)) {
                    if (!created) {
                        try { using (EventWaitHandle signal = EventWaitHandle.OpenExisting("Local\\NaiwaDesktopPet.Show")) signal.Set(); } catch { }
                        return 0;
                    }
                    Application app = new Application(); app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e) {
                        try { Directory.CreateDirectory(PetSettings.Folder); File.AppendAllText(Path.Combine(PetSettings.Folder, "error.log"), DateTime.Now.ToString("s") + " " + e.Exception + Environment.NewLine); } catch { }
                    };
                    app.Run(new PetWindow(new PetAssets())); mutex.ReleaseMutex();
                }
                return 0;
            } catch (Exception error) {
                if (testPath != null) WriteJson(testPath, false, new List<string>(), error.ToString());
                else if (args.Length > 0) File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic-error.txt"), error.ToString());
                else MessageBox.Show("奶娃暂时没能启动：\n" + error.Message, "奶娃桌宠", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
