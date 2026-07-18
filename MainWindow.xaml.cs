using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace AeroCut;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    enum Tab { Cut, Crop, Audio }

    const string WaveColor = "#8A929E";
    const long GitHubUserId = 109703063;

    static readonly HttpClient _http = CreateHttp();
    string? _gitHubUser;

    readonly LibVLC _libvlc;
    readonly MediaPlayer _player;
    readonly DispatcherTimer _timer;

    string? _path;
    double _duration;
    double _start;
    double _end;
    double _volume = 1.0;
    bool _lengthReady;
    bool _waveReady;
    Tab _tab = Tab.Cut;

    int _videoW;
    int _videoH;
    int _cropX;
    int _cropY;
    int _cropW;
    int _cropH;
    bool _cropActive;

    readonly Stopwatch _seekWatch = Stopwatch.StartNew();
    double _seekTarget = -1;

    CancellationTokenSource? _cts;
    Process? _proc;
    bool _canceled;

    public MainWindow()
    {
        Core.Initialize(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        _libvlc = new LibVLC();
        _player = new MediaPlayer(_libvlc) { Volume = 100 };

        InitializeComponent();
        ApplicationThemeManager.ApplySystemTheme();
        SystemThemeWatcher.Watch(this, Wpf.Ui.Controls.WindowBackdropType.Mica, false);
        ApplicationThemeManager.Changed += OnThemeChanged;
        ApplyWindowsAccent();
        ApplyDialogBackground();
        Video.MediaPlayer = _player;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += OnTick;

        Timeline.StartChanged += s => { _start = s; StartBox.Text = Fmt(s); Seek(s); };
        Timeline.EndChanged += e => { _end = e; EndBox.Text = Fmt(e); Seek(e); };
        Timeline.Seek += Seek;
        Crop.CropChanged += OnCropChanged;
        Audio.Seek += Seek;
        Audio.SelectionChanged += OnKfSelectionChanged;
        Audio.KeyframesChanged += OnKeyframesChanged;
        Audio.GlobalVolumeChanged += OnGlobalVolumeChanged;

        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version != null)
            VersionText.Text = $"{version.Major}.{version.Minor}.{version.Build}";

        InitGitHub();
    }

    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AeroCut");
        return http;
    }

    void OnThemeChanged(ApplicationTheme currentTheme, System.Windows.Media.Color systemAccent)
    {
        ApplyWindowsAccent();
        ApplyDialogBackground();
    }

    void ApplyDialogBackground()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var brush = new System.Windows.Media.SolidColorBrush(dark
            ? System.Windows.Media.Color.FromRgb(0x2B, 0x2B, 0x2B)
            : System.Windows.Media.Color.FromRgb(0xF9, 0xF9, 0xF9));
        brush.Freeze();
        ExportCard.Background = brush;
        BusyCard.Background = brush;
    }

    void ApplyWindowsAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int raw)
            {
                uint v = unchecked((uint)raw);
                var color = System.Windows.Media.Color.FromRgb(
                    (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
                ApplicationAccentColorManager.Apply(color, ApplicationThemeManager.GetAppTheme(), false, true);
            }
        }
        catch { }
    }

    void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            Load(files[0]);
    }

    void OnOpenClick(object sender, MouseButtonEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Video files|*.mp4;*.mov;*.mkv;*.avi;*.webm;*.wmv;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.3gp;*.mts;*.m2ts;*.ogv;*.vob|All files|*.*"
        };
        if (dlg.ShowDialog() == true)
            Load(dlg.FileName);
    }

    void Load(string path)
    {
        _path = path;
        _lengthReady = false;
        _waveReady = false;
        _seekTarget = -1;

        AppTitleBar.Title = Path.GetFileName(path);
        Title = "Aero Cut  —  " + Path.GetFileName(path);
        OpenPrompt.Visibility = Visibility.Collapsed;
        Sidebar.Visibility = Visibility.Visible;
        ToolbarRight.Visibility = Visibility.Visible;

        UseKeyframes.IsChecked = false;
        Audio.Clear();
        Audio.SetGlobalMode(100);
        _volume = 1.0;
        _player.Volume = 100;
        KfVolBox.Text = "100%";
        UpdateAudioControls(false);

        _tab = Tab.Cut;
        ApplyTabVisuals();

        var media = new Media(_libvlc, path, FromType.FromPath);
        StartPlayback(media);
    }

    void StartPlayback(Media media, int attempt = 0)
    {
        if (_player.Hwnd != IntPtr.Zero || attempt >= 50)
        {
            _player.Play(media);
            _timer.Start();
        }
        else
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => StartPlayback(media, attempt + 1)));
        }
    }

    void OnTick(object? sender, EventArgs e)
    {
        FlushSeek();

        if (!_lengthReady)
        {
            long len = _player.Length;
            if (len <= 0)
                return;

            _lengthReady = true;
            _duration = len / 1000.0;
            _start = 0;
            _end = _duration;
            Timeline.Setup(_duration, _start, _end);
            Audio.Setup(_duration);
            StartBox.Text = Fmt(_start);
            EndBox.Text = Fmt(_end);
            ReadVideoSize();
            _player.SetPause(true);
            _player.Time = 0;
            ApplyTabVisuals();
        }

        double pos = _player.Time / 1000.0;
        Timeline.Position = pos;
        Audio.Position = pos;
        TimeText.Text = Fmt(pos) + " / " + Fmt(_duration);
        PlayButton.Content = _player.IsPlaying ? "Pause" : "Play";

        if (UseKeyframes.IsChecked == true)
            _player.Volume = (int)Math.Round(VolumeAt(pos));

        if (_player.IsPlaying && pos >= _end)
        {
            _player.SetPause(true);
            _player.Time = (long)(_end * 1000);
        }
    }

    void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_path == null || !_lengthReady)
            return;

        if (_player.IsPlaying)
        {
            _player.SetPause(true);
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped)
            _player.Play();

        double pos = _player.Time / 1000.0;
        if (pos < _start || pos >= _end - 0.01)
            _player.Time = (long)(_start * 1000);
        _player.SetPause(false);
    }

    void Seek(double seconds)
    {
        if (_path == null)
            return;
        _seekTarget = Math.Clamp(seconds, 0, Math.Max(0, _duration - 0.05));
        if (_seekWatch.ElapsedMilliseconds >= 70)
            FlushSeek();
    }

    void FlushSeek()
    {
        if (_seekTarget < 0)
            return;
        double t = _seekTarget;
        _seekTarget = -1;
        _seekWatch.Restart();
        if (_player.State is VLCState.Ended or VLCState.Stopped)
            _player.Play();
        _player.Time = (long)(t * 1000);
    }

    void OnNavCut(object sender, RoutedEventArgs e) => SelectTab(Tab.Cut);

    void OnNavAudio(object sender, RoutedEventArgs e) => SelectTab(Tab.Audio);

    async void OnNavCrop(object sender, RoutedEventArgs e) => await EnterCrop();

    void SelectTab(Tab tab)
    {
        if (_path == null || !_lengthReady)
            return;
        _tab = tab;
        if (tab == Tab.Audio)
            EnsureWaveform();
        ApplyTabVisuals();
    }

    async Task EnterCrop()
    {
        if (_path == null || !_lengthReady || _videoW <= 0)
            return;

        _player.SetPause(true);
        NavCrop.IsEnabled = false;
        var frame = await CaptureFrame(_player.Time / 1000.0);
        NavCrop.IsEnabled = true;
        if (frame == null)
        {
            System.Windows.MessageBox.Show("Couldn't capture a frame to crop.", "AeroCut",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Crop.SetImage(frame);
        Crop.SetCrop(
            _cropActive ? _cropX : 0,
            _cropActive ? _cropY : 0,
            _cropActive ? _cropW : _videoW,
            _cropActive ? _cropH : _videoH);
        _tab = Tab.Crop;
        ApplyTabVisuals();
    }

    void ApplyTabVisuals()
    {
        NavCut.Appearance = _tab == Tab.Cut ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        NavCrop.Appearance = _tab == Tab.Crop ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        NavAudio.Appearance = _tab == Tab.Audio ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;

        bool loaded = _path != null;
        bool crop = _tab == Tab.Crop;
        VideoHost.Visibility = loaded && !crop ? Visibility.Visible : Visibility.Collapsed;
        Crop.Visibility = loaded && crop ? Visibility.Visible : Visibility.Collapsed;
        Transport.Visibility = loaded && !crop ? Visibility.Visible : Visibility.Collapsed;
        CutPanel.Visibility = loaded && _tab == Tab.Cut ? Visibility.Visible : Visibility.Collapsed;
        CropPanel.Visibility = loaded && crop ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = loaded && _tab == Tab.Audio ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnBack(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(
                "Go back to the start screen? Your current changes will be discarded.",
                "Aero Cut", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _timer.Stop();
        _player.Stop();
        _path = null;
        _lengthReady = false;
        _seekTarget = -1;
        AppTitleBar.Title = "Aero Cut";
        Title = "Aero Cut";
        Sidebar.Visibility = Visibility.Collapsed;
        ToolbarRight.Visibility = Visibility.Collapsed;
        ExportOverlay.Visibility = Visibility.Collapsed;
        ApplyTabVisuals();
        OpenPrompt.Visibility = Visibility.Visible;
    }

    void OnGitHub(object sender, RoutedEventArgs e)
    {
        if (_gitHubUser != null)
            OpenUrl($"https://github.com/{_gitHubUser}/aero-cut");
    }

    void OnSponsor(object sender, RoutedEventArgs e)
    {
        if (_gitHubUser != null)
            OpenUrl($"https://github.com/sponsors/{_gitHubUser}");
    }

    void OnUpgrade(object sender, RoutedEventArgs e)
    {
        if (_gitHubUser != null)
            OpenUrl($"https://github.com/{_gitHubUser}/aero-cut/releases/latest");
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    async void InitGitHub()
    {
        string? user = null;
        string? latestTag = null;

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var userJson = await _http.GetStringAsync($"https://api.github.com/user/{GitHubUserId}");
                using (var doc = JsonDocument.Parse(userJson))
                    user = doc.RootElement.GetProperty("login").GetString();

                if (!string.IsNullOrEmpty(user))
                {
                    try
                    {
                        var relJson = await _http.GetStringAsync($"https://api.github.com/repos/{user}/aero-cut/releases/latest");
                        using var relDoc = JsonDocument.Parse(relJson);
                        latestTag = relDoc.RootElement.GetProperty("tag_name").GetString();
                    }
                    catch { latestTag = null; }
                    break;
                }
            }
            catch { }

            if (attempt < 3)
                await Task.Delay(3000);
        }

        if (string.IsNullOrEmpty(user))
            return;

        _gitHubUser = user;
        SponsorButton.Visibility = Visibility.Visible;
        GitHubButton.Visibility = Visibility.Visible;

        var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (!string.IsNullOrEmpty(latestTag) && IsNewer(latestTag!, current))
            UpgradeButton.Visibility = Visibility.Visible;
    }

    static bool IsNewer(string latestTag, Version? current)
    {
        if (current == null || !Version.TryParse(latestTag, out var latest))
            return false;
        var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
        var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
        return lat > cur;
    }

    async Task<BitmapSource?> CaptureFrame(double time)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "aerocut_" + Guid.NewGuid().ToString("N") + ".png");
        bool ok = await FFmpeg.ExtractFrameAsync(_path!, time, tmp);
        if (!ok || !File.Exists(tmp))
            ok = await FFmpeg.ExtractFrameAsync(_path!, 0, tmp);
        if (!ok || !File.Exists(tmp))
            return null;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(tmp);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    async void EnsureWaveform()
    {
        if (_waveReady || _path == null)
            return;
        _waveReady = true;
        string tmp = Path.Combine(Path.GetTempPath(), "aerowave_" + Guid.NewGuid().ToString("N") + ".png");
        bool ok = await FFmpeg.WaveformAsync(_path, tmp, 1600, 240, WaveColor);
        if (ok && File.Exists(tmp))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(tmp);
                bmp.EndInit();
                bmp.Freeze();
                Audio.SetWaveform(bmp);
            }
            catch { }
        }
        TryDelete(tmp);
    }

    void ReadVideoSize()
    {
        int w = 0, h = 0;
        if (_player.Media != null)
        {
            foreach (var track in _player.Media.Tracks)
            {
                if (track.TrackType == TrackType.Video)
                {
                    w = (int)track.Data.Video.Width;
                    h = (int)track.Data.Video.Height;
                    break;
                }
            }
        }
        if (w <= 0 || h <= 0)
        {
            uint uw = 0, uh = 0;
            _player.Size(0, ref uw, ref uh);
            w = (int)uw;
            h = (int)uh;
        }
        _videoW = w;
        _videoH = h;
        _cropX = 0;
        _cropY = 0;
        _cropW = _videoW;
        _cropH = _videoH;
        _cropActive = false;
    }

    void OnCropChanged()
    {
        CropXBox.Text = Crop.CropX.ToString(CultureInfo.InvariantCulture);
        CropYBox.Text = Crop.CropY.ToString(CultureInfo.InvariantCulture);
        CropWBox.Text = Crop.CropW.ToString(CultureInfo.InvariantCulture);
        CropHBox.Text = Crop.CropH.ToString(CultureInfo.InvariantCulture);

        _cropX = Crop.CropX;
        _cropY = Crop.CropY;
        _cropW = Crop.CropW;
        _cropH = Crop.CropH;
        _cropActive = _cropX != 0 || _cropY != 0 || _cropW != _videoW || _cropH != _videoH;
    }

    void OnCropReset(object sender, RoutedEventArgs e) => Crop.SetCrop(0, 0, _videoW, _videoH);

    void OnCropBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitCropBoxes(); Defocus(); }
    }

    void OnCropBoxCommit(object sender, RoutedEventArgs e) => CommitCropBoxes();

    void CommitCropBoxes()
    {
        if (_tab != Tab.Crop)
            return;
        Crop.SetCrop(
            ParseInt(CropXBox.Text, Crop.CropX),
            ParseInt(CropYBox.Text, Crop.CropY),
            ParseInt(CropWBox.Text, Crop.CropW),
            ParseInt(CropHBox.Text, Crop.CropH));
    }

    void OnGlobalVolumeChanged(double volume)
    {
        _volume = volume / 100.0;
        if (UseKeyframes.IsChecked != true)
            _player.Volume = (int)Math.Round(volume);
        KfVolBox.Text = (int)Math.Round(volume) + "%";
    }

    void UpdateAudioControls(bool keyframed)
    {
        var vis = keyframed ? Visibility.Visible : Visibility.Collapsed;
        KfTimeLabel.Visibility = vis;
        KfTimeBox.Visibility = vis;
        AudioDivider.Visibility = vis;
        AddKfButton.Visibility = vis;
        RemoveKfButton.Visibility = vis;
    }

    void OnUseKeyframes(object sender, RoutedEventArgs e)
    {
        bool on = UseKeyframes.IsChecked == true;
        if (on)
        {
            EnsureWaveform();
            Audio.SetKeyframeMode();
            if (Audio.Keyframes.Count == 0 && _lengthReady)
                Audio.AddKeyframe(0, Audio.GlobalVolume);
        }
        else
        {
            Audio.SetGlobalMode(Audio.GlobalVolume);
            _volume = Audio.GlobalVolume / 100.0;
            _player.Volume = (int)Math.Round(Audio.GlobalVolume);
            KfVolBox.Text = (int)Math.Round(Audio.GlobalVolume) + "%";
        }
        UpdateAudioControls(on);
    }

    void OnAddKeyframe(object sender, RoutedEventArgs e)
    {
        if (!_lengthReady)
            return;
        double t = _player.Time / 1000.0;
        Audio.AddKeyframe(t, VolumeAt(t));
    }

    void OnRemoveKeyframe(object sender, RoutedEventArgs e) => Audio.RemoveSelected();

    void OnKfSelectionChanged()
    {
        bool has = Audio.HasSelection;
        RemoveKfButton.IsEnabled = has;
        if (has)
        {
            KfTimeBox.Text = Fmt(Audio.SelectedTime);
            KfVolBox.Text = (int)Math.Round(Audio.SelectedVolume) + "%";
        }
    }

    void OnKeyframesChanged()
    {
        if (Audio.HasSelection)
        {
            KfTimeBox.Text = Fmt(Audio.SelectedTime);
            KfVolBox.Text = (int)Math.Round(Audio.SelectedVolume) + "%";
        }
    }

    void OnKfBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitKeyframe(); Defocus(); }
    }

    void OnKfBoxCommit(object sender, RoutedEventArgs e) => CommitKeyframe();

    void CommitKeyframe()
    {
        if (UseKeyframes.IsChecked == true)
        {
            if (!Audio.HasSelection)
                return;
            double t = TryParse(KfTimeBox.Text, out double parsed) ? parsed : Audio.SelectedTime;
            int v = ParseInt(KfVolBox.Text.Replace("%", "").Trim(), (int)Math.Round(Audio.SelectedVolume));
            Audio.UpdateSelected(Math.Clamp(t, 0, _duration), Math.Clamp(v, 0, 300));
        }
        else
        {
            int v = ParseInt(KfVolBox.Text.Replace("%", "").Trim(), (int)Math.Round(Audio.GlobalVolume));
            Audio.SetGlobalVolume(Math.Clamp(v, 0, 300));
        }
    }

    double VolumeAt(double time)
    {
        double v = 100;
        foreach (var (kt, kv) in Audio.Keyframes)
        {
            if (kt <= time)
                v = kv;
            else
                break;
        }
        return v;
    }

    void OnStartKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitStart(); Defocus(); }
    }

    void OnStartCommit(object sender, RoutedEventArgs e) => CommitStart();

    void CommitStart()
    {
        if (!_lengthReady)
            return;
        if (TryParse(StartBox.Text, out double t))
        {
            _start = Math.Clamp(t, 0, _end - 0.05);
            Timeline.Start = _start;
            Seek(_start);
        }
        StartBox.Text = Fmt(_start);
    }

    void OnEndKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitEnd(); Defocus(); }
    }

    void OnEndCommit(object sender, RoutedEventArgs e) => CommitEnd();

    void CommitEnd()
    {
        if (!_lengthReady)
            return;
        if (TryParse(EndBox.Text, out double t))
        {
            _end = Math.Clamp(t, _start + 0.05, _duration);
            Timeline.End = _end;
            Seek(_end);
        }
        EndBox.Text = Fmt(_end);
    }

    void OnExportAsNew(object sender, RoutedEventArgs e)
    {
        if (_path == null)
            return;
        _player.SetPause(true);
        string ext = Path.GetExtension(_path).ToLowerInvariant();
        FormatCombo.SelectedIndex = ext switch { ".mov" => 1, ".mkv" => 2, ".webm" => 3, ".avi" => 4, _ => 0 };
        VideoHost.Visibility = Visibility.Collapsed;
        ExportOverlay.Visibility = Visibility.Visible;
    }

    void OnExportCancel(object sender, RoutedEventArgs e)
    {
        ExportOverlay.Visibility = Visibility.Collapsed;
        ApplyTabVisuals();
    }

    async void OnExportConfirm(object sender, RoutedEventArgs e)
    {
        if (_path == null)
            return;

        string ext = SelectedFormat();
        ExportOverlay.Visibility = Visibility.Collapsed;
        var dlg = new SaveFileDialog
        {
            Filter = FilterFor(ext),
            FileName = Path.GetFileNameWithoutExtension(_path) + "_edited",
            DefaultExt = ext,
            AddExtension = true,
            InitialDirectory = Path.GetDirectoryName(_path)
        };
        if (dlg.ShowDialog() != true)
        {
            ApplyTabVisuals();
            return;
        }

        bool compress = CompressToggle.IsChecked == true;
        int quality = (int)Math.Round(CompressSlider.Value);
        await Export(dlg.FileName, compress, quality, false);
    }

    async void OnUpdateOriginal(object sender, RoutedEventArgs e)
    {
        if (_path == null)
            return;

        var confirm = System.Windows.MessageBox.Show(
            "This will overwrite the original file with the edited version. Continue?",
            "Update Original", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        _player.SetPause(true);
        await Export(_path, false, 0, true);
    }

    async Task Export(string output, bool compress, int quality, bool replaceOriginal)
    {
        string input = _path!;
        double start = _start;
        double duration = Math.Max(0.05, _end - _start);
        string ext = Path.GetExtension(replaceOriginal ? input : output);
        string workDir = Path.GetDirectoryName(replaceOriginal ? input : output)!;
        string work = replaceOriginal
            ? Path.Combine(workDir, Guid.NewGuid().ToString("N") + ext)
            : output;

        bool sameExt = string.Equals(Path.GetExtension(work), Path.GetExtension(input),
            StringComparison.OrdinalIgnoreCase);
        string? crop = _cropActive
            ? $"crop={_cropW - _cropW % 2}:{_cropH - _cropH % 2}:{_cropX}:{_cropY}"
            : null;
        string? audioFilter = BuildAudioFilter(start, _end);
        bool couldCopy = !compress && audioFilter == null && crop == null && sameExt;

        _canceled = false;
        _cts = new CancellationTokenSource();
        VideoHost.Visibility = Visibility.Collapsed;
        BusyText.Text = replaceOriginal ? "Updating original" : "Exporting";
        ExportProgress.Value = 0;
        ProgressText.Text = "0%";
        BusyOverlay.Visibility = Visibility.Visible;

        var progress = new Progress<double>(p =>
        {
            ExportProgress.Value = p * 100;
            ProgressText.Text = (int)(p * 100) + "%";
        });

        try
        {
            try
            {
                await FFmpeg.RunAsync(
                    FFmpeg.BuildArgs(input, work, start, duration, audioFilter, compress, quality, crop, false),
                    duration, progress, p => _proc = p, _cts.Token);
            }
            catch (Exception) when (couldCopy && !_canceled)
            {
                await FFmpeg.RunAsync(
                    FFmpeg.BuildArgs(input, work, start, duration, audioFilter, compress, quality, crop, true),
                    duration, progress, p => _proc = p, _cts.Token);
            }

            if (replaceOriginal)
            {
                var current = _player.Media;
                _player.Stop();
                current?.Dispose();
                File.Delete(input);
                File.Move(work, input);
            }

            BusyOverlay.Visibility = Visibility.Collapsed;
            System.Windows.MessageBox.Show(
                replaceOriginal ? "Original updated." : "Export complete.",
                "AeroCut", MessageBoxButton.OK, MessageBoxImage.Information);

            if (replaceOriginal)
                Load(input);
            else
                ApplyTabVisuals();
        }
        catch (Exception ex)
        {
            BusyOverlay.Visibility = Visibility.Collapsed;
            TryDelete(work);
            if (!_canceled)
            {
                System.Windows.MessageBox.Show(ex.Message, "Export failed",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ApplyTabVisuals();
            }
            else if (replaceOriginal)
                Load(input);
            else
                ApplyTabVisuals();
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _proc = null;
        }
    }

    string? BuildAudioFilter(double start, double end)
    {
        if (UseKeyframes.IsChecked == true)
        {
            var keys = Audio.Keyframes;
            double baseV = 100;
            foreach (var (t, v) in keys)
            {
                if (t <= start)
                    baseV = v;
                else
                    break;
            }

            var pts = new List<(double t, double v)> { (0, baseV) };
            foreach (var (t, v) in keys)
                if (t > start && t < end)
                    pts.Add((t - start, v));

            if (pts.Count == 1 && Math.Abs(pts[0].v - 100) < 0.01)
                return null;

            var sb = new StringBuilder();
            sb.Append(Inv(pts[0].v / 100.0));
            for (int i = 1; i < pts.Count; i++)
            {
                double delta = (pts[i].v - pts[i - 1].v) / 100.0;
                sb.Append(delta >= 0 ? "+" : "-");
                sb.Append(Inv(Math.Abs(delta)));
                sb.Append("*gte(t,");
                sb.Append(Inv(pts[i].t));
                sb.Append(')');
            }
            return "volume=eval=frame:volume='" + sb + "'";
        }

        if (Math.Abs(_volume - 1.0) <= 0.001)
            return null;
        return "volume=" + Inv(_volume);
    }

    void OnCancelExport(object sender, RoutedEventArgs e)
    {
        _canceled = true;
        _cts?.Cancel();
        try { _proc?.Kill(true); }
        catch { }
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _canceled = true;
        _cts?.Cancel();
        try { _proc?.Kill(true); }
        catch { }
        _timer.Stop();
        _player.Stop();
        _player.Dispose();
        _libvlc.Dispose();
    }

    void Defocus() => Keyboard.ClearFocus();

    static string SelectedFormatTag(System.Windows.Controls.ComboBoxItem? item)
        => item?.Tag as string ?? ".mp4";

    string SelectedFormat()
        => SelectedFormatTag(FormatCombo.SelectedItem as System.Windows.Controls.ComboBoxItem);

    static string FilterFor(string ext) => ext switch
    {
        ".mov" => "MOV|*.mov",
        ".mkv" => "MKV|*.mkv",
        ".webm" => "WebM|*.webm",
        ".avi" => "AVI|*.avi",
        _ => "MP4|*.mp4"
    };

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    static string Fmt(double seconds)
    {
        if (seconds < 0)
            seconds = 0;
        return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    static int ParseInt(string text, int fallback)
        => int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : fallback;

    static bool TryParse(string text, out double seconds)
    {
        text = text.Trim();
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts))
        {
            seconds = ts.TotalSeconds;
            return true;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return true;
        seconds = 0;
        return false;
    }
}
