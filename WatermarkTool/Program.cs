using System;
using System.IO;
using SkiaSharp;

namespace WatermarkTool
{
    /// <summary>
    /// CLI tool: draw a text watermark in the bottom-right corner and re-encode
    /// the image as lossy WebP at quality 80. Two modes:
    ///
    ///   Single-file mode (used by StatiqMarkdownEditor at upload time):
    ///     WatermarkTool --in &lt;path&gt; --out &lt;path&gt; --text &lt;string&gt;
    ///
    ///   Batch mode (one-off historical-image processing):
    ///     WatermarkTool --in-dir &lt;dir&gt; --out-dir &lt;dir&gt; --text &lt;string&gt;
    ///
    /// Exit codes:
    ///   0  success
    ///   1  bad arguments (missing/invalid flags)
    ///   2  processing failure (decode error, IO error, etc.)
    /// </summary>
    class Program
    {
        static int Main(string[] args)
        {
            var opts = ParseArgs(args);
            if (opts.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            if (opts.Error != null)
            {
                Console.Error.WriteLine($"❌ {opts.Error}");
                Console.Error.WriteLine("   Run with --help for usage.");
                return 1;
            }

            try
            {
                if (opts.SingleFile)
                {
                    ProcessSingleFile(opts.InputPath!, opts.OutputPath!, opts.Text!);
                    Console.WriteLine($"✨ {Path.GetFileName(opts.OutputPath)}");
                }
                else
                {
                    ProcessBatch(opts.InputDir!, opts.OutputDir!, opts.Text!);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"❌ {ex.Message}");
                return 2;
            }
        }

        // ----------------------------------------------------------------
        //  Arg parsing
        // ----------------------------------------------------------------

        static Options ParseArgs(string[] args)
        {
            var opts = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        opts.ShowHelp = true;
                        break;
                    case "--in":
                        opts.InputPath = args[++i];
                        opts.SingleFile = true;
                        break;
                    case "--out":
                        opts.OutputPath = args[++i];
                        break;
                    case "--text":
                        opts.Text = args[++i];
                        break;
                    case "--in-dir":
                        opts.InputDir = args[++i];
                        opts.Batch = true;
                        break;
                    case "--out-dir":
                        opts.OutputDir = args[++i];
                        break;
                    default:
                        opts.Error = $"unknown argument: {args[i]}";
                        return opts;
                }
            }

            if (opts.SingleFile && (string.IsNullOrEmpty(opts.InputPath) || string.IsNullOrEmpty(opts.OutputPath)))
            {
                opts.Error = "--in/--out mode requires both --in and --out";
                return opts;
            }
            if (opts.Batch && (string.IsNullOrEmpty(opts.InputDir) || string.IsNullOrEmpty(opts.OutputDir)))
            {
                opts.Error = "--in-dir/--out-dir mode requires both --in-dir and --out-dir";
                return opts;
            }
            if (!opts.SingleFile && !opts.Batch)
            {
                opts.Error = "must specify either --in/--out (single file) or --in-dir/--out-dir (batch)";
                return opts;
            }
            if (string.IsNullOrEmpty(opts.Text))
            {
                opts.Error = "--text is required";
                return opts;
            }
            return opts;
        }

        static void PrintHelp()
        {
            Console.WriteLine("WatermarkTool — draw a text watermark, re-encode as lossy WebP");
            Console.WriteLine();
            Console.WriteLine("USAGE");
            Console.WriteLine("  Single-file mode (image upload):");
            Console.WriteLine("    WatermarkTool --in <path> --out <path> --text <string>");
            Console.WriteLine();
            Console.WriteLine("  Batch mode (one-off historical processing):");
            Console.WriteLine("    WatermarkTool --in-dir <dir> --out-dir <dir> --text <string>");
            Console.WriteLine();
            Console.WriteLine("REQUIRED");
            Console.WriteLine("  --text <string>       Watermark text drawn in the bottom-right corner");
            Console.WriteLine();
            Console.WriteLine("SINGLE-FILE");
            Console.WriteLine("  --in <path>           Source image (jpg / jpeg / png / webp)");
            Console.WriteLine("  --out <path>          Destination .webp path");
            Console.WriteLine();
            Console.WriteLine("BATCH");
            Console.WriteLine("  --in-dir <dir>        Source folder (recursive scan)");
            Console.WriteLine("  --out-dir <dir>       Destination folder (created if missing;");
            Console.WriteLine("                        preserves relative paths and .jpg/.jpeg/.png");
            Console.WriteLine("                        extensions, output is always .webp)");
            Console.WriteLine();
            Console.WriteLine("EXIT CODES");
            Console.WriteLine("  0   success");
            Console.WriteLine("  1   bad arguments");
            Console.WriteLine("  2   processing failure");
        }

        // ----------------------------------------------------------------
        //  Image processing
        // ----------------------------------------------------------------

        static void ProcessSingleFile(string inPath, string outPath, string text)
        {
            if (!File.Exists(inPath))
                throw new FileNotFoundException($"input not found: {inPath}");

            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            using var inputStream = File.OpenRead(inPath);
            using var bitmap = SKBitmap.Decode(inputStream);
            if (bitmap == null)
                throw new InvalidOperationException($"could not decode: {inPath}");

            ApplyWatermarkAndSave(bitmap, outPath, text);
        }

        static void ProcessBatch(string inDir, string outDir, string text)
        {
            if (!Directory.Exists(inDir))
                throw new DirectoryNotFoundException($"input dir not found: {inDir}");

            var files = Directory.GetFiles(inDir, "*.*", SearchOption.AllDirectories);
            Console.WriteLine($"📂 Found {files.Length} files in {Path.GetFullPath(inDir)}");
            int ok = 0, skipped = 0, errors = 0;

            foreach (var file in files)
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".jpg" && ext != ".jpeg" && ext != ".png" && ext != ".webp")
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var rel = Path.GetRelativePath(inDir, file);
                    var outPath = Path.Combine(outDir, Path.ChangeExtension(rel, ".webp"));
                    var outSubdir = Path.GetDirectoryName(outPath);
                    if (!string.IsNullOrEmpty(outSubdir)) Directory.CreateDirectory(outSubdir);

                    using var s = File.OpenRead(file);
                    using var bmp = SKBitmap.Decode(s);
                    if (bmp == null) { errors++; continue; }

                    ApplyWatermarkAndSave(bmp, outPath, text);
                    Console.WriteLine($"✨ {rel}");
                    ok++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"❌ {Path.GetFileName(file)}: {ex.Message}");
                    errors++;
                }
            }

            Console.WriteLine($"📊 ok={ok}  skipped={skipped}  errors={errors}");
        }

        /// <summary>
        /// Draw the watermark text in the bottom-right and encode the bitmap
        /// as lossy WebP. Both single-file and batch paths funnel through here.
        /// </summary>
        static void ApplyWatermarkAndSave(SKBitmap bitmap, string outPath, string text)
        {
            using var canvas = new SKCanvas(bitmap);

            // Font size scales with image height (~5%), with a 24px floor so it
            // stays legible on small thumbnails.
            float fontSize = bitmap.Height * 0.05f;
            if (fontSize < 24) fontSize = 24;

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold);
            using var font = new SKFont(typeface, fontSize);
            using var paint = new SKPaint
            {
                Color = SKColors.White.WithAlpha(180),
                IsAntialias = true,
            };
            // Soft drop shadow so the text stays legible on light backgrounds.
            paint.ImageFilter = SKImageFilter.CreateDropShadow(
                2f, 2f, 2f, 2f, SKColors.Black.WithAlpha(128));

            SKRect textBounds = new SKRect();
            font.MeasureText(text, out textBounds, paint);
            float margin = fontSize * 0.5f;
            float x = bitmap.Width - textBounds.Width - margin;
            float y = bitmap.Height - margin;
            canvas.DrawText(text, x, y, font, paint);

            // Lossy WebP at quality 80 — matches the ImageSharp default used
            // when the watermark is empty, so file sizes are comparable.
            using var outputStream = File.OpenWrite(outPath);
            var webpOptions = new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80);
            using var pixmap = bitmap.PeekPixels();
            pixmap.Encode(outputStream, webpOptions);
        }

        // ----------------------------------------------------------------
        //  Options record
        // ----------------------------------------------------------------

        class Options
        {
            public bool ShowHelp;
            public string? Error;
            public bool SingleFile;
            public bool Batch;
            public string? InputPath;
            public string? OutputPath;
            public string? InputDir;
            public string? OutputDir;
            public string? Text;
        }
    }
}