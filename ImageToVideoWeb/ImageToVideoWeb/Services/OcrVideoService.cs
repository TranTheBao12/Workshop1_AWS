using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Tesseract;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using SixLabors.ImageSharp.PixelFormats;
using Amazon.S3;
using Amazon.S3.Model;
using FFMpegCore;
using Microsoft.AspNetCore.Http;
using System.Web;

public class OcrVideoService
{
    private readonly HttpClient _httpClient; private readonly ILogger _logger; private readonly IAmazonS3 _s3Client; private readonly string _bucketName;

    public OcrVideoService(ILogger<OcrVideoService> logger, IAmazonS3 s3Client)
    {
        _httpClient = new HttpClient();
        _logger = logger;
        _s3Client = s3Client;
        _bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "imagetovideoweb-demo";
    }

    public async Task<string> UploadImageToS3Async(IFormFile image)
    {
        string s3Key = $"uploads/{Guid.NewGuid()}{Path.GetExtension(image.FileName)}";
        using (var stream = image.OpenReadStream())
        {
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = s3Key,
                InputStream = stream
            });
        }
        _logger.LogInformation($"Uploaded image to S3: {s3Key}");
        return s3Key;
    }

    private (string CleanedText, List<string> EnglishNames) CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return (text, new List<string>());

        _logger.LogDebug($"[CleanText] Input text: '{text}'");
        string namePattern = @"\b[A-Za-z]+\b";
        var englishNames = Regex.Matches(text, namePattern).Select(m => m.Value).ToList();
        _logger.LogDebug($"[CleanText] English names detected: {string.Join(", ", englishNames)}");

        string normalizedText = text.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new StringBuilder();
        foreach (char c in normalizedText)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        string tempCleanedText = sb.ToString().Normalize(NormalizationForm.FormC);
        string cleanedText = Regex.Replace(tempCleanedText, @"[^a-zA-Z0-9\p{L}\p{N}\p{P}\s]+", "").Trim();
        cleanedText = Regex.Replace(cleanedText, @"\s+", " ");
        _logger.LogDebug($"[CleanText] Final cleaned text: '{cleanedText}'");

        return (string.IsNullOrWhiteSpace(cleanedText) ? "Không có văn bản" : cleanedText, englishNames);
    }

    public async Task<string> ExtractTextFromImageAsync(string s3Key, string tessDataPath)
    {
        _logger.LogInformation($"Processing image: {s3Key}");
        string tempImagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");

        var getObjectRequest = new GetObjectRequest { BucketName = _bucketName, Key = s3Key };
        using (var response = await _s3Client.GetObjectAsync(getObjectRequest))
        using (var stream = response.ResponseStream)
        using (var image = await Image.LoadAsync<Rgba32>(stream))
        {
            image.Mutate(x => x.Contrast(1.1f));
            await image.SaveAsync(tempImagePath);
        }

        using (var engine = new TesseractEngine(tessDataPath, "vie", EngineMode.TesseractAndLstm))
        {
            engine.SetVariable("tessedit_char_whitelist", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789àáảãạâầấẩẫậăằắẳẵặèéẻẽẹêềếểễệđìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵÀÁẢÃẠÂẦẤẨẪẬĂẰẮẲẴẶÈÉẺẼẸÊỀẾỂỄỆĐÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ,.!?() ");
            engine.SetVariable("preserve_interword_spaces", "1");
            engine.SetVariable("psm", "6");

            using (var img = Pix.LoadFromFile(tempImagePath))
            using (var page = engine.Process(img))
            {
                string text = page.GetText().Trim();
                float confidence = page.GetMeanConfidence();
                _logger.LogDebug($"[OCR] Text: '{text}', Confidence: {confidence}%");
                if (confidence < 60)
                    _logger.LogWarning($"Low OCR confidence: {confidence}% for {s3Key}");

                var (cleanedText, _) = CleanText(text);
                string textKey = $"temp/{Path.GetFileNameWithoutExtension(s3Key)}_text.txt";
                await _s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = _bucketName,
                    Key = textKey,
                    ContentBody = cleanedText
                });
                return cleanedText;
            }
        }
    }

    public List<string> CropImageTo800By1800(string imagePath, string extractPath)
    {
        var resultPaths = new List<string>();
        using (var image = Image.Load<Rgba32>(imagePath))
        {
            const int targetWidth = 1200;
            const int targetHeight = 1600;

            // Trường hợp ảnh đã đúng kích thước
            if (image.Width == targetWidth && image.Height == targetHeight)
            {
                resultPaths.Add(imagePath);
                _logger.LogDebug($"Image {imagePath} is already {targetWidth}x{targetHeight} (width: {image.Width}, height: {image.Height})");
                return resultPaths;
            }

            // Trường hợp ảnh nhỏ hơn targetHeight pixel chiều cao
            if (image.Height <= targetHeight)
            {
                string resizedImagePath = Path.Combine(extractPath, $"{Path.GetFileNameWithoutExtension(imagePath)}_resized.png");
                int newHeight = targetHeight;
                int newWidth = (int)((float)image.Width / image.Height * newHeight);
                newWidth = newWidth % 2 == 0 ? newWidth : newWidth - 1;

                if (newWidth <= 0) newWidth = 2;

                image.Mutate(x => x.Resize(newWidth, newHeight, KnownResamplers.Bicubic));
                if (newWidth < targetWidth)
                {
                    image.Mutate(x => x.Pad(targetWidth, newHeight, Color.Black));
                }
                image.SaveAsPng(resizedImagePath);
                resultPaths.Add(resizedImagePath);
                _logger.LogDebug($"Resized image {imagePath} to fit {targetWidth}x{targetHeight} with padding: {resizedImagePath} (width: {newWidth}, height: {newHeight})");
                return resultPaths;
            }

            // Trường hợp ảnh lớn hơn targetHeight pixel, cắt thành nhiều đoạn
            int segmentCount = (int)Math.Ceiling((float)image.Height / targetHeight);
            _logger.LogDebug($"Image {imagePath} will be cropped into {segmentCount} segments (width: {image.Width}, height: {image.Height})");

            for (int i = 0; i < segmentCount; i++)
            {
                int yOffset = i * targetHeight;
                int currentHeight = Math.Min(targetHeight, image.Height - yOffset);

                if (currentHeight <= 0)
                {
                    _logger.LogWarning($"Skipping invalid segment {i} at yOffset {yOffset} (currentHeight <= 0)");
                    continue;
                }

                int width = Math.Min(targetWidth, image.Width);
                width = width % 2 == 0 ? width : width - 1;
                currentHeight = currentHeight % 2 == 0 ? currentHeight : currentHeight - 1;

                if (width <= 0 || currentHeight <= 0)
                {
                    _logger.LogWarning($"Invalid segment dimensions for {imagePath} at segment {i}: width={width}, height={currentHeight}");
                    continue;
                }

                string croppedImagePath = Path.Combine(extractPath, $"{Path.GetFileNameWithoutExtension(imagePath)}_crop_{i}.png");
                using (var croppedImage = image.Clone(ctx => ctx.Crop(new Rectangle(0, yOffset, width, currentHeight))))
                {
                    croppedImage.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new SixLabors.ImageSharp.Size(targetWidth, currentHeight),
                        Mode = ResizeMode.Crop
                    }));
                    croppedImage.SaveAsPng(croppedImagePath);
                    resultPaths.Add(croppedImagePath);
                    _logger.LogDebug($"Cropped segment {i} saved to: {croppedImagePath} (width: {width}, height: {currentHeight})");
                }
            }

            if (segmentCount > 1 && File.Exists(imagePath))
            {
                File.Delete(imagePath);
                _logger.LogDebug($"Deleted original image after cropping: {imagePath}");
            }
        }
        return resultPaths;
    }

    public async Task<List<(string ImagePath, string Text, string TranslatedText)>> ExtractDialogueTextFromImagesAsync(List<string> imagePaths, string tessDataPath)
    {
        _logger.LogInformation($"Starting OCR process for {imagePaths.Count} images.");
        var result = new List<(string ImagePath, string Text, string TranslatedText)>();

        if (!Directory.Exists(tessDataPath))
        {
            _logger.LogError($"Tesseract data path not found: {tessDataPath}");
            throw new DirectoryNotFoundException($"Tesseract data path not found: {tessDataPath}");
        }
        string trainedDataPath = Path.Combine(tessDataPath, "vie.traineddata");
        if (!File.Exists(trainedDataPath))
        {
            _logger.LogError($"Vietnamese trained data file not found at: {trainedDataPath}. Please download from https://github.com/tesseract-ocr/tessdata/raw/main/vie.traineddata and place it in {tessDataPath}");
            throw new FileNotFoundException($"Vietnamese trained data file not found at: {trainedDataPath}");
        }

        foreach (var path in imagePaths)
        {
            _logger.LogInformation($"Processing image: {Path.GetFileName(path)}");
            string text = "";

            string tempImagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
            using (var image = await Image.LoadAsync<Rgba32>(path))
            {
                image.Mutate(x => x.Contrast(1.1f));
                await image.SaveAsync(tempImagePath);
                _logger.LogDebug($"Temp image saved to: {tempImagePath}");
            }

            using (var engine = new TesseractEngine(tessDataPath, "vie", EngineMode.TesseractAndLstm))
            {
                engine.SetVariable("tessedit_char_whitelist", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789àáảãạâầấẩẫậăằắẳẵặèéẻẽẹêềếểễệđìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵÀÁẢÃẠÂẦẤẨẪẬĂẰẮẲẴẶÈÉẺẼẸÊỀẾỂỄỆĐÌÍỈĨỊÒÓỎÕỌÔỒỐỔỖỘƠỜỚỞỠỢÙÚỦŨỤƯỪỨỬỮỰỲÝỶỸỴ,.!?() ");
                engine.SetVariable("preserve_interword_spaces", "1");
                engine.SetVariable("psm", "6");

                using (var img = Pix.LoadFromFile(tempImagePath))
                using (var page = engine.Process(img))
                {
                    text = page.GetText().Trim();
                    _logger.LogDebug($"[OCR Raw Output for {Path.GetFileName(path)}] (Length: {text.Length}): '{text}'");

                    float confidence = page.GetMeanConfidence();
                    _logger.LogDebug($"[OCR Confidence for {Path.GetFileName(path)}]: {confidence}%");
                    if (confidence < 60)
                    {
                        _logger.LogWarning($"Low confidence OCR result for {Path.GetFileName(path)}: {confidence}%. Consider checking image quality or reprocessing.");
                    }
                }
            }
            File.Delete(tempImagePath);
            _logger.LogDebug($"Deleted temp image: {tempImagePath}");

            var (cleanedText, englishNames) = CleanText(text);
            string postProcessedText = PostProcessText(cleanedText);
            string translatedText = await TranslateTextAsync(postProcessedText, "vi", "en");
            result.Add((ImagePath: path, Text: postProcessedText, TranslatedText: translatedText));

            _logger.LogInformation($"Processed image: {Path.GetFileName(path)}");
            _logger.LogInformation($"  Original OCR Text: '{text}'");
            _logger.LogInformation($"  Cleaned Text: '{cleanedText}'");
            _logger.LogInformation($"  Post-Processed Text: '{postProcessedText}'");
            _logger.LogInformation($"  Translated Text: '{translatedText}'");
            _logger.LogInformation($"  English names detected: {string.Join(", ", englishNames)}");
        }

        return result;
    }

    private string PostProcessText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Không có văn bản";

        text = text.Replace("  ", " ");
        text = Regex.Replace(text, @"\s+\.", ".");

        var vietnameseReplacements = new Dictionary<string, string>
    {
        {"a", "à"}, {"e", "è"}, {"i", "ì"}, {"o", "ò"}, {"u", "ù"}, {"y", "ỳ"},
        {"A", "À"}, {"E", "È"}, {"I", "Ì"}, {"O", "Ò"}, {"U", "Ù"}, {"Y", "Ỳ"},
        {"d", "đ"}, {"D", "Đ"}
    };

        foreach (var replacement in vietnameseReplacements)
        {
            text = text.Replace(replacement.Key, replacement.Value);
        }

        var contextReplacements = new Dictionary<string, string>
    {
        {"nx", "này"}, {"ieo", "đi"}, {"toi", "tôi"}, {"den", "đến"}, {"dang", "đang"},
        {"thua", "thưa"}, {"ngai", "ngài"}, {"edo", "đó"}, {"day", "đây"}
    };
        foreach (var replacement in contextReplacements)
        {
            text = Regex.Replace(text, $@"\b{replacement.Key}\b", replacement.Value, RegexOptions.IgnoreCase);
        }

        text = Regex.Replace(text, @"(\w)\.(\w)", "$1. $2");
        text = Regex.Replace(text, @"(\p{L})\s+(\p{L})", "$1$2");

        return text.Trim();
    }

    public async Task<string> TranslateTextAsync(string text, string sourceLang, string targetLang)
    {
        if (string.IsNullOrEmpty(text) || sourceLang == targetLang) return text;

        try
        {
            string url = $"https://translate.google.com/translate_a/single?client=gtx&sl={sourceLang}&tl={targetLang}&dt=t&q={HttpUtility.UrlEncode(text, Encoding.UTF8)}";
            _logger.LogDebug($"[Translate] Calling Google Translate API: {url}");
            string response = await _httpClient.GetStringAsync(url);
            _logger.LogDebug($"[Translate] Raw Google Translate response: '{response}'");

            var match = Regex.Match(response, @"\[\[\[""(?<translatedText>[^""]+)""");
            string translated = match.Success ? match.Groups["translatedText"].Value : text;
            _logger.LogDebug($"[Translate] Translated Text: '{translated}'");

            string textKey = $"temp/translated_{Guid.NewGuid()}.txt";
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = textKey,
                ContentBody = translated
            });
            return translated;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error translating text '{text}': {ex.Message}");
            return text;
        }
    }

    private async Task GenerateAudioWithGoogleTTSAsync(string text, string outputPath, string language)
    {
        if (string.IsNullOrEmpty(text))
        {
            _logger.LogWarning($"Skipping audio generation for empty text.");
            return;
        }

        _logger.LogInformation($"Generating audio for text (language: {language}): '{text.Substring(0, Math.Min(text.Length, 50))}...'");

        try
        {
            string tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            _logger.LogDebug($"Temporary audio directory: {tempDir}");

            await GenerateTTS(text, outputPath, language == "vi" ? "vi" : "en");
            if (File.Exists(outputPath))
            {
                _logger.LogInformation($"Generated single audio file: {outputPath}");
            }
            else
            {
                _logger.LogWarning($"No audio generated for text: '{text}'. Output file: {outputPath}");
            }

            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
                _logger.LogDebug($"Deleted temporary audio directory: {tempDir}");
            }
            _logger.LogInformation($"Generated final audio file: {outputPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error generating audio with Google TTS: '{text.Substring(0, Math.Min(text.Length, 50))}' or similar: {ex.Message}");
        }
    }

    private async Task GenerateTTS(string text, string outputPath, string langCode)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _logger.LogDebug($"[TTS] Generating TTS for text '{text.Substring(0, Math.Min(text.Length, 50))}' with language '{langCode}' to '{outputPath}'");

        string ttsUrl = $"https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&q={HttpUtility.UrlEncode(text, Encoding.UTF8)}&tl={langCode}";
        try
        {
            using (var response = await _httpClient.GetAsync(ttsUrl))
            {
                response.EnsureSuccessStatusCode();
                using (var stream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
                {
                    await stream.CopyToAsync(fileStream);
                }
                _logger.LogDebug($"[TTS] Successfully generated TTS file: {outputPath}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"[TTS] Error generating TTS for text '{text.Substring(0, Math.Min(text.Length, 50))}' to '{outputPath}': {ex.Message}");
        }
    }

    private async Task ConcatenateAudioAsync(List<string> audioFiles, string outputPath)
    {
        _logger.LogInformation($"Concatenating audio files to {outputPath}");
        foreach (var file in audioFiles)
        {
            if (!File.Exists(file))
            {
                _logger.LogWarning($"Audio file missing for concatenation: {file}");
            }
        }
        try
        {
            await FFMpegArguments
                .FromConcatInput(audioFiles)
                .OutputToFile(outputPath, true, options => options
                    .WithAudioCodec("copy"))
                .ProcessAsynchronously();
            _logger.LogInformation($"Successfully concatenated audio files to {outputPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error concatenating audio files to {outputPath}: {ex.Message}");
            throw;
        }
    }

    public async Task<string> GenerateVideoWithAudioAsync(List<(string S3ImageKey, string Text, string TranslatedText)> imageTextPairs, string language)
    {
        string outputDir = Path.GetTempPath();
        string videoFileName = $"video_{Guid.NewGuid()}.mp4";
        string videoKey = $"outputs/{videoFileName}";
        string framesFile = Path.Combine(outputDir, "frames.txt");

        var resizedImagePaths = new List<string>();
        var audioFiles = new List<(string Path, double Duration)>();

        for (int i = 0; i < imageTextPairs.Count; i++)
        {
            string resizedImagePath = Path.Combine(outputDir, $"frame_{i:000}.png");
            var getObjectRequest = new GetObjectRequest { BucketName = _bucketName, Key = imageTextPairs[i].S3ImageKey };
            using (var response = await _s3Client.GetObjectAsync(getObjectRequest))
            using (var stream = response.ResponseStream)
            using (var image = await Image.LoadAsync(stream))
            {
                image.Mutate(x => x.Resize(1200, 1000));
                await image.SaveAsync(resizedImagePath);
            }
            resizedImagePaths.Add(resizedImagePath);

            string audioPath = Path.Combine(outputDir, $"audio_{Guid.NewGuid()}.mp3");
            await GenerateAudioWithGoogleTTSAsync(imageTextPairs[i].Text, audioPath, language);
            if (File.Exists(audioPath))
            {
                var mediaInfo = await FFProbe.AnalyseAsync(audioPath);
                audioFiles.Add((audioPath, mediaInfo.Duration.TotalSeconds));
            }
            else
            {
                audioFiles.Add((null, 1.0));
            }
        }

        using (var writer = new StreamWriter(framesFile))
        {
            for (int i = 0; i < resizedImagePaths.Count; i++)
            {
                await writer.WriteLineAsync($"file '{resizedImagePaths[i].Replace("'", "'\\''")}'");
                await writer.WriteLineAsync($"duration {audioFiles[i].Duration}");
            }
        }

        string tempVideoPath = Path.Combine(outputDir, videoFileName);
        await FFMpegArguments
            .FromFileInput(framesFile, false, options => options.WithCustomArgument("-f concat -safe 0"))
            .OutputToFile(tempVideoPath, true, options => options
                .WithVideoCodec("libx264")
                .WithCustomArgument("-pix_fmt yuv420p -y"))
            .ProcessAsynchronously();

        if (audioFiles.Any(a => a.Path != null))
        {
            string concatAudioPath = Path.Combine(outputDir, $"concat_{Guid.NewGuid()}.mp3");
            await ConcatenateAudioAsync(audioFiles.Where(a => a.Path != null).Select(a => a.Path).ToList(), concatAudioPath);

            string finalVideoPath = Path.Combine(outputDir, $"final_{videoFileName}");
            await FFMpegArguments
                .FromFileInput(tempVideoPath)
                .AddFileInput(concatAudioPath)
                .OutputToFile(finalVideoPath, true, options => options
                    .WithVideoCodec("copy")
                    .WithAudioCodec("aac")
                    .WithCustomArgument("-map 0:v:0 -map 1:a:0 -af atempo=1.5 -y"))
                .ProcessAsynchronously();

            tempVideoPath = finalVideoPath;
            File.Delete(concatAudioPath);
        }

        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = videoKey,
            FilePath = tempVideoPath
        });

        File.Delete(framesFile);
        File.Delete(tempVideoPath);
        foreach (var path in resizedImagePaths.Concat(audioFiles.Where(a => a.Path != null).Select(a => a.Path)))
        {
            File.Delete(path);
        }

        return videoKey;
    }

}