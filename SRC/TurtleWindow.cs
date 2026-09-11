using System.Runtime.InteropServices;
using SkiaSharp;

namespace BBC
{
    internal sealed class TurtleWindow : IDisposable
    {
        private const int Width = 720;
        private const int Height = 826;
        private const float PaperSize = 680;
        private readonly Func<JessopTurtle?> turtle;
        private readonly Action drawingCleared;
        private SKBitmap? bitmap;
        private readonly SKPaint paint = new() { IsAntialias = true };
        private readonly SKTypeface typeface = SKTypeface.FromFamilyName("Arial");
        private readonly SKBitmap turtleBitmap;
        private SKBitmap? gridBitmap;
        private IntPtr window;
        private IntPtr renderer;
        private IntPtr texture;
        private uint windowId;
        private bool visible;
        private int viewMillimetres = 3000;
        private int pressed = -1;
        private string message = "Save PNG exports the full 3 m floor, without the grid or turtle.";

        internal TurtleWindow(Func<JessopTurtle?> turtle, Action drawingCleared)
        {
            this.turtle = turtle;
            this.drawingCleared = drawingCleared;
            using Stream sprite = typeof(TurtleWindow).Assembly.GetManifestResourceStream("BBC.JessopTurtle.png")
                ?? throw new InvalidOperationException("Missing turtle image.");
            turtleBitmap = SKBitmap.Decode(sprite);
        }

        internal void Show()
        {
            if (window == IntPtr.Zero)
            {
                window = SDL_CreateWindow("Turtle — Drawing Floor", 0x2FFF0000, 0x2FFF0000, Width, Height, 0x2004);
                if (window == IntPtr.Zero) throw new InvalidOperationException("Could not create turtle window.");
                renderer = SDL_CreateRenderer(window, -1, 2);
                if (renderer == IntPtr.Zero) renderer = SDL_CreateRenderer(window, -1, 1);
                if (renderer == IntPtr.Zero) throw new InvalidOperationException("Could not create turtle renderer.");
                windowId = SDL_GetWindowID(window);
            }
            visible = true;
            SDL_ShowWindow(window);
            SDL_RaiseWindow(window);
        }

        internal void Hide()
        {
            visible = false;
            pressed = -1;
            if (window != IntPtr.Zero) SDL_HideWindow(window);
        }

        internal bool HandleEvent(uint type, uint id, byte windowEvent, byte button, int mouseX, int mouseY)
        {
            if (windowId == 0 || id != windowId) return false;
            if (type == 0x200 && windowEvent == 14)
                Hide();
            else if (type == 0x200 && windowEvent == 13) pressed = -1;
            else if (type == 0x401 && button == 1)
            {
                pressed = ButtonAt(mouseX, mouseY);
            }
            else if (type == 0x402 && button == 1)
            {
                int selected = pressed;
                pressed = -1;
                if (selected >= 0 && selected == ButtonAt(mouseX, mouseY)) Activate(selected);
            }
            return true;
        }

        private static SKRect Button(int index) => index >= 9
            ? new SKRect(index == 9 ? 486 : 590, 56, index == 9 ? 582 : 700, 86)
            : index >= 5
            ? new SKRect(70 + (index - 5) * 94, 56, 156 + (index - 5) * 94, 86)
            : index < 3
            ? new SKRect(162 + index * 82, 14, 240 + index * 82, 48)
            : new SKRect(index == 3 ? 486 : 590, 14, index == 3 ? 582 : 700, 48);

        private static int ButtonAt(float x, float y)
        {
            for (int i = 0; i < 11; i++) if (Button(i).Contains(x, y)) return i;
            return -1;
        }

        private void Activate(int index)
        {
            if (index == 9)
            {
                try
                {
                    string? path = Display.SelectNativeTurtleGridImage();
                    if (path is not null) LoadGridImage(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    message = $"Grid load failed: {ex.Message}";
                }
            }
            else if (index == 10)
            {
                gridBitmap?.Dispose();
                gridBitmap = null;
                message = "Default 0.25 m grid restored.";
            }
            else if (index >= 5)
            {
                drawingCleared();
                turtle()?.SetPenColour(PenColour(index));
            }
            else if (index < 3) viewMillimetres = (3 - index) * 1000;
            else if (index == 3)
            {
                drawingCleared();
                turtle()?.ClearDrawing();
                message = "Drawing cleared; turtle position preserved.";
            }
            else
            {
                TurtleDrawing? drawing = turtle()?.CaptureDrawing();
                if (drawing is null) return;
                try
                {
                    Directory.CreateDirectory("Drawings");
                    string path = Path.Combine("Drawings", $"Turtle-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
                    SaveDrawing(path, drawing);
                    message = $"Saved {path}";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    message = $"Save failed: {ex.Message}";
                }
            }
        }

        private void LoadGridImage(string path)
        {
            using Stream file = File.OpenRead(path);
            SKBitmap loaded = SKBitmap.Decode(file)
                ?? throw new InvalidDataException("Choose a valid PNG, JPEG, BMP or WebP image.");
            gridBitmap?.Dispose();
            gridBitmap = loaded;
            message = "Custom grid loaded at 30% opacity.";
        }

        internal static void SaveDrawing(string path, TurtleDrawing drawing)
        {
            using SKBitmap paper = new(3000, 3000);
            using SKCanvas canvas = new(paper);
            canvas.Clear(SKColors.White);
            canvas.Translate(1500, 1500);
            DrawMarks(canvas, drawing);
            using SKImage image = SKImage.FromBitmap(paper);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            using FileStream file = new(path, FileMode.CreateNew);
            png.SaveTo(file);
        }

        internal void Render()
        {
            if (!visible || renderer == IntPtr.Zero) return;
            if (SDL_GetRendererOutputSize(renderer, out int pixelWidth, out int pixelHeight) != 0
                || pixelWidth <= 0 || pixelHeight <= 0) return;
            // Rasterise at the display's pixel density, including Retina screens.
            if (bitmap is null || bitmap.Width != pixelWidth || bitmap.Height != pixelHeight)
            {
                if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
                bitmap?.Dispose();
                bitmap = new SKBitmap(pixelWidth, pixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                texture = SDL_CreateTexture(renderer, 0x16362004, 1, pixelWidth, pixelHeight);
                if (texture == IntPtr.Zero) throw new InvalidOperationException("Could not create turtle texture.");
            }
            using SKCanvas canvas = new(bitmap);
            canvas.Scale(pixelWidth / (float)Width, pixelHeight / (float)Height);
            Draw(canvas, turtle()?.CaptureDrawing());
            SDL_UpdateTexture(texture, IntPtr.Zero, bitmap.GetPixels(), bitmap.RowBytes);
            SDL_SetRenderDrawColor(renderer, 24, 28, 31, 255);
            SDL_RenderClear(renderer);
            SDL_RenderCopy(renderer, texture, IntPtr.Zero, IntPtr.Zero);
            SDL_RenderPresent(renderer);
        }

        internal void Draw(SKCanvas canvas, TurtleDrawing? drawing)
        {
            canvas.Clear(new SKColor(24, 28, 31));
            Text(canvas, "Turtle", 20, 37, 16, new SKColor(225, 233, 236));
            string[] labels = ["3 m × 3 m", "2 m × 2 m", "1 m × 1 m", "Clear", "Save PNG"];
            for (int i = 0; i < labels.Length; i++)
            {
                bool selected = i < 3 && viewMillimetres == (3 - i) * 1000;
                paint.Style = SKPaintStyle.Fill;
                paint.Color = pressed == i ? new SKColor(65, 118, 138) : selected ? new SKColor(42, 75, 89) : new SKColor(43, 48, 52);
                canvas.DrawRoundRect(Button(i), 4, 4, paint);
                Text(canvas, labels[i], Button(i).MidX, 36, 15, SKColors.White, true);
            }
            Text(canvas, "Pen", 20, 76, 14, new SKColor(214, 224, 227));
            for (int i = 5; i < 9; i++)
            {
                TurtlePenColour colour = PenColour(i);
                bool selected = (drawing?.Colour ?? TurtlePenColour.Black) == colour;
                paint.Color = pressed == i ? new SKColor(65, 118, 138) : selected ? new SKColor(42, 75, 89) : new SKColor(43, 48, 52);
                canvas.DrawRoundRect(Button(i), 4, 4, paint);
                paint.Color = InkColour(colour);
                canvas.DrawCircle(Button(i).Left + 13, 71, 5, paint);
                if (selected)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1;
                    paint.Color = new SKColor(180, 211, 224);
                    canvas.DrawRoundRect(Button(i), 4, 4, paint);
                    paint.Style = SKPaintStyle.Fill;
                }
                Text(canvas, colour.ToString(), Button(i).Left + 25, 76, 14, SKColors.White);
            }
            for (int i = 9; i < 11; i++)
            {
                paint.Color = pressed == i ? new SKColor(65, 118, 138) : new SKColor(43, 48, 52);
                canvas.DrawRoundRect(Button(i), 4, 4, paint);
                Text(canvas, i == 9 ? "Load grid" : "Default grid", Button(i).MidX, 76, 14, SKColors.White, true);
            }
            SKRect paper = new(20, 100, 700, 780);
            paint.Color = SKColors.White;
            canvas.DrawRect(paper, paint);
            canvas.Save();
            canvas.ClipRect(paper);
            canvas.Translate(paper.MidX, paper.MidY);
            float scale = PaperSize / viewMillimetres;
            canvas.Scale(scale);
            float half = viewMillimetres / 2f;
            if (gridBitmap is not null)
            {
                // Centre-crop to fill the 3 m floor without stretching. Zoom keeps
                // the reference image aligned with the ink's physical coordinates.
                float side = Math.Min(gridBitmap.Width, gridBitmap.Height);
                float left = (gridBitmap.Width - side) / 2;
                float top = (gridBitmap.Height - side) / 2;
                using SKPaint background = new()
                {
                    Color = SKColors.White.WithAlpha(77),
                    FilterQuality = SKFilterQuality.High,
                    IsAntialias = true
                };
                canvas.DrawBitmap(gridBitmap, new SKRect(left, top, left + side, top + side),
                    new SKRect(-1500, -1500, 1500, 1500), background);
            }
            else
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 0.7f / scale;
                paint.Color = new SKColor(228, 231, 231);
                for (float offset = -half; offset <= half; offset += 250)
                {
                    canvas.DrawLine(-half, offset, half, offset, paint);
                    canvas.DrawLine(offset, -half, offset, half, paint);
                }
            }
            if (drawing is not null)
            {
                DrawMarks(canvas, drawing);
                canvas.Save();
                canvas.Translate((float)drawing.X, (float)drawing.Y);
                canvas.RotateDegrees((float)(drawing.Heading * 180 / Math.PI));
                DrawTurtle(canvas);
                canvas.Restore();
            }
            canvas.Restore();
            paint.Style = SKPaintStyle.Fill;
            string status = drawing is null ? "Turtle disconnected — enable it in Peripherals."
                : $"X {drawing.X / 1000:+0.000;-0.000;0.000} m   Y {-drawing.Y / 1000:+0.000;-0.000;0.000} m   Heading {(drawing.Heading * 180 / Math.PI + 360) % 360:0.0}°   Pen {(drawing.PenDown ? "down" : "up")}";
            if (drawing is not null && (Math.Abs(drawing.X) > half || Math.Abs(drawing.Y) > half)) status += "   Outside view";
            Text(canvas, status, 20, 800, 14, new SKColor(214, 224, 227));
            Text(canvas, (gridBitmap is null ? "Grid: 0.25 m   •   " : "Custom grid   •   ") + message, 20, 819, 12, new SKColor(151, 167, 173));
        }

        private static void DrawMarks(SKCanvas canvas, TurtleDrawing drawing)
        {
            using SKPaint ink = new() { IsAntialias = true, Color = new SKColor(27, 37, 46), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            using SKPath path = new();
            TurtlePenColour colour = TurtlePenColour.Black;
            foreach (TurtleMark mark in drawing.Marks)
            {
                if (mark.Start || mark.Colour != colour)
                {
                    ink.Color = InkColour(colour);
                    canvas.DrawPath(path, ink);
                    path.Reset();
                    path.MoveTo(mark.X, mark.Y);
                    colour = mark.Colour;
                }
                else path.LineTo(mark.X, mark.Y);
            }
            if (drawing.PenDown && drawing.Marks.Length > 0) path.LineTo((float)drawing.X, (float)drawing.Y);
            ink.Color = InkColour(colour);
            canvas.DrawPath(path, ink);
        }

        private static TurtlePenColour PenColour(int button) => button switch
        {
            5 => TurtlePenColour.Red,
            6 => TurtlePenColour.Blue,
            7 => TurtlePenColour.Green,
            _ => TurtlePenColour.Black
        };

        private static SKColor InkColour(TurtlePenColour colour) => colour switch
        {
            TurtlePenColour.Red => new SKColor(220, 30, 30),
            TurtlePenColour.Blue => new SKColor(30, 80, 220),
            TurtlePenColour.Green => new SKColor(0, 140, 50),
            _ => SKColors.Black
        };

        private void DrawTurtle(SKCanvas canvas)
        {
            // The photographed-style sprite has a white background. Multiply keeps
            // the floor visible through the clear dome and outside the circular rim.
            using SKPaint sprite = new() { IsAntialias = true, FilterQuality = SKFilterQuality.High, BlendMode = SKBlendMode.Multiply };
            canvas.Save();
            using SKPath outline = new();
            outline.AddOval(new SKRect(-150, -150, 150, 150));
            canvas.ClipPath(outline, antialias: true);
            canvas.DrawBitmap(turtleBitmap, new SKRect(62, 55, 1190, 1159), new SKRect(-150, -150, 150, 150), sprite);
            canvas.Restore();
        }

        private void Text(SKCanvas canvas, string text, float x, float y, float size, SKColor color, bool centre = false)
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Typeface = typeface;
            paint.TextSize = size;
            paint.Color = color;
            paint.TextAlign = centre ? SKTextAlign.Center : SKTextAlign.Left;
            canvas.DrawText(text, x, y, paint);
        }

        public void Dispose()
        {
            if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
            if (renderer != IntPtr.Zero) SDL_DestroyRenderer(renderer);
            if (window != IntPtr.Zero) SDL_DestroyWindow(window);
            bitmap?.Dispose(); gridBitmap?.Dispose(); turtleBitmap.Dispose(); paint.Dispose(); typeface.Dispose();
        }

        [DllImport("SDL2")] private static extern IntPtr SDL_CreateWindow(string title, int x, int y, int w, int h, uint flags);
        [DllImport("SDL2")] private static extern IntPtr SDL_CreateRenderer(IntPtr window, int index, uint flags);
        [DllImport("SDL2")] private static extern IntPtr SDL_CreateTexture(IntPtr renderer, uint format, int access, int w, int h);
        [DllImport("SDL2")] private static extern uint SDL_GetWindowID(IntPtr window);
        [DllImport("SDL2")] private static extern void SDL_ShowWindow(IntPtr window);
        [DllImport("SDL2")] private static extern void SDL_RaiseWindow(IntPtr window);
        [DllImport("SDL2")] private static extern void SDL_HideWindow(IntPtr window);
        [DllImport("SDL2")] private static extern int SDL_GetRendererOutputSize(IntPtr renderer, out int w, out int h);
        [DllImport("SDL2")] private static extern int SDL_UpdateTexture(IntPtr texture, IntPtr rect, IntPtr pixels, int pitch);
        [DllImport("SDL2")] private static extern int SDL_SetRenderDrawColor(IntPtr renderer, byte r, byte g, byte b, byte a);
        [DllImport("SDL2")] private static extern int SDL_RenderClear(IntPtr renderer);
        [DllImport("SDL2")] private static extern int SDL_RenderCopy(IntPtr renderer, IntPtr texture, IntPtr source, IntPtr dest);
        [DllImport("SDL2")] private static extern void SDL_RenderPresent(IntPtr renderer);
        [DllImport("SDL2")] private static extern void SDL_DestroyTexture(IntPtr texture);
        [DllImport("SDL2")] private static extern void SDL_DestroyRenderer(IntPtr renderer);
        [DllImport("SDL2")] private static extern void SDL_DestroyWindow(IntPtr window);
    }
}
