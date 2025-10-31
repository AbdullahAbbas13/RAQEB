using EFCore.BulkExtensions;
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


    }
}
