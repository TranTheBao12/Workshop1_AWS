using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Amazon.S3;
using Amazon.S3.Model;
using System.Text.RegularExpressions;

public class HomeController : Controller
{
    private readonly IWebHostEnvironment _env; private readonly OcrVideoService _ocrService; private readonly IHttpClientFactory _httpClientFactory; private readonly IConfiguration _configuration; private readonly IAmazonS3 _s3Client; private readonly string _bucketName;
    public HomeController(IWebHostEnvironment env, OcrVideoService ocrService, IHttpClientFactory httpClientFactory, IConfiguration configuration, IAmazonS3 s3Client)
    {
        _env = env;
        _ocrService = ocrService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _s3Client = s3Client;
        _bucketName = Environment.GetEnvironmentVariable("S3_BUCKET_NAME") ?? "imagetovideoweb-demo";
    }
    public IActionResult Index()
    {
        //var recentVideos = GetRecentVideos();
        //ViewBag.RecentVideos = recentVideos;
        return View();
    }
    //private List<(string VideoUrl, DateTime CreationTime)> GetRecentVideos()
    //{
    //    var videoList = _s3Client.ListObjectsAsync(new ListObjectsRequest
    //    {
    //        BucketName = _bucketName,
    //        Prefix = "outputs/"
    //    }).Result;

    //    return videoList.S3Objects
    //        .Select(o => (VideoUrl: $"https://{_bucketName}.s3.amazonaws.com/{o.Key}", CreationTime: o.LastModified))
    //        .OrderByDescending(x => x.CreationTime)
    //        .Take(10)
    //        .ToList();
    //}

    [HttpPost]
    public async Task<IActionResult> UploadImages(IFormFile[] images)
    {
        if (images == null || images.Length == 0)
        {
            TempData["Error"] = "Vui lòng chọn ít nhất một ảnh.";
            return RedirectToAction("Index");
        }

        var imageTextPairsForView = new List<(string ImageUrl, string Text, string TranslatedText)>();
        string tessPath = Path.Combine(_env.WebRootPath, "tessdata");

        foreach (var img in images)
        {
            string s3Key = await _ocrService.UploadImageToS3Async(img);
            string text = await _ocrService.ExtractTextFromImageAsync(s3Key, tessPath);
            string translatedText = await _ocrService.TranslateTextAsync(text, "vi", "en");
            string imageUrl = $"https://{_bucketName}.s3.amazonaws.com/{s3Key}";
            imageTextPairsForView.Add((ImageUrl: imageUrl, Text: text, TranslatedText: translatedText));
        }

        return View("EditTextList", imageTextPairsForView);
    }


    [HttpPost]
    public async Task<IActionResult> CreateVideo([FromForm] List<string> selectedImages, [FromForm] List<string> selectedTexts, [FromForm] List<string> translatedTexts, [FromForm] string language = "vi")
    {
        if (selectedImages == null || selectedTexts == null || translatedTexts == null ||
            selectedImages.Count != selectedTexts.Count || selectedImages.Count != translatedTexts.Count)
        {
            return BadRequest("Dữ liệu đầu vào không hợp lệ.");
        }

        language = language?.ToLower() == "en" ? "en" : "vi";
        var imageTextPairs = new List<(string S3ImageKey, string Text, string TranslatedText)>();
        for (int i = 0; i < selectedImages.Count; i++)
        {
            string s3Key = selectedImages[i].Replace($"https://{_bucketName}.s3.amazonaws.com/", "");
            imageTextPairs.Add((S3ImageKey: s3Key, Text: selectedTexts[i], TranslatedText: translatedTexts[i]));
        }

        string videoKey = await _ocrService.GenerateVideoWithAudioAsync(imageTextPairs, language);
        ViewBag.VideoUrl = $"https://{_bucketName}.s3.amazonaws.com/{videoKey}";
        return View("Result");
    }

    [HttpPost]
    public async Task<IActionResult> CorrectTextWithGemini([FromBody] TextCorrectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return Json(new { success = true, correctedText = "" });

        try
        {
            string apiKey = _configuration["ApiKey"];
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Google API key has not been configured.");

            var httpClient = _httpClientFactory.CreateClient();
            var geminiRequest = new
            {
                contents = new[]
                {
                new
                {
                    parts = new[] { new { text = $"Hãy sửa phiên bản tiếng Việt sang văn phong tự nhiên, đúng chính tả: {request.Text}" } }
                }
            },
                generationConfig = new
                {
                    temperature = 0.7,
                    maxOutputTokens = 1000,
                    topP = 0.9,
                    topK = 1
                }
            };

            var response = await httpClient.PostAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}", new StringContent(JsonConvert.SerializeObject(geminiRequest), Encoding.UTF8, "application/json"));
            response.EnsureSuccessStatusCode();
            var responseBody = await response.Content.ReadAsStringAsync();
            var geminiResponse = JsonConvert.DeserializeObject<dynamic>(responseBody);
            string correctedText = geminiResponse?.candidates?[0]?.content?.parts?[0]?.text;

            if (string.IsNullOrWhiteSpace(correctedText))
                return Json(new { success = false, message = "Gemini returned an empty response.", correctedText = request.Text });

            correctedText = Regex.Replace(correctedText.Trim(), @"[^a-zA-Z0-9\s]+$", "");
            return Json(new { success = true, correctedText });
        }
        catch (Exception ex)
        {
            //_logger.LogError($"Error correcting text with Gemini: {ex.Message}");
            return Json(new { success = false, message = $"Error: {ex.Message}", correctedText = request.Text });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CombineTexts([FromForm] List<string> selectedTexts, [FromForm] List<string> translatedTexts, [FromForm] string language = "vi")
    {
        if (selectedTexts == null && translatedTexts == null)
            return BadRequest("Không có văn bản nào được cung cấp để gộp.");

        language = language?.ToLower() == "en" ? "en" : "vi";
        var textsToCombine = language == "en" ? translatedTexts : selectedTexts;
        if (textsToCombine == null || !textsToCombine.Any())
            return BadRequest("Không có văn bản nào để gộp trong ngôn ngữ đã chọn.");

        string textKey = $"outputs/text_{Guid.NewGuid()}.txt";
        using (var stream = new MemoryStream())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            foreach (var text in textsToCombine)
                if (!string.IsNullOrWhiteSpace(text))
                    await writer.WriteLineAsync(text);
            await writer.FlushAsync();
            stream.Position = 0;
            await _s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = textKey,
                InputStream = stream
            });
        }

        return Json(new { success = true, textFileUrl = $"https://{_bucketName}.s3.amazonaws.com/{textKey}" });
    }

    //public IActionResult VideoGallery()
    //{
    //    //var videos = GetRecentVideos();
    //    return View(videos);
    //}

    [HttpPost]
    public async Task<IActionResult> DeleteVideo(string videoUrl)
    {
        if (string.IsNullOrEmpty(videoUrl))
            return BadRequest("Đường dẫn video không hợp lệ.");

        string s3Key = videoUrl.Replace($"https://{_bucketName}.s3.amazonaws.com/", "");
        await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _bucketName,
            Key = s3Key
        });
        TempData["Success"] = "Video đã được xóa thành công.";
        return RedirectToAction("VideoGallery");
    }
}

public class TextCorrectionRequest
{
    public string Text { get; set; }
}