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
        //ApiResponse<List<List<double>>> CalculateAverageTransitionMatrixFromMemory(List<List<double>> transitionMatrix);

        //// 🧠 حساب مصفوفة المدى الطويل من الذاكرة
        //ApiResponse<List<List<double>>> CalculateLongRunMatrixFromMemory(List<List<double>> transitionMatrix);

        //// 🧠 حساب معدل التعثر الفعلي من الذاكرة
        //ApiResponse<double> CalculateObservedDefaultRateFromMemory(List<List<double>> transitionMatrix);

        Task<PagedResult<PDTransitionMatrixDto>> GetTransitionMatricesPagedAsync(PDMatrixFilterDto filter);
        Task<byte[]> ExportTransitionMatrixToExcelAsync(PDMatrixFilterDto filter);
        Task<List<TransitionMatrixDto>> GetYearlyAverageTransitionMatricesAsync(PDMatrixFilterDto filter);

        Task<byte[]> ExportYearlyAverageToExcelAsync(PDMatrixFilterDto filter);
        Task<TransitionMatrixDto> CalculateLongRunAverageTransitionMatrixAsync();

        Task<byte[]> ExportLongRunToExcelAsync();
        Task<ApiResponse<List<PDObservedRateDto>>> GetObservedDefaultRatesAsync();

        Task<byte[]> ExportObservedDefaultRatesToExcelAsync();

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

            // 🧹 حذف البيانات القديمة قبل الاستيراد
            var deleteSqlCommands = new[]
            {
                "DELETE FROM [dbo].[PDAverageCells]",
                "DELETE FROM [dbo].[PDLongRunCells]",
                "DELETE FROM [dbo].[PDMatrixCells]",
                "DELETE FROM [dbo].[PDObservedRates]",
                "DELETE FROM [dbo].[PDTransitionCells]",
                "DELETE FROM [dbo].[CustomerGrades]",
                "DELETE FROM [dbo].[PDMonthlyRowStats]",
                "DELETE FROM [dbo].[PDMonthlyTransitionCells]",
                "DELETE FROM [dbo].[PDYearlyAverageCells]",
                "DELETE FROM [dbo].[PDObservedRates]",
            };

            foreach (var sql in deleteSqlCommands)
            {
                await _uow.DbContext.Database.ExecuteSqlRawAsync(sql);
            }


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
                        await CalculateAllYearlyAverageTransitionMatricesAsync();
                        await CalculateAndSaveObservedDefaultRatesAsync();

                        //var average = CalculateAverageTransitionMatrixFromMemory(transition.Data);
                        //var longRun = CalculateLongRunMatrixFromMemory(transition.Data);
                        //var odr = CalculateObservedDefaultRateFromMemory(transition.Data);

                        // 💾 حفظ النتائج النهائية في قاعدة البيانات
                        await SaveCalculatedMatricesAsync(
                            pool,
                            newVersion,
                            transition,
                            //average,
                            //longRun,
                            //odr,
                            bulkConfig,
                            //yearlyAverage,
                            currentYear
                        );

                        //// 📊 تصدير النتائج إلى Excel
                        //string exportFilePath = await ExportResultsToExcelAsync(
                        //    pool,
                        //    newVersion,
                        //    transition,
                        //    average,
                        //    longRun,
                        //    odr
                        //);

                        // 🧹 تنظيف الملفات المؤقتة
                        if (File.Exists(tempFilePath))
                            File.Delete(tempFilePath);

                        await transaction.CommitAsync();

                        return ApiResponse<string>.SuccessResponse(
                            $"✅ PD Calculations completed successfully for Pool {pool.Name} (Version {newVersion})",
                            null
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
            // 🧭 1️⃣ تجميع كل الشهور اللي ظهرت في بيانات العملاء (بداية كل شهر فقط)
            var allMonths = customers
                .SelectMany(c => c.Grades.Select(g => new DateTime(g.Month.Year, g.Month.Month, 1)))
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            // 🧩 إنشاء قائمة بالشهور الكاملة (من أول شهر إلى آخر شهر بدون فجوات)
            if (allMonths.Any())
            {
                var firstMonth = allMonths.First();
                var lastMonth = allMonths.Last();
                var completeMonths = new List<DateTime>();

                for (var date = firstMonth; date <= lastMonth; date = date.AddMonths(1))
                    completeMonths.Add(new DateTime(date.Year, date.Month, 1));

                allMonths = completeMonths;
            }

            // ⚙️ تجهيز الهياكل
            var setMonths = new HashSet<DateTime>(allMonths);
            var transitionCells = new List<PDMonthlyTransitionCell>();
            var rowStats = new List<PDMonthlyRowStat>();
            int size = (maxGrade - minGrade + 1);

            // 🔁 2️⃣ المرور على كل شهر حتى لو مفيهوش داتا (هيسجّل 0)
            foreach (var from in allMonths)
            {
                var to = from.AddYears(1);
                bool hasNextYearMonth = setMonths.Contains(to);

                // لو الشهر المقابل مش موجود → نعمل مصفوفة فاضية بالقيم صفر
                TransitionCountsResult res;
                if (hasNextYearMonth)
                {
                    // ✅ الدالة دي فعلاً بترجع TransitionCountsResult
                    res = CalculateTransitionCounts(customers, from, to, minGrade, maxGrade, defaultGrade);
                }
                else
                {
                    // 🧱 إنشاء نسخة فارغة (شهر بدون بيانات)
                    int[,] counts = new int[size, size];
                    int[] rowTotals = new int[size];
                    double[] rowPD = new double[size];

                    // تهيئة القيم كلها بـ 0
                    for (int r = 0; r < size; r++)
                    {
                        for (int c = 0; c < size; c++)
                            counts[r, c] = 0;

                        rowTotals[r] = 0;
                        rowPD[r] = 0;
                    }

                    res = new TransitionCountsResult(counts, rowTotals, rowPD, minGrade, maxGrade);
                }


                // 🧮 3️⃣ حفظ كل خلايا الانتقال (من → إلى)
                for (int r = 0; r < size; r++)
                {
                    for (int c = 0; c < size; c++)
                    {
                        int count = res.Counts[r, c]; // حتى لو 0 هيتسجل دلوقتي
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

                // 📊 4️⃣ حفظ إحصائيات الـ PD لكل صف حتى لو قيمها 0
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

            // 💾 5️⃣ حفظ جميع البيانات دفعة واحدة
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

            // 📤 6️⃣ رجّع عدد السجلات المدخلة
            return transitionCells.Count + rowStats.Count;
        }

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

        private async Task SaveCalculatedMatricesAsync(
          Pool pool,
          int version,
          ApiResponse<List<List<double>>> transition,
          BulkConfig config,
          int? year = null)
        {
            // ============================================
            // 1️⃣ تقسيم البيانات إلى دفعات صغيرة
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

            if (!pdMatrixCells.Any())
                return;

            // ============================================
            // 2️⃣ تقسيم الإدخال على دفعات Bulk أصغر
            // ============================================
            const int batchSize = 50_000; // 👈 يمكنك تقليلها إذا ما زال هناك Timeout
            int totalCount = pdMatrixCells.Count;
            int totalBatches = (int)Math.Ceiling(totalCount / (double)batchSize);

            for (int batch = 0; batch < totalBatches; batch++)
            {
                var chunk = pdMatrixCells
                    .Skip(batch * batchSize)
                    .Take(batchSize)
                    .ToList();

                try
                {
                    // ⚙️ إعداد bulk config مستقل لكل دفعة
                    var bulkConfig = new BulkConfig
                    {
                        BatchSize = batchSize,
                        UseTempDB = true, // يحسن الأداء ويمنع قفل الجدول
                        BulkCopyTimeout = 0, // لا يوجد Timeout هنا
                        PreserveInsertOrder = true
                    };

                    await _uow.DbContext.BulkInsertAsync(chunk, bulkConfig);
                    _uow.DbContext.ChangeTracker.Clear();

                    Console.WriteLine($"✅ Saved batch {batch + 1}/{totalBatches} ({chunk.Count} records)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Batch {batch + 1} failed: {ex.Message}");
                }
            }
        }

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

        private async Task BulkInsertYearlyAverageBatchAsync(List<PDYearlyAverageCell> data, int maxRetries = 3)
        {
            if (data == null || !data.Any())
                return;

            int attempt = 0;
            bool success = false;
            Exception lastError = null;

            while (!success && attempt < maxRetries)
            {
                attempt++;
                try
                {
                    Console.WriteLine($"🔄 [BulkInsert] Attempt {attempt}/{maxRetries} - {data.Count} records");

                    var bulkConfig = new BulkConfig
                    {
                        UseTempDB = true,
                        PreserveInsertOrder = false,
                        SetOutputIdentity = false,
                        EnableStreaming = true,
                        BatchSize = 50_000,
                        BulkCopyTimeout = 0
                    };

                    await _uow.DbContext.BulkInsertAsync(data, bulkConfig);
                    _uow.DbContext.ChangeTracker.Clear();

                    Console.WriteLine($"✅ [BulkInsert] Batch inserted successfully on attempt {attempt}");
                    success = true;
                }
                catch (Exception ex)
                {
                    lastError = ex;

                    string msg = ex.Message.ToLowerInvariant();
                    bool transient =
                        msg.Contains("timeout") ||
                        msg.Contains("closed") ||
                        msg.Contains("transport-level error") ||
                        msg.Contains("connection") ||
                        msg.Contains("deadlocked");

                    if (transient)
                    {
                        Console.WriteLine($"⚠️ [BulkInsert] Transient error on attempt {attempt}: {ex.Message}");
                        Console.WriteLine("⏳ Retrying in 5 seconds...");
                        await Task.Delay(5000);
                    }
                    else
                    {
                        Console.WriteLine($"❌ [BulkInsert] Fatal error: {ex.Message}");
                        break;
                    }
                }
            }

            if (!success && lastError != null)
            {
                Console.WriteLine($"🚨 [BulkInsert] Failed after {maxRetries} retries. Last error: {lastError.Message}");
                throw new Exception($"Bulk insert failed after {maxRetries} retries", lastError);
            }
        }


        public async Task CalculateAllYearlyAverageTransitionMatricesAsync()
        {
            try
            {
                _uow.DbContext.Database.SetCommandTimeout(0);

                // 🧱 تحميل كل الـ IDs لتقليل الذاكرة
                var allIds = await _uow.DbContext.PDMonthlyTransitionCells
                    .AsNoTracking()
                    .Select(x => x.ID)
                    .ToListAsync();

                if (!allIds.Any())
                    return;

                const int chunkSize = 100_000;
                var yearlyCellsBuffer = new List<PDYearlyAverageCell>();

                for (int i = 0; i < allIds.Count; i += chunkSize)
                {
                    var chunkIds = allIds.Skip(i).Take(chunkSize).ToList();

                    var chunkData = await _uow.DbContext.PDMonthlyTransitionCells
                        .AsNoTracking()
                        .Where(x => chunkIds.Contains(x.ID))
                        .ToListAsync();

                    // 🔁 تجميع حسب PoolId + Year
                    foreach (var group in chunkData.GroupBy(c => new { c.PoolId, c.Year }))
                    {
                        int currentYear = group.Key.Year;
                        var monthsInYear = group.Select(x => x.Month).Distinct().ToList();

                        // ✅ لو السنة 2020 نحسب فقط يناير
                        if (currentYear == 2020)
                        {
                            monthsInYear = monthsInYear.Where(m => m == 1).ToList();
                        }

                        // ⚙️ فلترة البيانات حسب الشهور المحددة
                        var filteredGroup = group
                            .Where(c => monthsInYear.Contains(c.Month))
                            .ToList();

                        // 📊 لو السنة 2020 → احسب المتوسط على شهر واحد فقط (يناير)
                        int monthCount = currentYear == 2020 ? 1 : (monthsInYear.Count == 0 ? 1 : monthsInYear.Count);

                        var grouped = filteredGroup
                            .GroupBy(c => new { c.RowIndex, c.ColumnIndex })
                            .ToDictionary(
                                g => (g.Key.RowIndex, g.Key.ColumnIndex),
                                g => Math.Round(g.Sum(x => x.Value) / monthCount, 4)
                            );

                        // 🧱 تجهيز البيانات للإدخال
                        for (int from = 1; from <= 4; from++)
                        {
                            for (int to = 1; to <= 4; to++)
                            {
                                double value = grouped.TryGetValue((from - 1, to - 1), out double v) ? v : 0;
                                yearlyCellsBuffer.Add(new PDYearlyAverageCell
                                {
                                    PoolId = group.Key.PoolId,
                                    Year = currentYear,
                                    RowIndex = from - 1,
                                    ColumnIndex = to - 1,
                                    Value = value,
                                    CreatedAt = DateTime.UtcNow
                                });
                            }
                        }
                    }

                    if (yearlyCellsBuffer.Any())
                    {
                        await BulkInsertYearlyAverageBatchAsync(yearlyCellsBuffer, maxRetries: 3);
                        yearlyCellsBuffer.Clear();
                    }




                    Console.WriteLine($"✅ Processed chunk {i / chunkSize + 1} / {Math.Ceiling(allIds.Count / (double)chunkSize)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
            }
        }

        public async Task<List<TransitionMatrixDto>> GetYearlyAverageTransitionMatricesAsync(PDMatrixFilterDto filter)
        {
            var query = _uow.DbContext.PDYearlyAverageCells.AsQueryable();
            var rrr = query.ToList();
            //if (filter.PoolId > 0)
            //    query = query.Where(c => c.PoolId == filter.PoolId);

            if (filter.Year.HasValue)
                query = query.Where(c => c.Year == filter.Year.Value);

            var data = await query.ToListAsync();
            if (!data.Any())
                return new List<TransitionMatrixDto>();

            var distinctYears = data.Select(d => d.Year).Distinct().OrderBy(y => y).ToList();
            var result = new List<TransitionMatrixDto>();

            foreach (var year in distinctYears)
            {
                var yearData = data.Where(c => c.Year == year).ToList();
                if (!yearData.Any())
                    continue;

                // 🧮 حساب المتوسط بطريقة ديناميكية بناءً على عدد السجلات الفعلية
                // (حتى لو كانت السنة تحتوي على شهر واحد فقط مثل 2021)
                var grouped = yearData
                    .GroupBy(c => new { c.RowIndex, c.ColumnIndex })
                    .ToDictionary(
                        g => (g.Key.RowIndex, g.Key.ColumnIndex),
                        g => Math.Round(g.Average(x => x.Value), 6)
                    );

                // 🧱 بناء مصفوفة كاملة 4×4
                var avgCells = new List<TransitionCellDto>();
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

                // 📊 حساب Totals و PD لكل صف
                var rowStats = avgCells
                    .GroupBy(x => x.FromGrade)
                    .Select(g =>
                    {
                        var total = g.Sum(x => x.Count);
                        var pd = g.FirstOrDefault(x => x.ToGrade == 4)?.Count ?? 0;
                        var pdPercent = total > 0 ? Math.Round((pd / total) * 100, 4) : 0;

                        return new RowStatDto
                        {
                            FromGrade = g.Key,
                            TotalCount = (int)Math.Round(total),
                            PDPercent = pdPercent
                        };
                    })
                    .ToList();

                result.Add(new TransitionMatrixDto
                {
                    Year = year,
                    Title = $"Yearly Average Transition Matrix - {year}",
                    IsYearlyAverage = true,
                    Cells = avgCells,
                    RowStats = rowStats
                });
            }

            return result.OrderBy(r => r.Year).ToList();
        }

        public async Task<byte[]> ExportYearlyAverageToExcelAsync(PDMatrixFilterDto filter)
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // 🧩 استدعاء المصفوفات السنوية المحسوبة
            List<TransitionMatrixDto> matrices = await GetYearlyAverageTransitionMatricesAsync(filter);

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
                ws.Cells[startRow, 1, startRow, 7].Merge = true;
                ws.Cells[startRow, 1].Style.Font.Bold = true;
                ws.Cells[startRow, 1].Style.Font.Size = 14;
                ws.Cells[startRow, 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[startRow, 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96)); // Dark Blue
                ws.Cells[startRow, 1].Style.Font.Color.SetColor(System.Drawing.Color.White);
                ws.Cells[startRow, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                startRow += 2;

                // 🧱 رؤوس الأعمدة
                string[] headers = { "From / To", "Grade 1", "Grade 2", "Grade 3", "Grade 4", "Total", "PD %" };
                for (int i = 0; i < headers.Length; i++)
                {
                    ws.Cells[startRow, i + 1].Value = headers[i];
                    ws.Cells[startRow, i + 1].Style.Font.Bold = true;
                    ws.Cells[startRow, i + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                    ws.Cells[startRow, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[startRow, i + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96)); // navy blue
                    ws.Cells[startRow, i + 1].Style.Font.Color.SetColor(System.Drawing.Color.White);
                }

                int currentRow = startRow;

                // 🧮 عرض الصفوف (Grades)
                for (int from = 1; from <= 4; from++)
                {
                    currentRow++;
                    ws.Cells[currentRow, 1].Value = $"Grade {from}";
                    ws.Cells[currentRow, 1].Style.Font.Bold = true;
                    ws.Cells[currentRow, 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[currentRow, 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(221, 235, 247)); // Light Blue

                    double total = 0;

                    // الأعمدة (To Grades)
                    for (int to = 1; to <= 4; to++)
                    {
                        var cell = matrix.Cells.FirstOrDefault(c => c.FromGrade == from && c.ToGrade == to);
                        double value = cell?.Count ?? 0;

                        ws.Cells[currentRow, to + 1].Value = value;
                        ws.Cells[currentRow, to + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;

                        // تظليل تدريجي خفيف حسب العمود
                        if (value > 0)
                        {
                            ws.Cells[currentRow, to + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                            ws.Cells[currentRow, to + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(189, 215, 238)); // soft blue
                        }

                        total += value;
                    }

                    // الإجمالي (Total)
                    ws.Cells[currentRow, 6].Value = total;
                    ws.Cells[currentRow, 6].Style.Font.Bold = true;
                    ws.Cells[currentRow, 6].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                    ws.Cells[currentRow, 6].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(221, 235, 247));

                    // PD %
                    var rowStat = matrix.RowStats.FirstOrDefault(x => x.FromGrade == from);
                    double pd = rowStat?.PDPercent ?? 0;
                    ws.Cells[currentRow, 7].Value = pd / 100;
                    ws.Cells[currentRow, 7].Style.Numberformat.Format = "0.00%";
                    ws.Cells[currentRow, 7].Style.Font.Bold = true;
                    ws.Cells[currentRow, 7].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;

                    // لون العمود حسب القيمة
                    if (pd >= 100)
                    {
                        ws.Cells[currentRow, 7].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(255, 199, 206)); // red shade
                        ws.Cells[currentRow, 7].Style.Font.Color.SetColor(System.Drawing.Color.Red);
                    }
                    else
                    {
                        ws.Cells[currentRow, 7].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(198, 239, 206)); // green shade
                        ws.Cells[currentRow, 7].Style.Font.Color.SetColor(System.Drawing.Color.DarkGreen);
                    }
                }

                // ✅ الحدود العامة
                using (var range = ws.Cells[startRow, 1, currentRow, 7])
                {
                    range.Style.Border.Top.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Left.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Right.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                    range.Style.Border.Bottom.Style = OfficeOpenXml.Style.ExcelBorderStyle.Thin;
                }

                // 🎨 تنسيق عام
                ws.Cells.AutoFitColumns();
                ws.View.ShowGridLines = false;
                ws.Cells.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                ws.Cells.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;
            }

            return await package.GetAsByteArrayAsync();
        }

        public async Task<TransitionMatrixDto> CalculateLongRunAverageTransitionMatrixAsync()
        {
            // 🧱 1. اجلب كل البيانات السنوية المخزّنة في الجدول
            var allYearly = await _uow.DbContext.PDYearlyAverageCells.ToListAsync();

            if (!allYearly.Any())
                return null;

            // 🧮 2. احسب عدد السنوات الفعلية المميزة
            var distinctYears = allYearly.Select(x => x.Year).Distinct().Count();

            if (distinctYears == 0)
                distinctYears = 1; // حماية من القسمة على صفر

            // 🧮 3. حساب المتوسط العام لكل خلية (من → إلى) عبر كل السنوات
            var grouped = allYearly
                .GroupBy(c => new { c.RowIndex, c.ColumnIndex })
                .Select(g => new
                {
                    From = g.Key.RowIndex + 1,
                    To = g.Key.ColumnIndex + 1,
                    // ✅ نحسب مجموع القيم ونقسم على عدد السنوات فقط
                    AvgValue = Math.Round(g.Sum(x => x.Value) / 6, 4)
                })
                .ToList();

            // ✅ 4. بناء مصفوفة 4×4 كاملة حتى لو بعض القيم مفقودة
            var avgCells = new List<TransitionCellDto>();
            for (int from = 1; from <= 4; from++)
            {
                for (int to = 1; to <= 4; to++)
                {
                    double value = grouped.FirstOrDefault(g => g.From == from && g.To == to)?.AvgValue ?? 0;
                    avgCells.Add(new TransitionCellDto
                    {
                        FromGrade = from,
                        ToGrade = to,
                        Count = value
                    });
                }
            }

            // 📊 5. نحسب Totals و PD%
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
                        TotalCount = (int)Math.Round(total),
                        PDPercent = pdPercent
                    };
                })
                .ToList();

            // 🎯 6. نرجع النتيجة النهائية
            return new TransitionMatrixDto
            {
                Title = $"Long Run Average Transition Matrix (Based on {distinctYears} Years)",
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

        public async Task<ApiResponse<string>> CalculateAndSaveObservedDefaultRatesAsync()
        {
            try
            {
                _uow.DbContext.Database.SetCommandTimeout(0);

                // 🧱 1️⃣ تحميل البيانات السنوية
                var yearlyData = await _uow.DbContext.PDYearlyAverageCells
                    .AsNoTracking()
                    .ToListAsync();

                if (!yearlyData.Any())
                    return ApiResponse<string>.FailResponse("⚠️ لا توجد بيانات في PDYearlyAverageCells.");

                // 🗓️ 2️⃣ استخراج السنوات المميزة
                var years = yearlyData.Select(x => x.Year).Distinct().OrderBy(y => y).ToList();

                var odrList = new List<PDObservedRate>();

                foreach (var year in years)
                {
                    // 🧩 بيانات السنة الحالية
                    var yearCells = yearlyData.Where(x => x.Year == year).ToList();
                    if (!yearCells.Any())
                        continue;

                    // 🧮 3️⃣ مجموع العملاء اللي انتقلوا إلى Default
                    double defaultSum = yearCells
                        .Where(x => x.ColumnIndex == 3 && x.RowIndex < 3) // 0→Grade1, 1→Grade2, 2→Grade3
                        .Sum(x => x.Value);

                    // 🧮 4️⃣ مجموع إجمالي العملاء في الدرجات 1–3
                    double totalSum = yearCells
                        .Where(x => x.RowIndex < 3)
                        .GroupBy(x => x.RowIndex)
                        .Sum(g => g.Sum(c => c.Value));

                    // ⚙️ 5️⃣ حساب النسبة المئوية
                    double odrPercent = totalSum == 0 ? 0 : Math.Round((defaultSum / totalSum) * 100, 4);

                    // 💾 6️⃣ حفظ النتيجة كنسبة مئوية
                    odrList.Add(new PDObservedRate
                    {
                        PoolId = yearCells.First().PoolId,
                        Year = year,
                        ObservedDefaultRate = odrPercent,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                // 🧹 حذف القيم القديمة قبل الحفظ
                await _uow.DbContext.Database.ExecuteSqlRawAsync("DELETE FROM [dbo].[PDObservedRates]");

                // 🚀 إدخال النتائج الجديدة
                if (odrList.Any())
                    await _uow.DbContext.BulkInsertAsync(odrList);

                return ApiResponse<string>.SuccessResponse("✅ Observed Default Rates (as %) calculated and saved successfully.");
            }
            catch (Exception ex)
            {
                return ApiResponse<string>.FailResponse($"❌ Error while calculating ODR: {ex.Message}");
            }
        }

        public async Task<ApiResponse<List<PDObservedRateDto>>> GetObservedDefaultRatesAsync()
        {
            try
            {
                _uow.DbContext.Database.SetCommandTimeout(0);

                // 🧱 جلب كل السجلات من الجدول
                var data = await _uow.DbContext.PDObservedRates
                    .AsNoTracking()
                    .Where(x => x.Year != 2021) // 👈 استبعاد سنة 2021
                    .OrderBy(x => x.Year)
                    .ToListAsync();

                if (data == null || !data.Any())
                    return ApiResponse<List<PDObservedRateDto>>.FailResponse("⚠️ لا توجد بيانات ODR محفوظة حتى الآن.");

                // 🔄 تحويلها إلى DTO منسق
                var result = data.Select(x => new PDObservedRateDto
                {
                    Year = x.Year,
                    ObservedDefaultRate = x.ObservedDefaultRate,
                }).ToList();

                return ApiResponse<List<PDObservedRateDto>>.SuccessResponse("✅ تم جلب Observed Default Rates بنجاح.", result);
            }
            catch (Exception ex)
            {
                return ApiResponse<List<PDObservedRateDto>>.FailResponse($"❌ حدث خطأ أثناء جلب البيانات: {ex.Message}");
            }
        }

        public async Task<byte[]> ExportObservedDefaultRatesToExcelAsync()
        {
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // 🧱 جلب البيانات من الجدول (بدون سنة 2021)
            var data = await _uow.DbContext.PDObservedRates
                .AsNoTracking()
                .Where(x => x.Year != 2021)
                .OrderBy(x => x.Year)
                .ToListAsync();

            if (data == null || !data.Any())
                return Array.Empty<byte>();

            using var package = new ExcelPackage();
            var ws = package.Workbook.Worksheets.Add("Observed Default Rates");

            int startRow = 1;

            // 🏷️ عنوان رئيسي
            ws.Cells[startRow, 1].Value = "Observed Default Rates by Year";
            ws.Cells[startRow, 1, startRow, 4].Merge = true;
            ws.Cells[startRow, 1].Style.Font.Bold = true;
            ws.Cells[startRow, 1].Style.Font.Size = 14;
            ws.Cells[startRow, 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            ws.Cells[startRow, 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96)); // Dark Blue
            ws.Cells[startRow, 1].Style.Font.Color.SetColor(System.Drawing.Color.White);
            ws.Cells[startRow, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            startRow += 2;

            // 🧱 رؤوس الأعمدة
            string[] headers = { "Year", "Pool ID", "Observed Default Rate (%)", "Created At" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cells[startRow, i + 1].Value = headers[i];
                ws.Cells[startRow, i + 1].Style.Font.Bold = true;
                ws.Cells[startRow, i + 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
                ws.Cells[startRow, i + 1].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
                ws.Cells[startRow, i + 1].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.FromArgb(0, 32, 96));
                ws.Cells[startRow, i + 1].Style.Font.Color.SetColor(System.Drawing.Color.White);
            }

            int row = startRow;
            foreach (var item in data)
            {
                row++;
                ws.Cells[row, 1].Value = item.Year;
                ws.Cells[row, 2].Value = item.PoolId;
                ws.Cells[row, 3].Value = (double)item.ObservedDefaultRate;
                ws.Cells[row, 3].Style.Numberformat.Format = "0.0000"; // عرض 4 أرقام عشرية
                ws.Cells[row, 4].Value = item.CreatedAt.ToString("yyyy-MM-dd HH:mm");
            }

            // 🧮 صف الإجماليات
            row++;
            ws.Cells[row, 1].Value = "Average";
            ws.Cells[row, 1].Style.Font.Bold = true;
            ws.Cells[row, 3].Formula = $"AVERAGE(C{startRow + 1}:C{row - 1})";
            ws.Cells[row, 3].Style.Numberformat.Format = "0.0000";
            ws.Cells[row, 3].Style.Font.Bold = true;
            ws.Cells[row, 3].Style.Fill.PatternType = OfficeOpenXml.Style.ExcelFillStyle.Solid;
            ws.Cells[row, 3].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.LightYellow);

            // 🎨 تنسيقات عامة
            ws.Cells.AutoFitColumns();
            ws.View.ShowGridLines = false;
            ws.Cells.Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;
            ws.Cells.Style.VerticalAlignment = OfficeOpenXml.Style.ExcelVerticalAlignment.Center;

            // 🧱 حدود الجدول
            using (var range = ws.Cells[startRow, 1, row, 4])
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
