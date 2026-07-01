using iSMSOverdue.InboundInvoice.Helper;
using iSMSOverdue.InboundInvoice.Model;
using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace iSMSOverdue.InboundInvoice.Service.Inbound
{
    public sealed class ServiceInboundJob : IServiceInboundJob
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        public async Task ExecJob(ArgumentModel cfg, string sessionId)
        {
            // Preflight validations & resolution (paths, mapping, AwsKey decrypt, DB connections, SQL inspection)
            var pre = PreflightHelper.RunAll(cfg, _log);

            _log.Info("------Start Overdue Invoice Job------");

            // Load key locker (used both for AwsKey decryption (S3) and OverdueKey decryption (ZIP/HMAC))
            var keyLocker = ReadLocker(cfg.FileKeyLocker);

            // 1) DOWNLOAD FROM S3 TO PRIMARY, THEN REPLICATE & COPY TO CsvInbound
            var uploadPaths = FileHelper.ReadFile(cfg.FileUpload);
            if (uploadPaths.Count == 0)
            {
                _log.Info("fileupload.txt has no paths. Skipping S3.");
            }
            else
            {
                var primaryInbound = uploadPaths[0];
                if (!Directory.Exists(primaryInbound)) Directory.CreateDirectory(primaryInbound);

                var map = pre.Map; // inbound row for this job
                _log.Info($"Download file from S3 started for bucket={map.bucket_name}, prefix={map.prefix}");

                var downloads = await S3Helper.Download(map, cfg.FileKeyLocker, primaryInbound);
                _log.Info($"✅ files downloaded from s3: {downloads.Count(x => x.status)}");
                _log.Info($"⛔ files failed from s3: {downloads.Count(x => !x.status)}");

                // replicate to other shares (skip empty)
                var shares = uploadPaths.Skip(1).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                ReplicateZips(primaryInbound, shares);

                // also copy to CsvInbound for extraction (BV1 stages in a working folder before processing)
                CopyZipsTo(cfg.CsvInbound, Directory.GetFiles(primaryInbound, "*.zip"));

                // delete each object from S3 (as BV1 does by recalculating prefix with filename appended)
                await DeleteEachFromS3(map, cfg.FileKeyLocker, Directory.GetFiles(primaryInbound, "*.zip").Select(Path.GetFileName));
            }

            // 2) EXTRACT ZIPs FROM CsvInbound WITH HMAC PASSWORD (OverdueKey)
            var zipFiles = Directory.Exists(cfg.CsvInbound)
                ? Directory.GetFiles(cfg.CsvInbound, "*.zip").ToList()
                : new List<string>();

            foreach (var z in zipFiles)
            {
                var nameNoExt = Path.GetFileNameWithoutExtension(z);
                var output = Path.Combine(cfg.CsvInbound, $"{nameNoExt}_tempfile");

                // password = HMAC-SHA256(timestamp) using decrypted OverdueKey (same scheme as BV1 with BV1Key)
                var password = BuildZipPassword_FromTimestamp(nameNoExt, keyLocker);

                bool badZip = false;
                try
                {
                    ZipHelper.ExtractFile(z, output, password);
                    _log.Info($"extract file: {Path.GetFileName(z)}");
                }
                catch (Exception ex)
                {
                    badZip = true;
                    if (ex.Message.IndexOf("bad password", StringComparison.OrdinalIgnoreCase) >= 0)
                        _log.Error($"Failed extract file: {nameNoExt} : bad password=<REDACTED>");
                    else
                        _log.Error($"Failed extract file: {nameNoExt} : {ex.Message}");
                }

                // BV1 deletes a bad-zip to avoid re-processing on next run
                if (badZip && File.Exists(z))
                {
                    try { File.Delete(z); _log.Info($"file {nameNoExt} deleted."); } catch { }
                }
            }

            // 3) READ CSVs INTO STAGING TABLE (Users.dbo.iSMSOverdue_InvoiceTemp)
            var stagingAll = new DataTable();

            stagingAll.Columns.Add("invoice_legal_number", typeof(string));
            stagingAll.Columns.Add("status", typeof(string));
            stagingAll.Columns.Add("csv_filename", typeof(string));
            stagingAll.Columns.Add("file_sequence", typeof(long));
            stagingAll.Columns.Add("load_dtm", typeof(DateTime));

            // Collect temp folders created by extractions
            var tempDirs = Directory.Exists(cfg.CsvInbound)
                ? Directory.GetDirectories(cfg.CsvInbound, "*_tempfile").ToList()
                : new List<string>();

            foreach (var dir in tempDirs)
            {
                var zipName = Path.GetFileName(dir).Replace("_tempfile", ".zip");
                var seq = ExtractSequenceNumber(Path.GetFileNameWithoutExtension(zipName));

                var csvs = Directory.GetFiles(dir, "*.csv");

                foreach (var csv in csvs)
                {
                    try
                    {
                        // parse minimal CSV -> invoice_legal_number + status
                        ReadInvoiceCsvTo(stagingAll, csv, Path.GetFileName(csv), seq);
                    }
                    catch (Exception ex)
                    {
                        _log.Error($"Error read csv {Path.GetFileName(csv)}: {ex.Message}");
                    }
                }
            }

            // 4) BULK INSERT TO STAGING
            if (stagingAll.Rows.Count > 0)
            {
                await DBHelper.BulkInsert(
                    pre.DbUser.First(),
                    stagingAll,
                    "Users.dbo.iSMSOverdue_InvoiceTemp"
                );

                _log.Info($"Staging insert done. Rows: {stagingAll.Rows.Count:N0}");
            }
            else
            {
                _log.Info("No CSV rows found; nothing to insert into staging.");
            }

            // ===================== BUILD AREA FROM STAGING =====================
            var stagingAreas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow row in stagingAll.Rows)
            {
                var inv = Convert.ToString(row["invoice_legal_number"]);
                var area = ExtractAreaFromInvoice(inv);

                if (!string.IsNullOrWhiteSpace(area))
                    stagingAreas.Add(area);
            }

            // ===================== BUILD DBUSER AREA MAP =====================
            var dbUserMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var conn in pre.DbUser)
            {
                var catalog = ExtractInitialCatalog(conn);

                if (string.IsNullOrWhiteSpace(catalog))
                    continue;

                var parts = catalog.Split('_');

                if (parts.Length < 3)
                {
                    _log.Warn($"Invalid catalog format: {catalog}");
                    continue;
                }

                var area = parts[1]; // middle part

                if (!dbUserMap.ContainsKey(area))
                    dbUserMap[area] = conn;
            }

            // ===================== FILE PER AREA (ORIGINAL LOGIC) =====================
            var fileByArea = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (DataRow r in stagingAll.Rows)
            {
                var inv = Convert.ToString(r["invoice_legal_number"]);
                var area = ExtractAreaFromInvoice(inv);

                if (string.IsNullOrWhiteSpace(area)) continue;

                if (!fileByArea.ContainsKey(area))
                    fileByArea[area] = Convert.ToString(r["csv_filename"]) ?? "batch";
            }

            var env = ConfigurationManager.AppSettings["Environment"] ?? "DEV";

            //dev log:
            _log.Info($"stagingAreas: {string.Join(", ", stagingAreas)}");

            // ===================== FINAL EXECUTION =====================
            foreach (var kv in pre.DbMain)
            {
                var areaCode = kv.Key;

                // must exist in staging
                // if (!stagingAreas.Contains(areaCode))
                // {
                //     _log.Info($"Skip area {areaCode}: not found in staging");
                //     continue;
                // }

                // must exist in DbUser
                if (!dbUserMap.TryGetValue(areaCode, out var conn))
                {
                    _log.Warn($"Skip area {areaCode}: not found in DbUser catalog");
                    continue;
                }

                var dbName = ExtractInitialCatalog(conn); // ✅ AREA DB
                var fileForArea = fileByArea.ContainsKey(areaCode)
                    ? fileByArea[areaCode]
                    : "batch";

                _log.Info($"Processing area {areaCode} | DB={dbName} | FILE={fileForArea}");

                // ===================== SQL Main =====================
                if (!string.IsNullOrWhiteSpace(pre.SqlMain))
                {
                    try
                    {
                        await DBHelper.ExecuteNonQuery(conn, pre.SqlMain);
                        _log.Info($"SQL main executed for area {areaCode}.");
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"SQL main failed for area {areaCode}.");
                    }
                }

                // ===================== SQL =====================
                if (!string.IsNullOrWhiteSpace(pre.SqlFile))
                {
                    var raw = pre.SqlFile;

                    // 1) Strip SSMS batch separators (GO) — not supported by SqlClient.
                    //    This preserves variable scope for DECLAREs (e.g., @err) across the whole script.
                    raw = Regex.Replace(raw, @"(?mi)^\s*GO\s*(--.*)?$", "");

                    // 2) Token replacement
                    var sql = raw
                        .Replace("$$ENVIRONMENT$$", env)
                        .Replace("$$AREA_CODE$$", areaCode)
                        .Replace("$$DB_NAME$$", dbName)
                        .Replace("$$FILE_NAME$$", fileForArea);

                    // 3) Safety patches we already apply
                    sql = sql.Replace("@so_err", "@err");

                    if (!Regex.IsMatch(sql, @"(?i)\bdeclare\s+@start_job\b"))
                        sql = "DECLARE @start_job datetime = GETDATE();\r\n" + sql;

                    if (!Regex.IsMatch(sql, @"(?i)\bdeclare\s+@err\b"))
                        sql = "DECLARE @err nvarchar(4000);\r\n" + sql;

                    try
                    {
                        var result = await DBHelper.Execute(conn, sql);

                        if (result != null &&
                            result.Columns.Contains("msg") &&
                            result.Rows.Count > 0)
                        {
                            var msg = Convert.ToString(result.Rows[0]["msg"]);

                            if (!string.IsNullOrWhiteSpace(msg))
                                _log.Error($"inbound_invoice returned msg for area {areaCode}: {msg}");
                            else
                                _log.Info($"✅ Area {areaCode}: success");
                        }
                        else
                        {
                            _log.Info($"✅ {areaCode}: Execute inbound_invoice success.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, $"Execute inbound_invoice failed for area {areaCode}.");
                    }
                }
            }

            // 6) CLEANUP STAGING (TRUNCATE then fallback DELETE)
            await CleanupStaging(pre.DbUser.First());
            
            // 7) CLEANUP TEMP FOLDERS AND ZIPS
            CleanupTempAndZips(cfg.CsvInbound, tempDirs);

            _log.Info("------End Overdue Invoice Job------");
        }

        // ===================== helpers =====================

        private static string ReadLocker(string lockerPath)
        {
            using (var fs = new FileStream(lockerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) return sr.ReadToEnd().Trim();
        }

        private static void ReplicateZips(string sourceDir, List<string> destinations)
        {
            if (!Directory.Exists(sourceDir)) return;
            var zips = Directory.GetFiles(sourceDir, "*.zip");
            foreach (var dest in destinations)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(dest)) continue;
                    if (!Directory.Exists(dest)) Directory.CreateDirectory(dest);

                    foreach (var z in zips)
                    {
                        var target = Path.Combine(dest, Path.GetFileName(z));
                        if (!File.Exists(target))
                            File.Copy(z, target, false);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(ex, $"Replication to {dest} encountered an issue.");
                }
            }
        }

        private static void CopyZipsTo(string destDir, IEnumerable<string> sourceFiles)
        {
            if (string.IsNullOrWhiteSpace(destDir)) return;
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            foreach (var f in sourceFiles)
            {
                try
                {
                    var to = Path.Combine(destDir, Path.GetFileName(f));
                    if (!File.Exists(to)) File.Copy(f, to, false);
                }
                catch (Exception ex)
                {
                    _log.Warn(ex, $"Copy to {destDir} failed for {Path.GetFileName(f)}.");
                }
            }
        }

        private static async Task DeleteEachFromS3(BucketMapperModel baseMap, string keyLockerPath, IEnumerable<string> fileNames)
        {
            foreach (var f in fileNames)
            {
                try
                {
                    var delMap = new BucketMapperModel
                    {
                        job_name = baseMap.job_name,
                        bucket_name = baseMap.bucket_name,
                        prefix = $"{baseMap.prefix}/{f}",
                        job_type = baseMap.job_type,
                        update_only = baseMap.update_only
                    };
                    await S3Helper.Delete(delMap, keyLockerPath);
                }
                catch (Exception ex)
                {
                    _log.Warn(ex, $"S3 delete for {f} failed.");
                }
            }
        }

        private static string BuildZipPassword_FromTimestamp(string fileNameNoExt, string keyLocker)
        {
            // filename convention: ..._{sequence}_{timestamp}
            // timestamp = last token (14 digits). compute HMAC-SHA256 over timestamp using decrypted OverdueKey
            // OverdueKey is encrypted in App.config; decrypt with keyLocker first (BV1-style master key)
            var parts = fileNameNoExt.Split('_');
            var timeToken = parts.Length > 0 ? parts[parts.Length - 1] : string.Empty;

            var overdueKeyBase64 = ConfigurationManager.AppSettings["OverdueKey"] ?? "";
            var decryptedKey = new CryptoHelper(keyLocker).Decrypt(overdueKeyBase64);
            var h = new CryptoHelper(decryptedKey);
            return h.SHA256Encrypt(timeToken);
        }

        private static long ExtractSequenceNumber(string fileNameNoExt)
        {
            // e.g. sales_order_status_12_20260310090038  => 12
            var parts = fileNameNoExt.Split('_');
            if (parts.Length < 2) return 0;
            long seq;
            if (long.TryParse(parts[parts.Length - 2], out seq)) return seq;
            return 0;
        }

        private static void ReadInvoiceCsvTo(DataTable target, string csvPath, string csvFileName, long seq)
        {
            var lines = FileHelper.ReadFile(csvPath);
            if (lines.Count == 0) return;

            var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToList();
            int idxInv = FindHeaderIndex(headers, "invoice_legal_number");
            int idxStatus = FindHeaderIndex(headers, "status");

            if (idxInv < 0)
            {
                _log.Error($"CSV {csvFileName}: header 'invoice_legal_number' not found.");
                return;
            }

            // 'status' may be empty; we'll allow idxStatus = -1
            var now = DateTime.UtcNow;

            for (int i = 1; i < lines.Count; i++)
            {
                var row = SafeSplitCsv(lines[i]);
                if (idxInv >= row.Length) continue;

                var inv = row[idxInv]?.Trim('"', ' ', '\t') ?? "";
                if (string.IsNullOrWhiteSpace(inv)) continue;

                var st = (idxStatus >= 0 && idxStatus < row.Length) ? (row[idxStatus]?.Trim('"', ' ', '\t') ?? "") : null;

                var dr = target.NewRow();
                dr["invoice_legal_number"] = inv;
                dr["status"] = string.IsNullOrEmpty(st) ? (object)DBNull.Value : st;
                dr["csv_filename"] = csvFileName;
                dr["file_sequence"] = seq;
                dr["load_dtm"] = now;

                target.Rows.Add(dr);
            }
        }

        private static int FindHeaderIndex(List<string> headers, string wanted)
        {
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i], wanted, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static string[] SafeSplitCsv(string line)
        {
            // Lightweight split for simple CSVs without embedded commas/quotes.
            // If upstream adds quoting/commas inside fields, consider a stricter CSV parser.
            return line.Split(',');
        }

        private static string ExtractAreaFromInvoice(string invoice)
        {
            if (string.IsNullOrEmpty(invoice) || invoice.Length < 4) return null;
            var first4 = invoice.Substring(0, 4);
            if (first4.Length < 3) return null;
            return first4.Substring(first4.Length - 3, 3);
        }

        private static string ExtractInitialCatalog(string connectionString)
        {
            var m = Regex.Match(connectionString ?? "", @"Initial\s+Catalog\s*=\s*([^;]+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }

        private static async Task CleanupStaging(string dbUserConn)
        {
            try
            {
                await DBHelper.ExecuteNonQuery(dbUserConn, "TRUNCATE TABLE Users.dbo.iSMSOverdue_InvoiceTemp;");
                _log.Info("Staging truncated.");
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "TRUNCATE staging failed. Trying DELETE...");
                try
                {
                    await DBHelper.ExecuteNonQuery(dbUserConn, "DELETE FROM Users.dbo.iSMSOverdue_InvoiceTemp;");
                    _log.Info("Staging deleted.");
                }
                catch (Exception ex2)
                {
                    _log.Error(ex2, "Failed to clear staging.");
                }
            }
        }

        private static void CleanupTempAndZips(string csvInbound, List<string> tempDirs)
        {
            // Remove temp folders
            foreach (var d in tempDirs)
            {
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception ex) { _log.Warn(ex, $"Delete temp folder failed: {d}"); }
            }

            // Optionally remove processed zip files
            try
            {
                var zips = Directory.GetFiles(csvInbound, "*.zip");
                foreach (var z in zips)
                {
                    try { File.Delete(z); } catch (Exception ex) { _log.Warn(ex, $"Delete zip failed: {Path.GetFileName(z)}"); }
                }
            }
            catch (Exception ex)
            {
                _log.Warn(ex, "Enumerating zips for deletion failed.");
            }
        }
    }
}