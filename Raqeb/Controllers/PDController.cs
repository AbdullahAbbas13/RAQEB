using Microsoft.AspNetCore.Mvc;
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



        //[HttpGet("transition/{poolId}")]
        //public async Task<IActionResult> Transition(int poolId)
        //{
        //    var res = await _repo.CalculateTransitionMatrixAsync(poolId);
        //    return res.Success ? Ok(res) : BadRequest(res);
        //}

        //[HttpGet("average/{poolId}")]
        //public async Task<IActionResult> Average(int poolId)
        //{
        //    var res = await _repo.CalculateAverageTransitionMatrixAsync(poolId);
        //    return res.Success ? Ok(res) : BadRequest(res);
        //}

        //[HttpGet("longrun/{poolId}")]
        //public async Task<IActionResult> LongRun(int poolId)
        //{
        //    var res = await _repo.CalculateLongRunMatrixAsync(poolId);
        //    return res.Success ? Ok(res) : BadRequest(res);
        //}

        //[HttpGet("odr/{poolId}")]
        //public async Task<IActionResult> ObservedRate(int poolId)
        //{
        //    var res = await _repo.CalculateObservedDefaultRateAsync(poolId);
        //    return res.Success ? Ok(res) : BadRequest(res);
        //}
    }
}
