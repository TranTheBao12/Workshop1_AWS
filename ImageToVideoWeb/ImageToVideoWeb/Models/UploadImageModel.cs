// Models/UploadImageModel.cs
using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations;

public class UploadImageModel
{
    [Required]
    [Display(Name = "Chọn ảnh")]
    public IFormFile ImageFile { get; set; }
}
