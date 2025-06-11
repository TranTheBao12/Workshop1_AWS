namespace ImageToVideoWeb.Models
{
    public class ImageTextPair
    {
        public string ImageUrl { get; set; }
        public string Text { get; set; }
        public string TranslatedText { get; set; }
        public List<(int X, int Y, int Width, int Height)> CropRegions { get; set; }
    }   
}
