using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace KBEA.Diag;

/// <summary>Logo 工具：抠白底 → 裁剪内容 → 输出应用内 PNG 与多尺寸 ICO。</summary>
internal static class LogoTool
{
    public static void Run(string[] args)
    {
        var source = args.Length > 1 ? args[1] : throw new ArgumentException("需要源图片路径");
        var outputDir = args.Length > 2 ? args[2] : throw new ArgumentException("需要输出目录");
        Directory.CreateDirectory(outputDir);

        using var original = new Bitmap(source);
        Console.WriteLine($"源图: {original.Width} x {original.Height}");

        // 1) 抠掉近白背景，保留抗锯齿边缘
        using var transparent = new Bitmap(original.Width, original.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < original.Height; y++)
        {
            for (var x = 0; x < original.Width; x++)
            {
                var pixel = original.GetPixel(x, y);
                // 用亮度与饱和度共同判断"接近白色"，避免误伤图中高光
                var brightness = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B));
                var minChannel = Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                var isWhite = brightness >= 246 && brightness - minChannel <= 8;
                transparent.SetPixel(x, y, isWhite ? Color.FromArgb(0, pixel.R, pixel.G, pixel.B) : pixel);
            }
        }

        // 2) 找内容边界
        var minX = transparent.Width;
        var maxX = -1;
        var minY = transparent.Height;
        var maxY = -1;
        for (var y = 0; y < transparent.Height; y++)
        {
            for (var x = 0; x < transparent.Width; x++)
            {
                if (transparent.GetPixel(x, y).A <= 8) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < 0) throw new InvalidOperationException("未找到非透明内容");

        var contentW = maxX - minX + 1;
        var contentH = maxY - minY + 1;
        Console.WriteLine($"内容边界: x[{minX}..{maxX}] y[{minY}..{maxY}] → {contentW} x {contentH}");

        // 3) 裁剪 + 居中留白成正方形
        var side = (int)Math.Ceiling(Math.Max(contentW, contentH) * 1.02);
        using var square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(square))
        {
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.DrawImage(transparent,
                new Rectangle((side - contentW) / 2 - minX, (side - contentH) / 2 - minY, contentW, contentH),
                new Rectangle(minX, minY, contentW, contentH),
                GraphicsUnit.Pixel);
        }

        // 4) 输出各尺寸 PNG
        foreach (var size in new[] { 256, 128, 64, 32 })
        {
            using var scaled = Resize(square, size, size);
            var path = Path.Combine(outputDir, $"logo-{size}.png");
            scaled.Save(path, ImageFormat.Png);
            Console.WriteLine($"输出 {path}");
        }

        // 5) 输出应用内用的大图
        using (var appLogo = Resize(square, 512, 512))
        {
            var path = Path.Combine(outputDir, "logo.png");
            appLogo.Save(path, ImageFormat.Png);
            Console.WriteLine($"输出 {path}");
        }

        // 6) 生成 ICO（16/24/32/48/64/128/256）
        var icoSizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        using var icoBuffer = new MemoryStream();
        WriteIco(icoBuffer, square, icoSizes);
        var icoPath = Path.Combine(outputDir, "app.ico");
        File.WriteAllBytes(icoPath, icoBuffer.ToArray());
        Console.WriteLine($"输出 {icoPath} ({icoPath.Length} bytes)");
    }

    private static Bitmap Resize(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(target);
        graphics.Clear(Color.Transparent);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return target;
    }

    private static void WriteIco(Stream stream, Bitmap source, int[] sizes)
    {
        // ICO 容器：6 字节头 + 每个尺寸 16 字节目录项 + PNG 数据
        var images = sizes.Select(size => (Size: size, Data: ToPng(Resize(source, size, size)))).ToArray();

        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((ushort)0);              // reserved
            writer.Write((ushort)1);              // type = icon
            writer.Write((ushort)images.Length);

            var offset = 6 + images.Length * 16;
            foreach (var (size, data) in images)
            {
                writer.Write((byte)(size >= 256 ? 0 : size));  // width
                writer.Write((byte)(size >= 256 ? 0 : size));  // height
                writer.Write((byte)0);                         // 调色板数
                writer.Write((byte)0);                         // reserved
                writer.Write((ushort)1);                       // color planes
                writer.Write((ushort)32);                      // bpp
                writer.Write(data.Length);
                writer.Write(offset);
                offset += data.Length;
            }
            foreach (var (_, data) in images) writer.Write(data);
            writer.Flush();
        }
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }
}
