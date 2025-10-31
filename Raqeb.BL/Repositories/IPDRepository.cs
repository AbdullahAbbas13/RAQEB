using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using OfficeOpenXml;
using Raqeb.Shared.DTOs;
using Raqeb.Shared.Models;
using Raqeb.Shared.Models.Raqeb.Shared.Models;
using Raqeb.Shared.ViewModels.Responses;

namespace Raqeb.BL.Repositories
{
    // ============================================================
    // 🔹 واجهة الـ Repository (Interface)
    // ============================================================
    public interface IPDRepository
    {
        // ============================================================
        // 🔹 1. استيراد ملف Excel وتنفيذ الحسابات بالكامل
        // ============================================================
        Task<ApiResponse<string>> ImportPDExcelAsync(IFormFile file);

        // ============================================================
        // 🔹 2. الدوال التقليدية (تعتمد على قاعدة البيانات)
        // ============================================================

        // 🟢 حساب مصفوفة الانتقال فقط (Transition Matrix)
        //Task<ApiResponse<List<List<double>>>> CalculateTransitionMatrixAsync(int poolId);

        //// 🟢 حساب المصفوفة المتوسطة فقط (Average Transition Matrix)
        //Task<ApiResponse<List<List<double>>>> CalculateAverageTransitionMatrixAsync(int poolId);

        //// 🟢 حساب المصفوفة بعيدة المدى فقط (Long Run Transition Matrix)
        //Task<ApiResponse<List<List<double>>>> CalculateLongRunMatrixAsync(int poolId);

        //// 🟢 حساب معدل التعثر الفعلي فقط (Observed Default Rate)
        //Task<ApiResponse<double>> CalculateObservedDefaultRateAsync(int poolId);

        // ============================================================
        // 🔹 3. دوال In-Memory (تُستخدم داخل ImportPDExcelAsync قبل الـ Commit)
        // ============================================================

        // 🧠 حساب مصفوفة الانتقال من الذاكرة بدون قراءة من قاعدة البيانات
        ApiResponse<List<List<double>>> CalculateTransitionMatrixFromMemory(Pool pool, List<Customer> customers);

        // 🧠 حساب المصفوفة المتوسطة من الذاكرة
        ApiResponse<List<List<double>>> CalculateAverageTransitionMatrixFromMemory(List<List<double>> transitionMatrix);

        // 🧠 حساب مصفوفة المدى الطويل من الذاكرة
        ApiResponse<List<List<double>>> CalculateLongRunMatrixFromMemory(List<List<double>> transitionMatrix);

        // 🧠 حساب معدل التعثر الفعلي من الذاكرة
        ApiResponse<double> CalculateObservedDefaultRateFromMemory(List<List<double>> transitionMatrix);

       Task<PagedResult<PDTransitionMatrixDto>> GetTransitionMatricesPagedAsync(PDMatrixFilterDto filter);
        Task<byte[]> ExportTransitionMatrixToExcelAsync(PDMatrixFilterDto filter);
        Task<List<TransitionMatrixDto>> CalculateYearlyAverageTransitionMatricesAsync(PDMatrixFilterDto filter);
        Task<byte[]> ExportYearlyAverageToExcelAsync(PDMatrixFilterDto filter);
        Task<TransitionMatrixDto> CalculateLongRunAverageTransitionMatrixAsync();

        Task<byte[]> ExportLongRunToExcelAsync();

    }

    // ============================================================
    // 🔹 تنفيذ واجهة Repository: PDRepository
    // ============================================================
    public class PDRepository : IPDRepository
    {
        private readonly IUnitOfWork _uow;

        public PDRepository(IUnitOfWork uow)
        {
            _uow = uow;

        }

        public async Task<ApiResponse<string>> ImportPDExcelAsync(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return ApiResponse<string>.FailResponse("❌ File is empty or missing.");

            var bulkConfig = new BulkConfig
            {
                UseTempDB = true,               // استخدام قاعدة مؤقتة لتسريع الـ Bulk
                PreserveInsertOrder = true,     // يحافظ على ترتيب الإدخال
                SetOutputIdentity = true,       // يرجع قيم الـ ID بعد الإدخال
                EnableStreaming = true,         // إدخال على دفعات بدون ضغط على الذاكرة
                BatchSize = 10000,              // حجم الدفعة
                BulkCopyTimeout = 0             // بدون مهلة زمنية
            };

            _uow.DbContext.Database.SetCommandTimeout(0);
            int currentYear = 2015;
            var monthlyTransitions = new List<List<List<double>>>();
            var strategy = _uow.DbContext.Database.CreateExecutionStrategy();

            try
            {
                return await strategy.ExecuteAsync(async () =>
                {
                    await using var transaction = await _uow.DbContext.Database.BeginTransactionAsync();

                    try
                    {
                        // 📂 حفظ ملف Excel مؤقتًا
                        string tempFilePath = await SaveTemporaryFileAsync(file);

                        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
                        using var package = new ExcelPackage(new FileInfo(tempFilePath));
                        var sheet = package.Workbook.Worksheets.FirstOrDefault();
                        if (sheet == null)
                            return ApiResponse<string>.FailResponse("❌ No worksheet found in Excel file.");

                        // 🧱 تحميل الـ Pool أو إنشاؤه
                        var pool = await LoadOrCreatePoolAsync(sheet);
                        int newVersion = await GetNewPoolVersionAsync(pool.Id);

                        // 🧩 قراءة العملاء من الملف
                        var customers = await ParseCustomersFromSheetAsync(sheet, pool, newVersion);

                        // ⚡ إدخال العملاء والدرجات Bulk (بدون معاملة داخلية)
                        await BulkInsertLargeDataAsync(customers, bulkConfig);

                        // 🧮 حساب مصفوفات الانتقال السنوية
                        await SaveYoYTransitionSnapshotsAsync(
                            pool,
                            newVersion,
                            customers,
                            bulkConfig,
                            minGrade: 1,
                            maxGrade: 4,
                            defaultGrade: 4
                        );

                        // 🧠 حساب المصفوفات النهائية
                        var transition = CalculateTransitionMatrixFromMemory(pool, customers);
                        var yearlyAverage = CalculateYearlyAverageTransitionMatrixFromMemory(monthlyTransitions);
                        var average = CalculateAverageTransitionMatrixFromMemory(transition.Data);
                        var longRun = CalculateLongRunMatrixFromMemory(transition.Data);
                        var odr = CalculateObservedDefaultRateFromMemory(transition.Data);

                        // 💾 حفظ النتائج النهائية في قاعدة البيانات
                        await SaveCalculatedMatricesAsync(
                            pool,
                            newVersion,
                            transition,
                            average,
                            longRun,
                            odr,
                            bulkConfig,
                            yearlyAverage,
                            currentYear
                        );

                        // 📊 تصدير النتائج إلى Excel
                        string exportFilePath = await ExportResultsToExcelAsync(
                            pool,
                            newVersion,
                            transition,
                            average,
                            longRun,
                            odr
                        );

                        // 🧹 تنظيف الملفات المؤقتة
                        if (File.Exists(tempFilePath))
                            File.Delete(tempFilePath);

                        await transaction.CommitAsync();

                        return ApiResponse<string>.SuccessResponse(
                            $"✅ PD Calculations completed successfully for Pool {pool.Name} (Version {newVersion})",
                            exportFilePath
                        );
                    }
                    catch (Exception ex)
                    {
                        await transaction.RollbackAsync();
                        return ApiResponse<string>.FailResponse($"⚠️ Error while processing PD Import: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                return ApiResponse<string>.FailResponse($"❌ Unexpected error: {ex.Message}");
            }
        }


        /// <summary>
        /// يحسب عدّ الانتقالات بين fromMonth و toMonth (نفس الشهر في السنة التالية).
        /// يعتمد أن لكل عميل Grade واحد فقط لكل شهر.
        /// </summary>
        public static TransitionCountsResult CalculateTransitionCounts(
                                                                        IEnumerable<Customer> customers,
                                                                        DateTime fromMonth,
                                                                        DateTime toMonth,
                                                                        int minGrade = 1,
                                                                        int maxGrade = 4,
                                                                        int? defaultGrade = null)
        {
            defaultGrade ??= maxGrade;

            int size = (maxGrade - minGrade + 1);
            var counts = new int[size, size];

            foreach (var c in customers)
            {
                var gFrom = c.Grades.FirstOrDefault(g => g.Month.Year == fromMonth.Year && g.Month.Month == fromMonth.Month);
                var gTo = c.Grades.FirstOrDefault(g => g.Month.Year == toMonth.Year && g.Month.Month == toMonth.Month);

                if (gFrom == null || gTo == null) continue;

                int from = gFrom.GradeValue;  // <-- غيّرها لو اسم الخاصية مختلف
                int to = gTo.GradeValue;

                if (from < minGrade || from > maxGrade || to < minGrade || to > maxGrade) continue;

                counts[from - minGrade, to - minGrade]++;
            }

            var rowTotals = new int[size];
            var rowPd = new double[size];
            int defaultColIndex = (defaultGrade.Value - minGrade);

            for (int r = 0; r < size; r++)
            {
                int total = 0;
                for (int c = 0; c < size; c++)
                    total += counts[r, c];

                rowTotals[r] = total;
                rowPd[r] = total == 0 ? 0d : (double)counts[r, defaultColIndex] / total;
            }

            return new TransitionCountsResult(counts, rowTotals, rowPd, minGrade, maxGrade);
        }

        private async Task BulkInsertLargeDataAsync(List<Customer> customers, BulkConfig config)
        {
            _uow.DbContext.ChangeTracker.AutoDetectChangesEnabled = false;
            _uow.DbContext.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
            _uow.DbContext.Database.SetCommandTimeout(0);

            int totalCustomers = customers.Count;
            int dynamicBatch = totalCustomers switch
            {
                > 100000 => 50000,
                > 50000 => 20000,
                > 10000 => 10000,
                _ => 5000
            };

            // 🧱 المرحلة 1: إدخال العملاء
            var newCustomers = customers.Where(c => c.ID == 0).ToList();
            if (newCustomers.Any())
            {
                await _uow.DbContext.BulkInsertAsync(newCustomers, config);
                _uow.DbContext.ChangeTracker.Clear();
            }

            // 🔗 المرحلة 2: ربط الدرجات بالعملاء
            var customerMap = customers
                .Where(c => !string.IsNullOrEmpty(c.Code))
                .ToDictionary(c => c.Code, c => c.ID);

            var allGrades = customers
                .SelectMany(c => c.Grades ?? Enumerable.Empty<CustomerGrade>())
                .Where(g => g != null)
                .ToList();

            foreach (var grade in allGrades)
            {
                if (customerMap.TryGetValue(grade.CustomerCode, out int custId))
                    grade.CustomerID = custId;
            }

            // 💾 المرحلة 3: إدخال الدرجات
            foreach (var batch in allGrades.Chunk(dynamicBatch))
            {
                await _uow.DbContext.BulkInsertAsync(batch.ToList(), config);
                _uow.DbContext.ChangeTracker.Clear();
            }

            _uow.DbContext.ChangeTracker.AutoDetectChangesEnabled = true;
        }


        public async Task<int> SaveYoYTransitionSnapshotsAsync(
    Pool pool,
    int newVersion,
    IEnumerable<Customer> customers,
    BulkConfig bulkConfig,
    int minGrade = 1,
    int maxGrade = 4,
    int? defaultGrade = null)
        {
            var allMonths = customers
                .SelectMany(c => c.Grades.Select(g => new DateTime(g.Month.Year, g.Month.Month, 1)))
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            var setMonths = new HashSet<DateTime>(allMonths);
            var transitionCells = new List<PDMonthlyTransitionCell>();
            var rowStats = new List<PDMonthlyRowStat>();
            int size = (maxGrade - minGrade + 1);

            foreach (var from in allMonths)
            {
                var to = from.AddYears(1);
                if (!setMonths.Contains(to)) continue;

                var res = CalculateTransitionCounts(customers, from, to, minGrade, maxGrade, defaultGrade);

                for (int r = 0; r < size; r++)
                {
                    for (int c = 0; c < size; c++)
                    {
                        int count = res.Counts[r, c];
                        if (count == 0) continue;

                        transitionCells.Add(new PDMonthlyTransitionCell
                        {
                            PoolId = pool.Id,
                            PoolName = pool.Name,
                            Version = newVersion,
                            Year = from.Year,
                            Month = from.Month,
                            RowIndex = r,
                            ColumnIndex = c,
                            Value = count,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                // 🔹 PD لكل صف كنسبة مئوية
                for (int r = 0; r < size; r++)
                {
                    int total = res.RowTotals[r];
                    double pd = res.RowPD[r];
                    double pdPercent = Math.Round(pd * 100, 4);

                    rowStats.Add(new PDMonthlyRowStat
                    {
                        PoolId = pool.Id,
                        PoolName = pool.Name,
                        Version = newVersion,
                        Year = from.Year,
                        Month = from.Month,
                        FromGrade = r + minGrade,
                        TotalCount = total,
                        PDPercent = pdPercent,
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            if (transitionCells.Any())
            {
                await _uow.DbContext.BulkInsertAsync(transitionCells, bulkConfig);
                _uow.DbContext.ChangeTracker.Clear();
            }

            if (rowStats.Any())
            {
                await _uow.DbContext.BulkInsertAsync(rowStats, bulkConfig);
                _uow.DbContext.ChangeTracker.Clear();
            }

            return transitionCells.Count + rowStats.Count;
        }






        //private async Task BulkInsertLargeDataAsync(List<Customer> customers, BulkConfig config)
        //{
        //    int batchSize = 5000; // 🔹 يمكنك تعديلها حسب حجم السيرفر والذاكرة

        //    // 🧩 تقسيم العملاء إلى دفعات صغيرة
        //    var newCustomers = customers
        //        .Where(c => c.ID == 0 || string.IsNullOrWhiteSpace(c.Code))
        //        .ToList();

        //    if (newCustomers.Any())
        //    {
        //        foreach (var batch in newCustomers.Chunk(batchSize))
        //        {
        //            await _uow.DbContext.BulkInsertAsync(batch.ToList(), config);
        //            _uow.DbContext.ChangeTracker.Clear(); // تنظيف التتبع لتقليل الذاكرة
        //        }
        //    }

        //    // 🧮 إدخال الدرجات لكل العملاء
        //    var allGrades = customers
        //        .SelectMany(c => c.Grades ?? Enumerable.Empty<CustomerGrade>())
        //        .ToList();

        //    if (allGrades.Any())
        //    {
        //        foreach (var batch in allGrades.Chunk(batchSize))
        //        {
        //            await _uow.DbContext.BulkInsertAsync(batch.ToList(), config);
        //            _uow.DbContext.ChangeTracker.Clear();
        //        }
        //    }
        //}




        private async Task<string> SaveTemporaryFileAsync(IFormFile file)
        {
            string exportDir = Path.Combine(Directory.GetCurrentDirectory(), "PDExports");
            if (!Directory.Exists(exportDir))
                Directory.CreateDirectory(exportDir);

            string tempFilePath = Path.Combine(exportDir, $"{Guid.NewGuid()}_{file.FileName}");
            using var stream = new FileStream(tempFilePath, FileMode.Create);
            await file.CopyToAsync(stream);

            return tempFilePath;
        }


        private async Task<Pool> LoadOrCreatePoolAsync(ExcelWorksheet sheet)
        {
            string poolName = sheet.Cells[2, 7].GetValue<string>() ?? "Default Pool";
            var pool = await _uow.DbContext.Pools.FirstOrDefaultAsync(p => p.Name == poolName);

            if (pool == null)
            {
                pool = new Pool { Name = poolName, TotalEAD = 0, RecoveryRate = 0, UnsecuredLGD = 0 };
                await _uow.DbContext.Pools.AddAsync(pool);
                await _uow.DbContext.SaveChangesAsync();
                _uow.DbContext.Entry(pool).State = EntityState.Detached;
            }

            return pool;
        }



        private async Task<int> GetNewPoolVersionAsync(int poolId)
        {
            int latestVersion = await _uow.DbContext.PDTransitionCells
                .Where(x => x.PoolId == poolId)
                .Select(x => (int?)x.Version)
                .MaxAsync() ?? 0;

            return latestVersion + 1;
        }



        private async Task<List<Customer>> ParseCustomersFromSheetAsync(ExcelWorksheet sheet, Pool pool, int version)
        {
            int startColumn = 82;
            int monthsCount = 73;
            DateTime startMonth = new DateTime(2015, 1, 1);
            int maxRow = sheet.Dimension.End.Row;

            var monthColumns = Enumerable.Range(0, monthsCount)
                .Select(i => (ColumnIndex: startColumn + i, Month: startMonth.AddMonths(i)))
                .ToList();

            var allCustomerCodes = new HashSet<string>();
            for (int row = 2; row <= maxRow; row++)
            {
                var code = sheet.Cells[row, 1].GetValue<string>();
                if (!string.IsNullOrWhiteSpace(code)) allCustomerCodes.Add(code);
            }

            var existingCustomers = await _uow.DbContext.Customers
                .Where(c => allCustomerCodes.Contains(c.Code))
                .ToListAsync();

            var existingDict = existingCustomers.ToDictionary(c => c.Code, c => c);
            var customersToInsert = new List<Customer>();

            for (int row = 2; row <= maxRow; row++)
            {
                var code = sheet.Cells[row, 1].GetValue<string>();
                if (string.IsNullOrWhiteSpace(code)) continue;

                var name = sheet.Cells[row, 2].GetValue<string>() ?? "";

                if (!existingDict.TryGetValue(code, out var customer))
                {
                    customer = new Customer
                    {
                        Code = code,
                        NameAr = name,
                        PoolId = pool.Id,
                        Grades = new List<CustomerGrade>()
                    };
                    customersToInsert.Add(customer);
                    existingDict[code] = customer;
                }

                foreach (var (col, month) in monthColumns)
                {
                    var gradeVal = sheet.Cells[row, col].GetValue<int?>();
                    if (gradeVal.HasValue)
                    {
                        customer.Grades ??= new List<CustomerGrade>();
                        customer.Grades.Add(new CustomerGrade
                        {
                            CustomerCode = code,
                            PoolId = pool.Id,
                            Version = version,
                            GradeValue = gradeVal.Value,
                            Month = month,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }
            }

            // دمج الجدد مع الموجودين
            existingCustomers.AddRange(customersToInsert);
            return existingCustomers;
        }

        /// <summary>
        /// 🔹 حساب Yearly Average Transition Matrix من مجموعة المصفوفات الشهرية.
        /// </summary>
        /// <param name="monthlyMatrices">قائمة تحتوي على مصفوفة الانتقال لكل شهر (List of 2D matrices)</param>
        /// <param name="monthsPerYear">عدد الأشهر في السنة، الافتراضي 12</param>
        /// <returns>مصفوفة تمثل المتوسط السنوي لكل انتقال (from → to)</returns>
        public ApiResponse<List<List<double>>> CalculateYearlyAverageTransitionMatrixFromMemory(
            List<List<List<double>>> monthlyMatrices,
            int monthsPerYear = 12)
        {
            try
            {
                // ✅ تحقق من وجود بيانات
                if (monthlyMatrices == null || !monthlyMatrices.Any())
                    return ApiResponse<List<List<double>>>.FailResponse("⚠️ No monthly transition matrices provided.");

                // 🔹 عدد الحالات (Grades)
                int states = monthlyMatrices.First().Count;
                int cols = monthlyMatrices.First().First().Count;

                // 🧮 إنشاء مصفوفة تجميع (للجمع الكلي عبر الأشهر)
                var sumMatrix = new double[states, cols];
                int monthCount = monthlyMatrices.Count;

                // 🔁 جمع كل القيم من كل مصفوفة شهرية
                foreach (var monthlyMatrix in monthlyMatrices)
                {
                    for (int i = 0; i < states; i++)
                    {
                        for (int j = 0; j < cols; j++)
                        {
                            sumMatrix[i, j] += monthlyMatrix[i][j];
                        }
                    }
                }

                // 🔹 حساب المتوسط السنوي (القسمة على عدد الأشهر)
                var yearlyAvg = new List<List<double>>();
                for (int i = 0; i < states; i++)
                {
                    var row = new List<double>();
                    for (int j = 0; j < cols; j++)
                    {
                        double avgValue = sumMatrix[i, j] / monthCount;
                        row.Add(Math.Round(avgValue, 10)); // تقريب 6 خانات عشرية
                    }
                    yearlyAvg.Add(row);
                }

                return ApiResponse<List<List<double>>>.SuccessResponse(
                    "✅ Yearly Average Transition Matrix calculated successfully.",
                    yearlyAvg
                );
            }
            catch (Exception ex)
            {
                return ApiResponse<List<List<double>>>.FailResponse($"⚠️ Error while calculating Yearly Average Matrix: {ex.Message}");
            }
        }






        private async Task SaveCalculatedMatricesAsync(
            Pool pool,
            int version,
            ApiResponse<List<List<double>>> transition,
            ApiResponse<List<List<double>>> average,
            ApiResponse<List<List<double>>> longRun,
            ApiResponse<double> odr,
            BulkConfig config,
            ApiResponse<List<List<double>>> yearlyAverage = null, // 👈 مضاف
            int? year = null) // 👈 السنة لو موجودة
        {
            // ============================================
            // 1️⃣ حفظ Transition Matrix
            // ============================================
            var pdMatrixCells = new List<PDMatrixCell>();
            int stateCount = transition.Data.Count - 1;

            for (int i = 0; i < stateCount; i++)
            {
                for (int j = 0; j < stateCount; j++)
                {
                    pdMatrixCells.Add(new PDMatrixCell
                    {
                        PoolId = pool.Id,
                        PoolName = pool.Name,
                        Version = version,
                        MatrixType = "Transition",
                        RowIndex = i,
                        ColumnIndex = j,
                        Value = Math.Round(transition.Data[i][j], 10),
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }
            await _uow.DbContext.BulkInsertAsync(pdMatrixCells, config);
            _uow.DbContext.ChangeTracker.Clear();

            // ============================================
            // 2️⃣ حفظ Average / LongRun / ODR كالمعتاد
            // ============================================
            var averageCells = new List<PDAverageCell>();
            for (int i = 0; i < average.Data.Count; i++)
                for (int j = 0; j < average.Data[i].Count; j++)
                    averageCells.Add(new PDAverageCell
                    {
                        PoolId = pool.Id,
                        PoolName = pool.Name,
                        Version = version,
                        RowIndex = i,
                        ColumnIndex = j,
                        Value = Math.Round(average.Data[i][j], 10),
                        CreatedAt = DateTime.UtcNow
                    });
            await _uow.DbContext.BulkInsertAsync(averageCells, config);
            _uow.DbContext.ChangeTracker.Clear();

            var longRunCells = new List<PDLongRunCell>();
            for (int i = 0; i < longRun.Data.Count; i++)
                for (int j = 0; j < longRun.Data[i].Count; j++)
                    longRunCells.Add(new PDLongRunCell
                    {
                        PoolId = pool.Id,
                        PoolName = pool.Name,
                        Version = version,
                        RowIndex = i,
                        ColumnIndex = j,
                        Value = Math.Round(longRun.Data[i][j], 10),
                        CreatedAt = DateTime.UtcNow
                    });
            await _uow.DbContext.BulkInsertAsync(longRunCells, config);
            _uow.DbContext.ChangeTracker.Clear();

            var odrCells = new List<PDObservedRate>
            {
                new PDObservedRate
                {
                    PoolId = pool.Id,
                    PoolName = pool.Name,
                    Version = version,
                    ObservedDefaultRate = odr.Data,
                    CreatedAt = DateTime.UtcNow
                }
            };

            await _uow.DbContext.BulkInsertAsync(odrCells, config);
            _uow.DbContext.ChangeTracker.Clear();

            // ============================================
            // 3️⃣ حفظ Yearly Average Transition Matrix
            // ============================================
            if (yearlyAverage != null && yearlyAverage.Data != null && yearlyAverage.Data.Any())
            {
                var yearlyCells = new List<PDYearlyAverageCell>();

                for (int i = 0; i < yearlyAverage.Data.Count; i++)
                {
                    for (int j = 0; j < yearlyAverage.Data[i].Count; j++)
                    {
                        yearlyCells.Add(new PDYearlyAverageCell
                        {
                            PoolId = pool.Id,
                            PoolName = pool.Name,
                            Version = version,
                            Year = year ?? DateTime.UtcNow.Year, // 👈 السنة
                            RowIndex = i,
                            ColumnIndex = j,
                            Value = Math.Round(yearlyAverage.Data[i][j], 10),
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                }

                await _uow.DbContext.BulkInsertAsync(yearlyCells, config);
                _uow.DbContext.ChangeTracker.Clear();
            }
        }





        private async Task<string> ExportResultsToExcelAsync(Pool pool, int version,
                    ApiResponse<List<List<double>>> transition,
                    ApiResponse<List<List<double>>> average,
                    ApiResponse<List<List<double>>> longRun,
                    ApiResponse<double> odr)
        {
            string exportDir = Path.Combine(Directory.GetCurrentDirectory(), "PDExports");
            string filePath = Path.Combine(exportDir, $"PD_Result_{pool.Name}_V{version}_{DateTime.Now:yyyy-MM-dd_HH-mm}.xlsx");

            using var package = new ExcelPackage();
            AddSheet(package, "Transition Matrix", transition.Data);
            AddSheet(package, "Average Matrix", average.Data);
            AddSheet(package, "Long Run Matrix", longRun.Data);

            var wsODR = package.Workbook.Worksheets.Add("Observed Default Rate");
            wsODR.Cells[1, 1].Value = "Observed Default Rate";
            wsODR.Cells[1, 2].Value = odr.Data;

            await package.SaveAsAsync(new FileInfo(filePath));
            return filePath;
        }

        private void AddSheet(ExcelPackage package, string name, List<List<double>> data)
        {
            var ws = package.Workbook.Worksheets.Add(name);
            for (int i = 0; i < data.Count; i++)
                for (int j = 0; j < data[i].Count; j++)
                    ws.Cells[i + 1, j + 1].Value = data[i][j];
        }





        // ✅ (سأكمل الجزء الثاني بعد هذا — تنفيذ BulkInsert + إنشاء Excel + Commit Transaction)


        /*
        //////////////////////////////////////////////////////////////////////////////////
         */



        // ============================================================
        // 🟢 1. حساب Transition Matrix وتخزينها في قاعدة البيانات
        // ============================================================

        public ApiResponse<List<List<double>>> CalculateTransitionMatrixFromMemory(Pool pool, List<Customer> customers)
        {
            try
            {
                if (pool == null)
                    return ApiResponse<List<List<double>>>.FailResponse("❌ Pool is null.");

                if (customers == null || customers.Count == 0)
                    return ApiResponse<List<List<double>>>.FailResponse("⚠️ لا يوجد عملاء لحساب Transition Matrix.");

                // 🔹 عدد الحالات (الدرجات)
                int states = customers
                    .SelectMany(c => c.Grades ?? new List<CustomerGrade>())
                    .Select(g => g.GradeValue)
                    .DefaultIfEmpty(0)
                    .Max();

                if (states == 0)
                    return ApiResponse<List<List<double>>>.FailResponse("⚠️ لا توجد قيم درجات صالحة.");

                var matrix = new double[states, states];
                var totalPerRow = new double[states];

                // 🔹 حساب عدد العملاء في كل انتقال (Counts)
                foreach (var customer in customers)
                {
                    if (customer.Grades == null || customer.Grades.Count < 2)
                        continue;

                    var sortedGrades = customer.Grades.OrderBy(g => g.Month).ToList();

                    for (int i = 0; i < sortedGrades.Count - 1; i++)
                    {
                        int from = sortedGrades[i].GradeValue - 1;
                        int to = sortedGrades[i + 1].GradeValue - 1;

                        if (from >= 0 && from < states && to >= 0 && to < states)
                        {
                            matrix[from, to]++;
                            totalPerRow[from]++;
                        }
                    }
                }

                // 🔹 تجهيز النتيجة النهائية (تحتوي على counts فقط)
                var result = new List<List<double>>();
                for (int i = 0; i < states; i++)
                {
                    var row = new List<double>();

                    // إضافة القيم الخام (عدد العملاء)
                    for (int j = 0; j < states; j++)
                        row.Add(Math.Round(matrix[i, j], 10));

                    // إجمالي العملاء في الصف
                    double totalCount = totalPerRow[i];

                    // حساب PD% (انتقال إلى آخر حالة فقط)
                    double pd = totalCount == 0 ? 0 : (matrix[i, states - 1] / totalCount);

                    row.Add(totalCount);                 // إجمالي العملاء في هذه الدرجة
                    row.Add(Math.Round(pd * 100, 10));   // النسبة المئوية للتعثر (PD%)

                    result.Add(row);
                }

                // 🔹 صف الإجمالي الكلي (Totals)
                var totalRow = new List<double>();
                for (int j = 0; j < states; j++)
                {
                    double colSum = 0;
                    for (int i = 0; i < states; i++)
                        colSum += matrix[i, j];
                    totalRow.Add(colSum);
                }

                double grandTotal = totalRow.Sum();
                double overallPD = grandTotal == 0 ? 0 : (totalRow.Last() / grandTotal);

                totalRow.Add(grandTotal);
                totalRow.Add(Math.Round(overallPD * 100, 10));
                result.Add(totalRow);

                return ApiResponse<List<List<double>>>.SuccessResponse(
                    $"✅ Transition Matrix calculated successfully for Pool {pool.Name}.",
                    result);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<List<double>>>.FailResponse($"⚠️ Error while calculating in memory: {ex.Message}");
            }
        }



        //public async Task<ApiResponse<List<List<double>>>> CalculateTransitionMatrixAsync(int poolId)
        //{
        //    try
        //    {
        //        // ✅ تحميل الـ Pool المطلوب من قاعدة البيانات مع العملاء والدرجات الخاصة بكل عميل
        //        var pool = await _uow.DbContext.Pools
        //            .Include(p => p.Customers)
        //                .ThenInclude(c => c.Grades)
        //            .FirstOrDefaultAsync(p => p.Id == poolId);

        //        // ❌ التحقق من وجود الـ Pool فعلاً
        //        if (pool == null)
        //            return ApiResponse<List<List<double>>>.FailResponse("❌ لم يتم العثور على Pool.");

        //        // ✅ استخراج العملاء من الـ Pool وتحويلهم إلى List في الذاكرة
        //        var customers = pool.Customers.ToList();

        //        // ❌ في حال عدم وجود عملاء داخل الـ Pool
        //        if (customers.Count == 0)
        //            return ApiResponse<List<List<double>>>.FailResponse("⚠️ لا يوجد عملاء داخل هذا الـ Pool.");

        //        // ✅ عدد الحالات (Grades) — يمكن تعديلها لاحقًا ديناميكيًا
        //        int states = 4;

        //        // ✅ مصفوفة لتخزين عدد الانتقالات من كل درجة إلى الأخرى
        //        var matrix = new double[states, states];

        //        // ✅ مصفوفة لتخزين عدد العملاء الذين بدأوا في كل درجة (Total per row)
        //        var totalPerRow = new double[states];

        //        // ✅ تمرّ على كل عميل لحساب الانتقالات
        //        foreach (var customer in customers)
        //        {
        //            // 🔹 التأكد من وجود درجات للعميل
        //            if (customer.Grades == null || customer.Grades.Count < 2)
        //                continue;

        //            // 🔹 ترتيب الدرجات حسب التاريخ تصاعديًا (زمنيًا)
        //            var sortedGrades = customer.Grades.OrderBy(g => g.Month).ToList();

        //            // 🔁 المرور على كل انتقال من شهر لآخر
        //            for (int i = 0; i < sortedGrades.Count - 1; i++)
        //            {
        //                // 🔹 درجة البداية (From Grade)
        //                int from = sortedGrades[i].GradeValue - 1;

        //                // 🔹 درجة النهاية (To Grade)
        //                int to = sortedGrades[i + 1].GradeValue - 1;

        //                // 🔹 زيادة العداد للانتقال المحدد
        //                matrix[from, to]++;

        //                // 🔹 زيادة إجمالي عدد العملاء الذين بدأوا من الدرجة الحالية
        //                totalPerRow[from]++;
        //            }

        //        }

        //        // ✅ تجهيز المصفوفة النهائية لنتيجة الإخراج
        //        var result = new List<List<double>>();

        //        // ✅ تمرّ على كل صف (درجة) في المصفوفة لحساب النسب و PD
        //        for (int i = 0; i < states; i++)
        //        {
        //            // 🔹 إنشاء صف جديد
        //            var row = new List<double>();

        //            // 🔹 جمع إجمالي الصف لحساب المجموع والنسب
        //            double total = totalPerRow[i] == 0 ? 1 : totalPerRow[i];

        //            // 🔹 المرور على كل عمود (To Grade)
        //            for (int j = 0; j < states; j++)
        //            {
        //                // 🔹 حساب النسبة الانتقالية (الاحتمالية)
        //                double probability = matrix[i, j] / total;

        //                // 🔹 تقريب القيمة لأربعة منازل عشرية
        //                row.Add(Math.Round(probability, 4));
        //            }

        //            // 🔹 حساب الـ Total (إجمالي العملاء في هذا الصف)
        //            double totalCount = totalPerRow[i];

        //            // 🔹 حساب الـ PD (احتمالية الانتقال إلى Default)
        //            double pd = totalCount == 0 ? 0 : (matrix[i, states - 1] / totalCount);

        //            // 🔹 إضافة الـ Total و الـ PD كأعمدة إضافية في الصف
        //            row.Add(totalCount);
        //            row.Add(Math.Round(pd * 100, 2)); // النسبة المئوية %

        //            // 🔹 إضافة الصف إلى النتيجة النهائية
        //            result.Add(row);
        //        }

        //        // ✅ تجهيز صف الإجمالي (Total Row)
        //        var totalRow = new List<double>();
        //        for (int j = 0; j < states; j++)
        //        {
        //            // 🔹 جمع كل القيم في نفس العمود عبر جميع الصفوف
        //            double colSum = 0;
        //            for (int i = 0; i < states; i++)
        //                colSum += matrix[i, j];

        //            totalRow.Add(colSum);
        //        }

        //        // 🔹 جمع إجمالي الصفوف لعمل المجموع الكلي
        //        double grandTotal = totalRow.Sum();

        //        // 🔹 حساب الـ PD العام (مجموع Default ÷ المجموع العام)
        //        double overallPD = grandTotal == 0 ? 0 : (totalRow.Last() / grandTotal);

        //        // 🔹 إضافة الإجماليات في نهاية الجدول
        //        totalRow.Add(grandTotal);
        //        totalRow.Add(Math.Round(overallPD * 100, 2));

        //        // 🔹 إضافة صف الإجمالي في نهاية النتيجة
        //        result.Add(totalRow);

        //        // ✅ إرجاع المصفوفة النهائية (مع Totals و PDs)
        //        return ApiResponse<List<List<double>>>.SuccessResponse(
        //            $"✅ تم حساب Transition Matrix بنجاح لعدد {customers.Count} عميل داخل Pool {pool.Name}",
        //            result);
        //    }
        //    catch (Exception ex)
        //    {
        //        // ⚠️ في حال وجود أي خطأ أثناء العملية
        //        return ApiResponse<List<List<double>>>.FailResponse($"⚠️ حدث خطأ أثناء حساب Transition Matrix: {ex.Message}");
        //    }
        //}


        // ============================================================
        // 🟢 2. حساب Average Transition Matrix
        // ============================================================
        public ApiResponse<List<List<double>>> CalculateAverageTransitionMatrixFromMemory(List<List<double>> transitionMatrix)
        {
            try
            {
                // 🔹 التحقق من وجود بيانات في المصفوفة
                if (transitionMatrix == null || !transitionMatrix.Any())
                    return ApiResponse<List<List<double>>>.FailResponse("⚠️ Transition Matrix is empty.");

                // 🔹 حساب المصفوفة المتوسطة
                var avg = CalculateAverageMatrix(transitionMatrix);

                // ✅ إرجاع النتيجة النهائية
                return ApiResponse<List<List<double>>>.SuccessResponse("✅ تم حساب Average Transition Matrix بنجاح", avg);
            }
            catch (Exception ex)
            {
                // ⚠️ في حال حدوث أي خطأ أثناء العملية
                return ApiResponse<List<List<double>>>.FailResponse($"⚠️ خطأ أثناء حساب Average Transition Matrix: {ex.Message}");
            }
        }


        // ============================================================
        // 🟢 3. حساب Long Run Matrix (المصفوفة بعيدة المدى)
        // ============================================================
        public ApiResponse<List<List<double>>> CalculateLongRunMatrixFromMemory(List<List<double>> transitionMatrix)
        {
            try
            {
                // 🔹 التحقق من وجود مصفوفة الانتقال
                if (transitionMatrix == null || !transitionMatrix.Any())
                    return ApiResponse<List<List<double>>>.FailResponse("⚠️ Transition Matrix is empty.");

                // 🔹 حساب مصفوفة المدى الطويل بالاعتماد على المصفوفة الحالية
                var longRun = CalculateLongRunMatrix(transitionMatrix);

                // ✅ إرجاع النتيجة
                return ApiResponse<List<List<double>>>.SuccessResponse("✅ تم حساب Long Run Matrix بنجاح", longRun);
            }
            catch (Exception ex)
            {
                // ⚠️ في حال حدوث أي خطأ أثناء العملية
                return ApiResponse<List<List<double>>>.FailResponse($"⚠️ خطأ أثناء حساب Long Run Matrix: {ex.Message}");
            }
        }


        // ============================================================
        // 🟢 4. حساب معدل التعثر الفعلي (Observed Default Rate)
        // ============================================================
        public ApiResponse<double> CalculateObservedDefaultRateFromMemory(List<List<double>> transitionMatrix)
        {
            try
            {
                // 🔹 التحقق من وجود المصفوفة
                if (transitionMatrix == null || !transitionMatrix.Any())
                    return ApiResponse<double>.FailResponse("⚠️ Transition Matrix is empty.");

                // 🔹 حساب الـ ODR من المصفوفة
                var odr = CalculateObservedDefaultRate(transitionMatrix);

                // ✅ إرجاع النتيجة
                return ApiResponse<double>.SuccessResponse("✅ تم حساب Observed Default Rate بنجاح", odr);
            }
            catch (Exception ex)
            {
                // ⚠️ في حال وجود خطأ أثناء الحساب
                return ApiResponse<double>.FailResponse($"⚠️ خطأ أثناء حساب Observed Default Rate: {ex.Message}");
            }
        }


        // ============================================================
        // 🔸 دالة حساب المصفوفة المتوسطة
        // ============================================================
        private List<List<double>> CalculateAverageMatrix(List<List<double>> matrix)
        {
            // 🔹 إنشاء قائمة جديدة لحفظ النتائج
            var avg = new List<List<double>>();

            // 🔁 المرور على كل صف في المصفوفة
            for (int i = 0; i < matrix.Count; i++)
            {
                // 🧮 جمع القيم في الصف الواحد
                double sum = matrix[i].Sum();

                // 🧩 قسمة كل قيمة على مجموع الصف للحصول على النسبة
                var row = matrix[i].Select(x => x / (sum == 0 ? 1 : sum)).ToList();

                avg.Add(row);
            }

            return avg;
        }

        // ============================================================
        // 🔸 دالة حساب المصفوفة بعيدة المدى (Long Run)
        // ============================================================
        private List<List<double>> CalculateLongRunMatrix(List<List<double>> matrix)
        {
            // 🔹 نسخة من المصفوفة الأصلية
            var result = matrix;

            // 🔁 نضرب المصفوفة بنفسها 50 مرة للحصول على الاستقرار (Steady State)
            for (int i = 0; i < 50; i++)
                result = MultiplyMatrices(result, matrix);

            return result;
        }

        // ============================================================
        // 🔸 دالة ضرب مصفوفتين (Matrix Multiplication)
        // ============================================================
        private List<List<double>> MultiplyMatrices(List<List<double>> A, List<List<double>> B)
        {
            int n = A.Count; // عدد الصفوف والأعمدة (مصفوفة مربعة)
            var result = new List<List<double>>(n);

            for (int i = 0; i < n; i++)
            {
                var row = new List<double>(n);
                for (int j = 0; j < n; j++)
                {
                    double sum = 0;

                    for (int k = 0; k < n; k++)
                        sum += A[i][k] * B[k][j];

                    // ✅ حماية من NaN و Infinity + تقريب 10 منازل عشرية
                    double safeValue = double.IsNaN(sum) || double.IsInfinity(sum)
                        ? 0
                        : Math.Round(sum, 15);

                    row.Add(safeValue);
                }

                // ✅ تطبيع الصف (Normalization) لو المجموع مش صفر
                double rowSum = row.Sum();
                if (rowSum != 0)
                {
                    for (int j = 0; j < n; j++)
                        row[j] = Math.Round(row[j] / rowSum, 15);
                }

                result.Add(row);
            }

            return result;
        }



        // ============================================================
        // 🔸 دالة حساب معدل التعثر الفعلي (Observed Default Rate)
        // ============================================================
        private double CalculateObservedDefaultRate(List<List<double>> matrix)
        {
            int n = matrix.Count;

            // 🔹 نجمع آخر عمود في كل صف (احتمالية التعثر)
            double sum = matrix.Sum(r => r.Last());

            // 🔹 المتوسط العام لمعدل التعثر
            return Math.Round(sum / n, 10);
        }







        public async Task<PagedResult<PDTransitionMatrixDto>> GetTransitionMatricesPagedAsync(PDMatrixFilterDto filter)
        {
            int skip = (filter.Page - 1) * filter.PageSize;

            // 🧩 بناء الاستعلام الديناميكي
            var periodQuery = _uow.DbContext.PDMonthlyRowStats
                .Where(x => x.PoolId == filter.PoolId && x.Version == filter.Version);

            if (filter.Year.HasValue)
                periodQuery = periodQuery.Where(x => x.Year == filter.Year.Value);

            if (filter.Month.HasValue)
                periodQuery = periodQuery.Where(x => x.Month == filter.Month.Value);

            // 📆 جلب الفترات المطلوبة مع pagination
            var periods = await periodQuery
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .OrderBy(x => x.Year)
                .ThenBy(x => x.Month)
                .Skip(skip)
                .Take(filter.PageSize)
                .ToListAsync();

            var result = new List<PDTransitionMatrixDto>();

            foreach (var p in periods)
            {
                // 🧱 خلايا الانتقال
                var dbCells = await _uow.DbContext.PDMonthlyTransitionCells
                    .Where(x => x.PoolId == filter.PoolId && x.Version == filter.Version && x.Year == p.Year && x.Month == p.Month)
                    .Select(x => new TransitionCellDto
                    {
                        FromGrade = x.RowIndex + filter.MinGrade,
                        ToGrade = x.ColumnIndex + filter.MinGrade,
                        Count = (int)x.Value
                    })
                    .ToListAsync();

                // ✅ ملء المصفوفة الكاملة لجميع الدرجات
                var completeCells = new List<TransitionCellDto>();
                for (int from = filter.MinGrade; from <= filter.MaxGrade; from++)
                {
                    for (int to = filter.MinGrade; to <= filter.MaxGrade; to++)
                    {
                        var existing = dbCells.FirstOrDefault(c => c.FromGrade == from && c.ToGrade == to);
                        completeCells.Add(existing ?? new TransitionCellDto
                        {
                            FromGrade = from,
                            ToGrade = to,
                            Count = 0
                        });
                    }
                }

                // 📊 إحصاءات الصفوف (PD%)
                var stats = await _uow.DbContext.PDMonthlyRowStats
                    .Where(x => x.PoolId == filter.PoolId && x.Version == filter.Version && x.Year == p.Year && x.Month == p.Month)
                    .Select(x => new RowStatDto
                    {
                        FromGrade = x.FromGrade,
                        TotalCount = x.TotalCount,
                        PDPercent = x.PDPercent
                    })
                    .ToListAsync();

                // ✅ ضمان كل الدرجات موجودة
                for (int g = filter.MinGrade; g <= filter.MaxGrade; g++)
                {
                    if (!stats.Any(s => s.FromGrade == g))
                    {
                        stats.Add(new RowStatDto
                        {
                            FromGrade = g,
                            TotalCount = 0,
                            PDPercent = 0
                        });
                    }
                }

                result.Add(new PDTransitionMatrixDto
                {
                    Year = p.Year,
                    Month = p.Month,
                    Cells = completeCells,
                    RowStats = stats
                });
            }

            // 🔢 عدد السجلات الكلي
            int totalCount = await periodQuery
                .Select(x => new { x.Year, x.Month })
                .Distinct()
                .CountAsync();

            return new PagedResult<PDTransitionMatrixDto>
            {
                Items = result.OrderBy(x => x.Year).ThenBy(x => x.Month).ToList(),
                Page = filter.Page,
                PageSize = filter.PageSize,
                TotalCount = totalCount
            };
        }


        public async Task<byte[]> ExportTransitionMatrixToExcelAsync(PDMatrixFilterDto filter)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            var matrices = await GetTransitionMatricesPagedAsync(filter);
            if (matrices == null || !matrices.Items.Any())
                return Array.Empty<byte>();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("PD Transition Matrices");

            int startRow = 1;
            int startCol = 1;
            int tableWidth = 7;
            int tableHeight = 8;

            int tablesPerRow = 3; // ← عدد الجداول في كل صف أفقي
            int tableIndex = 0;

            foreach (var matrix in matrices.Items)
            {
                // حساب موقع الجدول
                int tableRow = tableIndex / tablesPerRow;
                int tableCol = tableIndex % tablesPerRow;

                int top = startRow + (tableRow * (tableHeight + 2));
                int left = startCol + (tableCol * (tableWidth + 2));

                // العنوان الرئيسي (مثلاً Jan/15 -> Jan/16)
                string title = $"{new DateTime(matrix.Year, matrix.Month, 1):MMM/yy} → {new DateTime(matrix.Year, matrix.Month, 1).AddYears(1):MMM/yy}";
                ws.Cells[top, left, top, left + 6].Merge = true;
                ws.Cells[top, left].Value = title;
                ws.Cells[top, left].Style.Font.Bold = true;
                ws.Cells[top, left].Style.Font.Color.SetColor(System.Drawing.Color.White);
                ws.Cells[top, left].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[top, left].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.DarkGreen);
                ws.Cells[top, left].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;

                // رؤوس الأعمدة
                string[] headers = { "From\\To", "1", "2", "3", "4", "Total", "PD" };
                for (int i = 0; i < headers.Length; i++)
                {
                    ws.Cells[top + 1, left + i].Value = headers[i];
                    ws.Cells[top + 1, left + i].Style.Font.Bold = true;
                    ws.Cells[top + 1, left + i].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[top + 1, left + i].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96)); // Navy Blue
                    ws.Cells[top + 1, left + i].Style.Font.Color.SetColor(System.Drawing.Color.White);
                    ws.Cells[top + 1, left + i].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                }

                // الصفوف (FromGrade)
                for (int r = 1; r <= 4; r++)
                {
                    int row = top + 1 + r;
                    ws.Cells[row, left].Value = r;
                    ws.Cells[row, left].Style.Font.Bold = true;
                    ws.Cells[row, left].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[row, left].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96)); // Navy
                    ws.Cells[row, left].Style.Font.Color.SetColor(System.Drawing.Color.White);

                    // الأعمدة (ToGrade)
                    for (int c = 1; c <= 4; c++)
                    {
                        var cellData = matrix.Cells.FirstOrDefault(x => x.FromGrade == r && x.ToGrade == c);
                        ws.Cells[row, left + c].Value = cellData?.Count ?? 0;
                        ws.Cells[row, left + c].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                    }

                    // الإجمالي (Total)
                    var total = matrix.Cells.Where(x => x.FromGrade == r).Sum(x => x.Count);
                    ws.Cells[row, left + 5].Value = total;
                    ws.Cells[row, left + 5].Style.Font.Bold = true;

                    // PD%
                    var pd = matrix.RowStats.FirstOrDefault(x => x.FromGrade == r)?.PDPercent ?? 0;
                    ws.Cells[row, left + 6].Value = $"{pd:0.0}%";
                    ws.Cells[row, left + 6].Style.Font.Bold = true;
                    ws.Cells[row, left + 6].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[row, left + 6].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.DarkRed);
                    ws.Cells[row, left + 6].Style.Font.Color.SetColor(System.Drawing.Color.White);
                    ws.Cells[row, left + 6].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                }

                // صف الإجماليات
                int totalRow = top + 6;
                ws.Cells[totalRow, left].Value = "Total";
                ws.Cells[totalRow, left].Style.Font.Bold = true;
                ws.Cells[totalRow, left].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[totalRow, left].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96));
                ws.Cells[totalRow, left].Style.Font.Color.SetColor(System.Drawing.Color.White);

                for (int c = 1; c <= 4; c++)
                {
                    var totalCol = matrix.Cells.Where(x => x.ToGrade == c).Sum(x => x.Count);
                    ws.Cells[totalRow, left + c].Value = totalCol;
                    ws.Cells[totalRow, left + c].Style.Font.Bold = true;
                }

                var grandTotal = matrix.Cells.Sum(x => x.Count);
                ws.Cells[totalRow, left + 5].Value = grandTotal;
                ws.Cells[totalRow, left + 5].Style.Font.Bold = true;

                var avgPD = matrix.RowStats.Any() ? matrix.RowStats.Average(x => x.PDPercent) : 0;
                ws.Cells[totalRow, left + 6].Value = $"{avgPD:0.0}%";
                ws.Cells[totalRow, left + 6].Style.Font.Bold = true;
                ws.Cells[totalRow, left + 6].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[totalRow, left + 6].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.DarkRed);
                ws.Cells[totalRow, left + 6].Style.Font.Color.SetColor(System.Drawing.Color.White);

                // حدود الجدول
                using (var range = ws.Cells[top, left, totalRow, left + 6])
                {
                    range.Style.Border.BorderAround(OfficeOpenXml.Style.ExcelBorderStyle.Thin, System.Drawing.Color.Black);
                    range.Style.Border.Top.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Left.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Right.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Bottom.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                }

                tableIndex++;
            }

            ws.Cells.AutoFitColumns();
            return await package.GetAsByteArrayAsync();
        }

        public async Task<List<TransitionMatrixDto>> CalculateYearlyAverageTransitionMatricesAsync(PDMatrixFilterDto filter)
        {
            var query = _uow.DbContext.PDMonthlyTransitionCells
                .Where(c => c.PoolId == filter.PoolId && c.ColumnIndex >= 0);

            if (filter.Year.HasValue)
                query = query.Where(c => c.Year == filter.Year.Value);

            var allCells = await query.ToListAsync();
            if (!allCells.Any())
                return new List<TransitionMatrixDto>();

            // 🧮 نجمع حسب السنة (لو الفلتر فيه سنة واحدة هتبقى مجموعة واحدة)
            var result = new List<TransitionMatrixDto>();

            foreach (var yearGroup in allCells.GroupBy(c => c.Year))
            {
                // بناء قاموس (from,to) => المتوسط
                var grouped = yearGroup
                    .GroupBy(c => new { c.RowIndex, c.ColumnIndex })
                    .ToDictionary(
                        g => (g.Key.RowIndex, g.Key.ColumnIndex),
                        g => Math.Round(g.Average(x => x.Value), 2)
                    );

                var avgCells = new List<TransitionCellDto>();

                // ✅ بناء مصفوفة 4×4 حتى لو مفيش بيانات
                for (int from = 1; from <= 4; from++)
                {
                    for (int to = 1; to <= 4; to++)
                    {
                        double value = grouped.TryGetValue((from - 1, to - 1), out double v) ? v : 0;
                        avgCells.Add(new TransitionCellDto
                        {
                            FromGrade = from,
                            ToGrade = to,
                            Count = value
                        });
                    }
                }

                // 📊 حساب الإجماليات و PD لكل صف
                var rowStats = avgCells
                    .GroupBy(x => x.FromGrade)
                    .Select(g =>
                    {
                        var total = g.Sum(x => x.Count);
                        var pd = g.FirstOrDefault(x => x.ToGrade == 4)?.Count ?? 0;
                        var pdPercent = total > 0 ? Math.Round((pd / total) * 100, 1) : 0;
                        return new RowStatDto
                        {
                            FromGrade = g.Key,
                            TotalCount = (int)total,
                            PDPercent = pdPercent
                        };
                    })
                    .ToList();

                result.Add(new TransitionMatrixDto
                {
                    Year = yearGroup.Key,
                    Title = $"Yearly Average Transition Matrix - {yearGroup.Key}",
                    IsYearlyAverage = true,
                    Cells = avgCells,
                    RowStats = rowStats
                });
            }

            return result;
        }


        public async Task<byte[]> ExportYearlyAverageToExcelAsync(PDMatrixFilterDto filter)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // 🧩 استدعاء المصفوفات السنوية المحسوبة
            List<TransitionMatrixDto> matrices = await CalculateYearlyAverageTransitionMatricesAsync(filter);

            if (matrices == null || matrices.Count == 0)
                return Array.Empty<byte>();


            using var package = new ExcelPackage();

            foreach (var matrix in matrices)
            {
                if (matrix.Cells == null || matrix.Cells.Count == 0)
                    continue;

                var ws = package.Workbook.Worksheets.Add($"Year_{matrix.Year}");
                int startRow = 1;

                // 🏷️ العنوان الرئيسي
                ws.Cells[startRow, 1].Value = matrix.Title;
                ws.Cells[startRow, 1, startRow, 6].Merge = true;
                ws.Cells[startRow, 1].Style.Font.Bold = true;
                ws.Cells[startRow, 1].Style.Font.Size = 14;
                ws.Cells[startRow, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                ws.Cells[startRow, 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[startRow, 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.DarkBlue);
                ws.Cells[startRow, 1].Style.Font.Color.SetColor(System.Drawing.Color.White);

                startRow += 2;

                // 🧱 عناوين الأعمدة
                string[] headers = { "FromGrade", "ToGrade", "Count", "Total", "PD%" };
                for (int i = 0; i < headers.Length; i++)
                {
                    ws.Cells[startRow, i + 1].Value = headers[i];
                    ws.Cells[startRow, i + 1].Style.Font.Bold = true;
                    ws.Cells[startRow, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[startRow, i + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightGray);
                    ws.Cells[startRow, i + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                }

                int row = startRow;

                // 📊 تعبئة بيانات الخلايا
                foreach (var cell in matrix.Cells.OrderBy(x => x.FromGrade).ThenBy(x => x.ToGrade))
                {
                    row++;
                    ws.Cells[row, 1].Value = cell.FromGrade;
                    ws.Cells[row, 2].Value = cell.ToGrade;
                    ws.Cells[row, 3].Value = cell.Count;
                }

                // 📈 حساب الإجماليات (Totals + PD)
                row++;
                ws.Cells[row, 1].Value = "Totals";
                ws.Cells[row, 1].Style.Font.Bold = true;

                foreach (var stat in matrix.RowStats.OrderBy(s => s.FromGrade))
                {
                    row++;
                    ws.Cells[row, 1].Value = stat.FromGrade;
                    ws.Cells[row, 4].Value = stat.TotalCount;
                    ws.Cells[row, 5].Value = stat.PDPercent / 100; // تحويل النسبة إلى نسبة مئوية في Excel
                    ws.Cells[row, 5].Style.Numberformat.Format = "0.0%";

                    // لون العمود الأحمر لـ PD
                    ws.Cells[row, 5].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[row, 5].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.DarkRed);
                    ws.Cells[row, 5].Style.Font.Color.SetColor(System.Drawing.Color.White);
                }

                // 🎨 تنسيق الجدول
                ws.Cells.AutoFitColumns();
                ws.View.ShowGridLines = false;
                ws.Cells.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;
                ws.Cells.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            }

            // 💾 تحويل الملف إلى bytes
            return await package.GetAsByteArrayAsync();
        }

        public async Task<TransitionMatrixDto> CalculateLongRunAverageTransitionMatrixAsync()
        {
            // 🧱 اجلب كل البيانات الموجودة في الجدول (كل Pools وكل السنوات)
            var allCells = await _uow.DbContext.PDMonthlyTransitionCells.ToListAsync();

            if (!allCells.Any())
                return null;

            // 🧮 تجميع المتوسط لجميع البيانات (بدون أي شروط)
            var grouped = allCells
                .GroupBy(c => new { c.RowIndex, c.ColumnIndex })
                .Select(g => new
                {
                    From = g.Key.RowIndex + 1,
                    To = g.Key.ColumnIndex,
                    AvgValue = Math.Round(g.Average(x => x.Value), 4)
                })
                .ToList();

            // ✅ بناء المصفوفة 4×4 (حتى لو بعض الخلايا فاضية)
            var avgCells = grouped
                .Where(g => g.To >= 0)
                .Select(g => new TransitionCellDto
                {
                    FromGrade = g.From,
                    ToGrade = g.To + 1,
                    Count = g.AvgValue
                })
                .ToList();

            // 📊 حساب الإجماليات و PD%
            var rowStats = avgCells
                .GroupBy(x => x.FromGrade)
                .Select(g =>
                {
                    var total = g.Sum(x => x.Count);
                    var pd = g.FirstOrDefault(x => x.ToGrade == 4)?.Count ?? 0;
                    var pdPercent = total > 0 ? Math.Round((pd / total) * 100, 2) : 0;
                    return new RowStatDto
                    {
                        FromGrade = g.Key,
                        TotalCount = (int)total,
                        PDPercent = pdPercent
                    };
                })
                .ToList();

            return new TransitionMatrixDto
            {
                Title = "Global Long Run Average Transition Matrix (All Pools, All Years)",
                Year = 0,
                IsYearlyAverage = false,
                Cells = avgCells,
                RowStats = rowStats
            };
        }


        public async Task<byte[]> ExportLongRunToExcelAsync()
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // 🧮 احسب مصفوفة Long Run من كل البيانات (بدون فلتر)
            var matrix = await CalculateLongRunAverageTransitionMatrixAsync();
            if (matrix == null || matrix.Cells == null || !matrix.Cells.Any())
                return Array.Empty<byte>();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Long Run Matrix");

            int startRow = 1;

            // 🏷️ عنوان رئيسي
            ws.Cells[startRow, 1].Value = matrix.Title;
            ws.Cells[startRow, 1, startRow, 7].Merge = true;
            ws.Cells[startRow, 1].Style.Font.Bold = true;
            ws.Cells[startRow, 1].Style.Font.Size = 14;
            ws.Cells[startRow, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            startRow += 2;

            // 🧱 رؤوس الأعمدة
            string[] headers = { "From Grade ↓ / To Grade →", "1", "2", "3", "4", "Total", "PD%" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cells[startRow, i + 1].Value = headers[i];
                ws.Cells[startRow, i + 1].Style.Font.Bold = true;
                ws.Cells[startRow, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[startRow, i + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightSteelBlue);
                ws.Cells[startRow, i + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            }

            int row = startRow;

            // 🧮 تعبئة المصفوفة 4×4
            foreach (var fromGrade in Enumerable.Range(1, 4))
            {
                row++;
                ws.Cells[row, 1].Value = fromGrade;
                ws.Cells[row, 1].Style.Font.Bold = true;

                for (int toGrade = 1; toGrade <= 4; toGrade++)
                {
                    var cell = matrix.Cells.FirstOrDefault(c => c.FromGrade == fromGrade && c.ToGrade == toGrade);
                    ws.Cells[row, toGrade + 1].Value = Math.Round(cell?.Count ?? 0, 2);
                }

                // 📊 إجمالي و PD%
                var stat = matrix.RowStats.FirstOrDefault(r => r.FromGrade == fromGrade);
                ws.Cells[row, 6].Value = stat?.TotalCount ?? 0;
                ws.Cells[row, 7].Value = stat?.PDPercent ?? 0;
                ws.Cells[row, 7].Style.Numberformat.Format = "0.0";
            }

            // 🟦 صف الإجماليات في النهاية
            row++;
            ws.Cells[row, 1].Value = "Total";
            ws.Cells[row, 1].Style.Font.Bold = true;

            for (int to = 1; to <= 4; to++)
            {
                var totalForCol = matrix.Cells.Where(c => c.ToGrade == to).Sum(c => c.Count);
                ws.Cells[row, to + 1].Value = Math.Round(totalForCol, 2);
                ws.Cells[row, to + 1].Style.Font.Bold = true;
            }

            var grandTotal = matrix.RowStats.Sum(r => r.TotalCount);
            ws.Cells[row, 6].Value = grandTotal;
            ws.Cells[row, 6].Style.Font.Bold = true;
            ws.Cells[row, 7].Value = 100;
            ws.Cells[row, 7].Style.Font.Bold = true;
            ws.Cells[row, 7].Style.Numberformat.Format = "0.0";

            // 🎨 تنسيقات عامة
            ws.Cells.AutoFitColumns();
            ws.View.ShowGridLines = false;
            ws.Cells.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            ws.Cells.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;

            // 🧱 حدود الجدول
            using (var range = ws.Cells[startRow, 1, row, 7])
            {
                range.Style.Border.Top.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                range.Style.Border.Bottom.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                range.Style.Border.Left.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                range.Style.Border.Right.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
            }

            return await package.GetAsByteArrayAsync();
        }

    }
}
