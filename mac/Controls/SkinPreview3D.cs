using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using AltsTools.Models;
using AltsTools.Rendering;

namespace AltsTools.Controls
{
    /// <summary>
    /// Real-time 3D Minecraft skin preview. Software-rasterizes the player rig
    /// into a WriteableBitmap (no GPU interop needed), with mouse-drag orbit and
    /// wheel zoom — the cross-platform replacement for the Windows D3D11 control.
    /// </summary>
    public sealed class SkinPreview3D : Control
    {
        public static readonly StyledProperty<byte[]?> SkinBytesProperty =
            AvaloniaProperty.Register<SkinPreview3D, byte[]?>(nameof(SkinBytes));
        public static readonly StyledProperty<MinecraftSkinVariant> SkinVariantProperty =
            AvaloniaProperty.Register<SkinPreview3D, MinecraftSkinVariant>(nameof(SkinVariant));
        public static readonly StyledProperty<int> CameraResetNonceProperty =
            AvaloniaProperty.Register<SkinPreview3D, int>(nameof(CameraResetNonce));
        public static readonly StyledProperty<PreviewBackgroundMode> BackgroundModeProperty =
            AvaloniaProperty.Register<SkinPreview3D, PreviewBackgroundMode>(nameof(BackgroundMode));
        public static readonly StyledProperty<string?> PanoramaKeyProperty =
            AvaloniaProperty.Register<SkinPreview3D, string?>(nameof(PanoramaKey));
        public static readonly StyledProperty<PreviewAnimationMode> AnimationModeProperty =
            AvaloniaProperty.Register<SkinPreview3D, PreviewAnimationMode>(nameof(AnimationMode));

        public byte[]? SkinBytes { get => GetValue(SkinBytesProperty); set => SetValue(SkinBytesProperty, value); }
        public MinecraftSkinVariant SkinVariant { get => GetValue(SkinVariantProperty); set => SetValue(SkinVariantProperty, value); }
        public int CameraResetNonce { get => GetValue(CameraResetNonceProperty); set => SetValue(CameraResetNonceProperty, value); }
        public PreviewBackgroundMode BackgroundMode { get => GetValue(BackgroundModeProperty); set => SetValue(BackgroundModeProperty, value); }
        public string? PanoramaKey { get => GetValue(PanoramaKeyProperty); set => SetValue(PanoramaKeyProperty, value); }
        public PreviewAnimationMode AnimationMode { get => GetValue(AnimationModeProperty); set => SetValue(AnimationModeProperty, value); }

        private readonly SkinRasterizer _rast = new();
        private WriteableBitmap? _bmp;
        private byte[]? _buf;
        private int _w, _h;
        private bool _dragging;
        private Point _last;
        private readonly DispatcherTimer _spin;

        static SkinPreview3D()
        {
            SkinBytesProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => c.OnSkinChanged());
            SkinVariantProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => c.OnVariantChanged());
            CameraResetNonceProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => c.ResetCamera());
            BackgroundModeProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => c.OnBackgroundChanged());
            PanoramaKeyProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => c.OnBackgroundChanged());
            AnimationModeProperty.Changed.AddClassHandler<SkinPreview3D>((c, _) => { c._rast.AnimMode = (int)c.AnimationMode; c.Invalidate(); });
        }

        public SkinPreview3D()
        {
            ClipToBounds = true;
            // advance animation time each frame; gently auto-rotate when idle
            _spin = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _spin.Tick += (_, _) =>
            {
                _rast.Time += 0.033f;
                if (!_dragging) _rast.Yaw += 0.004f;
                Invalidate();
            };
            _spin.Start();
        }

        private Bitmap? _panorama;
        private string? _panoramaLoadedKey;

        private void OnSkinChanged() { _rast.SetSkin(SkinBytes); Invalidate(); }
        private void OnVariantChanged() { _rast.SetVariant(SkinVariant == MinecraftSkinVariant.Slim); Invalidate(); }
        private void ResetCamera() { _rast.Yaw = 0.5f; _rast.Pitch = 0.1f; _rast.Distance = 3.0f; Invalidate(); }

        private void OnBackgroundChanged()
        {
            // Load the panorama image when in Panorama mode and the key changed.
            if (BackgroundMode == PreviewBackgroundMode.Panorama && !string.IsNullOrWhiteSpace(PanoramaKey))
            {
                if (_panoramaLoadedKey != PanoramaKey)
                {
                    _panorama?.Dispose();
                    _panorama = null;
                    try
                    {
                        var uri = new Uri($"avares://AltsTools/Assets/panoramas/{PanoramaKey}/panorama_0.jpg");
                        using var s = Avalonia.Platform.AssetLoader.Open(uri);
                        _panorama = new Bitmap(s);
                        _panoramaLoadedKey = PanoramaKey;
                    }
                    catch { _panorama = null; _panoramaLoadedKey = null; }
                }
            }
            Invalidate();
        }

        // Solid background tint per mode (Panorama uses the image instead).
        private IBrush BackgroundFill() => BackgroundMode switch
        {
            PreviewBackgroundMode.Bright => new SolidColorBrush(Color.Parse("#E9E2F1")),
            PreviewBackgroundMode.Moody  => new SolidColorBrush(Color.Parse("#3A3346")),
            PreviewBackgroundMode.Dark   => new SolidColorBrush(Color.Parse("#15121E")),
            _ => Brushes.Transparent,
        };

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            _dragging = true; _last = e.GetPosition(this); e.Pointer.Capture(this);
        }
        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            _dragging = false; e.Pointer.Capture(null);
        }
        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (!_dragging) return;
            var p = e.GetPosition(this);
            _rast.Yaw   += (float)(p.X - _last.X) * 0.012f;
            _rast.Pitch += (float)(p.Y - _last.Y) * 0.010f;
            _rast.Pitch = Math.Clamp(_rast.Pitch, -1.4f, 1.4f);
            _last = p;
            Invalidate();
        }
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            _rast.Distance = Math.Clamp(_rast.Distance - (float)e.Delta.Y * 0.18f, 1.2f, 6f);
            Invalidate();
        }

        private void Invalidate() => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render);

        public override void Render(DrawingContext ctx)
        {
            int w = Math.Max(1, (int)Bounds.Width);
            int h = Math.Max(1, (int)Bounds.Height);
            if (w < 4 || h < 4) return;

            if (_bmp == null || _w != w || _h != h)
            {
                _w = w; _h = h;
                _bmp?.Dispose();
                _bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                                           PixelFormat.Bgra8888, AlphaFormat.Premul);
                _buf = new byte[w * h * 4];
            }

            var full = new Rect(0, 0, w, h);

            // 1) background: panorama image, or a solid tint per mode.
            if (BackgroundMode == PreviewBackgroundMode.Panorama && _panorama != null)
                ctx.DrawImage(_panorama, full);
            else
                ctx.FillRectangle(BackgroundFill(), full);

            // 2) the model, rendered over a transparent buffer so the bg shows through.
            _rast.Render(_buf!, w, h, 0x00000000u);
            using (var fb = _bmp!.Lock())
            {
                System.Runtime.InteropServices.Marshal.Copy(_buf!, 0, fb.Address, _buf!.Length);
            }
            ctx.DrawImage(_bmp, full);
        }
    }
}
