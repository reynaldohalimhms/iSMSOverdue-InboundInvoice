using iSMSOverdue.InboundInvoice.Model;
using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public sealed class PreflightResult
    {
        public string DbMain;
        public string DbUser;
        public string SqlMain;
        public string SqlFile;
        public BucketMapperModel Map;

        public List<string> UploadPaths = new List<string>();
        public bool HasUploadPaths;
    }

    public static class PreflightHelper
    {
        public static PreflightResult RunAll(ArgumentModel cfg, Logger log)
        {
            if (cfg == null) throw new ArgumentNullException("cfg");

            var (paths, hasUploads) = ValidatePathsAndPermissions(cfg, log);
            var map = ResolveS3Map(cfg.S3Bucket, cfg.JobName);
            ValidateAwsKeyDecrypt(cfg.FileKeyLocker);

            var dbMain = ResolveDbMain(cfg.DBConnectionMain);
            var dbUser = ResolveDbUser(cfg.DBConnection);

            var sqlMain = ReadSqlIfExists(cfg.FileQueryMain);
            var sqlFile = ReadSqlIfExists(cfg.FileQuery);

            if (!string.IsNullOrEmpty(sqlMain)) ValidateSqlScript(sqlMain, "FileQueryMain");
            if (!string.IsNullOrEmpty(sqlFile)) ValidateSqlScript(sqlFile, "FileQuery");

            log.Info("Preflight OK.");
            return new PreflightResult
            {
                DbMain = dbMain,
                DbUser = dbUser,
                SqlMain = sqlMain,
                SqlFile = sqlFile,
                Map = map,
                UploadPaths = paths,
                HasUploadPaths = hasUploads
            };
        }

        private static (List<string> paths, bool hasUploads) ValidatePathsAndPermissions(ArgumentModel cfg, Logger log)
        {
            var missing = new List<string>();
            var reqFiles = new[]
            {
                cfg.DBConnection, cfg.DBConnectionMain, cfg.FileKeyLocker,
                cfg.FileUpload, cfg.S3Bucket, cfg.Logger
            };

            foreach (var f in reqFiles)
                if (string.IsNullOrWhiteSpace(f) || !File.Exists(f)) missing.Add(f ?? "(null)");

            if (missing.Count > 0)
                throw new InvalidOperationException("Preflight: required files missing:\n - " + string.Join("\n - ", missing.ToArray()));

            if (string.IsNullOrWhiteSpace(cfg.CsvInbound))
                throw new InvalidOperationException("Preflight: CsvInbound path is empty.");

            if (!Directory.Exists(cfg.CsvInbound))
                Directory.CreateDirectory(cfg.CsvInbound);

            var uploadsRaw = FileHelper.ReadFile(cfg.FileUpload);
            var uploadPaths = uploadsRaw
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();

            var hasUploads = uploadPaths.Count > 0;

            if (hasUploads)
            {
                // Primary + replicas
                var primary = uploadPaths[0];
                if (!Directory.Exists(primary)) Directory.CreateDirectory(primary);
                WriteTest(primary);

                foreach (var d in uploadPaths.Skip(1))
                {
                    if (!Directory.Exists(d)) Directory.CreateDirectory(d);
                    WriteTest(d);
                }

                log.Info("Preflight: fileupload paths OK. Primary + replicas validated.");
            }
            else
            {
                // NEW: do not fail; just inform
                log.Info("Preflight: fileupload.txt is empty. S3 operations will be skipped.");
            }

            // Also basic write test for csvInbound (we did mkdir above already)
            WriteTest(cfg.CsvInbound);

            // We still validate S3 map + key decrypt elsewhere to keep the environment consistent,
            // but ServiceInboundJob will skip S3 when hasUploads==false.
            return (uploadPaths, hasUploads);
        }

        private static void WriteTest(string dir)
        {
            var p = Path.Combine(dir, $"_preflight_write_{Guid.NewGuid():N}.tmp");
            try { File.WriteAllText(p, "ok"); }
            finally { try { if (File.Exists(p)) File.Delete(p); } catch { } }
        }

        private static BucketMapperModel ResolveS3Map(string csvPath, string jobName)
        {
            var rows = FileHelper.ReadFile(csvPath);
            var maps = rows.Select(BucketMapperModel.FromCsv).ToList();

            var map = maps.FirstOrDefault(b =>
                b.job_type.Equals("inbound", StringComparison.OrdinalIgnoreCase) &&
                b.job_name.Equals(jobName, StringComparison.OrdinalIgnoreCase))
                ?? maps.FirstOrDefault(b => b.job_type.Equals("inbound", StringComparison.OrdinalIgnoreCase));

            if (map == null) throw new InvalidOperationException("Preflight: No inbound S3 mapping found.");
            if (string.IsNullOrWhiteSpace(map.bucket_name) || string.IsNullOrWhiteSpace(map.prefix))
                throw new InvalidOperationException("Preflight: S3 mapping with empty bucket_name/prefix.");

            return map;
        }

        private static void ValidateAwsKeyDecrypt(string keyLocker)
        {
            var locker = File.ReadAllText(keyLocker).Trim();
            var awsKeyBase64 = ConfigurationManager.AppSettings["AwsKey"];
            if (string.IsNullOrWhiteSpace(awsKeyBase64))
                throw new InvalidOperationException("Preflight: AwsKey appSetting is empty.");

            try
            {
                var dec = new CryptoHelper(locker).Decrypt(awsKeyBase64);
                var parts = dec.Split(new[] { "::" }, StringSplitOptions.None);
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                    throw new InvalidOperationException("Preflight: AwsKey decrypted but not in 'access::secret' format.");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Preflight: Failed to decrypt AwsKey with key.txt.", ex);
            }
        }

        private static string ResolveDbMain(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Preflight: DBConnectionMain not found.");

            var lines = FileHelper.ReadFile(path);
            if (lines.Count == 0) throw new InvalidOperationException("Preflight: DBConnectionMain empty.");

            if (lines[0].Trim().StartsWith("area_code", StringComparison.OrdinalIgnoreCase))
            {
                if (lines.Count < 2) throw new InvalidOperationException("Preflight: DBConnectionMain CSV header without rows.");
                var first = lines[1];
                var parts = first.Split(new[] { ',' }, 3);
                if (parts.Length < 3) throw new InvalidOperationException("Preflight: DBConnectionMain CSV malformed.");
                var conn = parts[2].Trim();
                if (string.IsNullOrWhiteSpace(conn)) throw new InvalidOperationException("Preflight: DBConnectionMain CSV db_connection empty.");
                return conn;
            }
            else
            {
                var conn = lines[0].Trim();
                if (string.IsNullOrWhiteSpace(conn)) throw new InvalidOperationException("Preflight: DBConnectionMain plain string empty.");
                return conn;
            }
        }

        private static string ResolveDbUser(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new InvalidOperationException("Preflight: DBConnection file not found.");

            var lines = FileHelper.ReadFile(path);
            var first = lines.Count > 0 ? lines[0].Trim() : null;
            if (string.IsNullOrWhiteSpace(first)) throw new InvalidOperationException("Preflight: DBConnection empty.");
            return first;
        }

        private static string ReadSqlIfExists(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            if (!File.Exists(p)) return null;
            return File.ReadAllText(p);
        }

        private static void ValidateSqlScript(string sqlText, string tag)
        {
            if (string.IsNullOrWhiteSpace(sqlText)) return;

            var min = Regex.Replace(sqlText, @"--.*?$", "", RegexOptions.Multiline);
            min = Regex.Replace(min, @"/\*.*?\*/", "", RegexOptions.Singleline);

            var rxPrintSelect = new Regex(@"PRINT\s*\([^)]*SELECT[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var rxConcatSelect = new Regex(@"CONCAT\s*\([^)]*SELECT[^)]*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var rxStringPlusSelect = new Regex(@"'[^']*'\s*\+\s*\(\s*SELECT", RegexOptions.IgnoreCase);

            if (rxPrintSelect.IsMatch(min) || rxConcatSelect.IsMatch(min) || rxStringPlusSelect.IsMatch(min))
            {
                throw new InvalidOperationException(
                    "Preflight SQL validation failed for " + tag + ". " +
                    "Avoid SELECT-subqueries inside PRINT/CONCAT or string concatenations. " +
                    "Use variables or return a result set instead."
                );
            }
        }
    }
}
