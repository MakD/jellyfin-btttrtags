using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.BtttrTags.Common;
using SkiaSharp;

namespace Jellyfin.Plugin.BtttrTags.Drawing;

public static class PosterOverlayDrawer
{
    private static SKTypeface? _interTypeface;
    private static readonly object _fontLock = new();

    private static SKTypeface GetTypeface()
    {
        if (_interTypeface != null) return _interTypeface;

        lock (_fontLock)
        {
            if (_interTypeface != null) return _interTypeface;

            var asm = Assembly.GetExecutingAssembly();
            using var stream = asm.GetManifestResourceStream("Jellyfin.Plugin.BtttrTags.Resources.Inter-Bold.ttf");
            if (stream != null)
            {
                _interTypeface = SKTypeface.FromStream(stream);
            }
            _interTypeface ??= SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            return _interTypeface;
        }
    }

    public static byte[] RenderPosterWithTag(Stream imageStream, TagResult tag)
    {
        using var originalBitmap = SKBitmap.Decode(imageStream);
        if (originalBitmap == null) throw new InvalidOperationException("Could not decode image stream.");

        int w = originalBitmap.Width;
        int h = originalBitmap.Height;

        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888));
        var canvas = surface.Canvas;

        canvas.DrawBitmap(originalBitmap, 0, 0);

        float vignetteHeight = h * 0.12f;
        using (var vignetteShader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(0, vignetteHeight),
            new[] { new SKColor(0, 0, 0, 130), new SKColor(0, 0, 0, 0) },
            new[] { 0.0f, 1.0f },
            SKShaderTileMode.Clamp
        ))
        using (var vignettePaint = new SKPaint { Shader = vignetteShader })
        {
            canvas.DrawRect(0, 0, w, vignetteHeight, vignettePaint);
        }

        SKColor tabBg;
        SKColor tabText;

        if (tag.IsGold)
        {
            tabBg = new SKColor(214, 158, 93);
            tabText = new SKColor(17, 24, 39);
        }
        else
        {
            (tabBg, tabText) = ExtractColors(originalBitmap);
        }

        var typeface = GetTypeface();
        float fontSize = Math.Max(28f, w * 0.072f);
        using var font = new SKFont(typeface, fontSize);

        font.MeasureText(tag.Text, out var textBounds);

        float horizPadding = fontSize * 1.35f;
        float tabWidth = textBounds.Width + (horizPadding * 2);
        float tabHeight = fontSize * 1.85f;
        float tabX = (w - tabWidth) / 2f;
        float radius = tabHeight * 0.42f;

        using var rrect = new SKRoundRect();
        rrect.SetRectRadii(new SKRect(tabX, 0, tabX + tabWidth, tabHeight), new[]
        {
            new SKPoint(0, 0),
            new SKPoint(0, 0),
            new SKPoint(radius, radius),
            new SKPoint(radius, radius)
        });

        using (var shadowFilter = SKImageFilter.CreateDropShadow(0, 6, 12, 12, new SKColor(0, 0, 0, 130)))
        using (var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 110),
            ImageFilter = shadowFilter
        })
        {
            canvas.DrawRoundRect(rrect, shadowPaint);
        }

        using (var tabPaint = new SKPaint
        {
            Color = tabBg,
            IsAntialias = true
        })
        {
            canvas.DrawRoundRect(rrect, tabPaint);
        }

        using (var textPaint = new SKPaint
        {
            Color = tabText,
            IsAntialias = true
        })
        {
            float textX = tabX + (tabWidth - textBounds.Width) / 2f - textBounds.Left;
            float textY = (tabHeight - textBounds.Height) / 2f - textBounds.Top - (fontSize * 0.04f);
            canvas.DrawText(tag.Text, textX, textY, font, textPaint);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    private static (SKColor bg, SKColor text) ExtractColors(SKBitmap bmp)
    {
        using var small = bmp.Resize(new SKImageInfo(30, 45), SKSamplingOptions.Default);
        if (small == null) return (new SKColor(24, 24, 27), SKColors.White);

        var colorCounts = new Dictionary<string, float>();
        for (int y = 5; y < 40; y++)
        {
            for (int x = 0; x < 30; x++)
            {
                var c = small.GetPixel(x, y);
                int max = Math.Max(c.Red, Math.Max(c.Green, c.Blue));
                int min = Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                if (max < 55 || min > 225) continue;
                float sat = (float)(max - min) / max;
                if (sat < 0.20f) continue;

                int qr = (c.Red / 16) * 16 + 8;
                int qg = (c.Green / 16) * 16 + 8;
                int qb = (c.Blue / 16) * 16 + 8;
                string key = $"{qr},{qg},{qb}";
                float weight = 1.0f + (sat * 2.0f);
                colorCounts[key] = colorCounts.GetValueOrDefault(key, 0f) + weight;
            }
        }

        SKColor chosen = new SKColor(24, 24, 27);
        float maxScore = 0f;
        string? bestKey = null;
        foreach (var kvp in colorCounts)
        {
            if (kvp.Value > maxScore)
            {
                maxScore = kvp.Value;
                bestKey = kvp.Key;
            }
        }

        if (bestKey != null && maxScore >= 3.5f)
        {
            var parts = bestKey.Split(',');
            chosen = new SKColor(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
        }

        float lum = (chosen.Red * 299 + chosen.Green * 587 + chosen.Blue * 114) / 1000f;
        SKColor textColor = lum > 140 ? new SKColor(15, 23, 42) : SKColors.White;
        return (chosen, textColor);
    }
}