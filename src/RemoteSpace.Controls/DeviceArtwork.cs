using Microsoft.UI.Xaml;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace RemoteSpace.Controls;

/// <summary>Original vector artwork, not a Microsoft wallpaper or remote screenshot.
/// Three normalized pictures are recorded once and reused by virtualized tiles.</summary>
public sealed class DeviceArtwork : SKCanvasElement
{
    private static readonly Lazy<SKPicture[]> Pictures = new(() => [Record(0), Record(1), Record(2)]);
    private int _variant;
    public int Variant { get => _variant; set { int next = Math.Clamp(value, 0, 2); if (_variant != next) { _variant = next; Invalidate(); } } }
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        canvas.Save(); canvas.Scale((float)area.Width / 300, (float)area.Height / 132);
        canvas.DrawPicture(Pictures.Value[_variant]); canvas.Restore();
    }
    private static SKPicture Record(int variant)
    {
        using var recorder = new SKPictureRecorder(); var canvas = recorder.BeginRecording(new SKRect(0, 0, 300, 132));
        SKColor pale = variant switch { 1 => new(228, 206, 229), 2 => new(192, 224, 224), _ => new(184, 223, 241) };
        SKColor deep = variant switch { 1 => new(103, 51, 153), 2 => new(17, 111, 136), _ => new(20, 91, 191) };
        canvas.Clear(pale);
        using var paint = new SKPaint { IsAntialias = true };
        for (int i = 0; i < 6; i++)
        {
            using var shader = SKShader.CreateLinearGradient(new(30, 10 + i * 12), new(270, 130),
                [pale, deep, new SKColor(28, 52, 113)], [0, .55f, 1], SKShaderTileMode.Clamp);
            paint.Shader = shader;
            using var wave = new SKPath(); wave.MoveTo(-30, 86 + i * 12);
            wave.CubicTo(58, -44 + i * 12, 125, 173 - i * 13, 191, 57 + i * 7);
            wave.CubicTo(230, -17 + i * 8, 285, 19 + i * 10, 342, -22 + i * 8);
            wave.LineTo(342, 166); wave.LineTo(-30, 166); wave.Close(); canvas.DrawPath(wave, paint);
            paint.Shader = null;
        }
        return recorder.EndRecording();
    }
}
