﻿using Microsoft.AspNetCore.Mvc;
using Raqeb.BL.Repositories;
using Raqeb.Shared.DTOs;
using Raqeb.Shared.ViewModels.Responses;

namespace Raqeb.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PDController : ControllerBase
    {
        private readonly IPDRepository _repo;
        public PDController(IPDRepository repo)
        {
            _repo = repo;
        }


        // ============================================================
        // 🟢 API Endpoint: رفع ملف Excel لحساب PD والـ Matrices
        // ============================================================
        [HttpPost("import")]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> ImportPDExcel( IFormFile file)
        {
            // ✅ التحقق من أن الملف تم رفعه بشكل صحيح
            if (file == null || file.Length == 0)
                return BadRequest(ApiResponse<string>.FailResponse("❌ Please upload a valid Excel file."));

            // ✅ استدعاء الدالة داخل الـ Repository لمعالجة الملف
            var result = await _repo.ImportPDExcelAsync(file);

            // ✅ في حالة النجاح، إرجاع الاستجابة 200 OK
            if (result.Success)
                return Ok(result);

            // ⚠️ في حالة الفشل، إرجاع الاستجابة 400 BadRequest مع الرسالة
            return BadRequest(result);
        }



        // ============================================================
        // 🟢 API Endpoint: تحميل ملف Excel الناتج بعد عملية PD Import
        // ============================================================
        [HttpGet("download/{fileName}")]
        public async Task<IActionResult> DownloadResultFile(string fileName)
        {
            try
            {
                // ✅ تحديد المسار الكامل للملف بناءً على الاسم
                var exportDir = Path.Combine(Directory.GetCurrentDirectory(), "../PDExports");
                var filePath = Path.Combine(exportDir, fileName);

                // ✅ التحقق من أن الملف موجود فعليًا
                if (!System.IO.File.Exists(filePath))
                    return NotFound(ApiResponse<string>.FailResponse("❌ File not found."));

                // ✅ قراءة محتوى الملف وتحويله إلى Stream
                var memory = new MemoryStream();
                await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                    await stream.CopyToAsync(memory);
                memory.Position = 0;

                // ✅ تحديد نوع المحتوى (MIME Type) لملف Excel
                var contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

                // ✅ إرجاع الملف للتحميل مع Header مناسب
                return File(memory, contentType, fileName);
            }
            catch (Exception ex)
            {
                // ⚠️ في حال وجود أي خطأ أثناء عملية التحميل يتم إرجاع رسالة خطأ واضحة
                return BadRequest(ApiResponse<string>.FailResponse($"⚠️ Error while downloading file: {ex.Message}"));
            }
        }

        [HttpPost("transition-matrices")]
        public async Task<ActionResult< PagedResult<PDTransitionMatrixDto>>> GetMatrices([FromBody] PDMatrixFilterDto filter)
        {
            var data = await _repo.GetTransitionMatricesPagedAsync(filter);
            return Ok(data);
        }


        [HttpPost("transition-matrices/export")]
        public async Task<FileResult> ExportTransitionMatrixToExcel([FromBody] PDMatrixFilterDto filter)
        {
            var fileBytes = await _repo.ExportTransitionMatrixToExcelAsync(filter);

            if (fileBytes == null || fileBytes.Length == 0)
                throw new Exception("⚠️ No data found for the selected filters.");

            // 📦 تجهيز اسم الملف وصيغة الإرجاع
            var fileName = $"TransitionMatrix_{filter.PoolId}_{filter.Year ?? DateTime.UtcNow.Year}.xlsx";

            // ✅ إرجاع الملف بصيغة Excel كـ FileResult مباشر
            return new FileContentResult(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            {
                FileDownloadName = fileName
            };
        }




        // ✅ عرض المتوسطات السنوية فقط
        [HttpPost("yearly-averages")]
        public async Task<ActionResult<Task<List<TransitionMatrixDto>>>> GetYearlyAverages([FromBody] PDMatrixFilterDto filter)
        {
            var result = await _repo.CalculateYearlyAverageTransitionMatricesAsync(filter);
            return Ok(result);
        }

        // ✅ تصدير المتوسط السنوي إلى Excel
        [HttpPost("yearly-averages/export")]
        public async Task<FileResult> ExportYearlyAverageToExcel([FromBody] PDMatrixFilterDto filter)
        {
            var fileBytes = await _repo.ExportYearlyAverageToExcelAsync(filter);

            if (fileBytes == null || fileBytes.Length == 0)
                throw new Exception("⚠️ No data found for the selected filters.");

            // 📦 تجهيز اسم الملف وصيغة الإرجاع
            var fileName = $"YearlyAverage_{filter.PoolId}_{filter.Year ?? DateTime.UtcNow.Year}.xlsx";

            // ✅ إرجاع الملف بصيغة Excel كـ FileResult مباشر
            return new FileContentResult(fileBytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
            {
                FileDownloadName = fileName
            };
        }




        [HttpPost("transition-matrix/longrun")]
        public async Task<ActionResult<TransitionMatrixDto>> GetLongRunMatrix()
        {
            var matrix = await _repo.CalculateLongRunAverageTransitionMatrixAsync();
            if (matrix == null)
                return NotFound("⚠️ No data found for this pool.");

            return Ok(matrix);
        }



        [HttpPost("transition-matrix/longrun/export")]
        public async Task<ActionResult<FileResult>> ExportLongRunMatrix()
        {
            var fileBytes = await _repo.ExportLongRunToExcelAsync();
            if (fileBytes == null || fileBytes.Length == 0)
                return BadRequest("⚠️ No data found for the selected pool.");

            var fileName = $"LongRunMatrix_{DateTime.UtcNow:yyyyMMdd}.xlsx";
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
        }

    }
}
