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
        public Dictionary<string, string> DbMain;
        public List<string> DbUser;
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
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

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

        private static Dictionary<string, string> ResolveDbMain(string path)
        {
            var lines = File.ReadAllLines(path);

            if (lines.Length < 2)
                throw new InvalidOperationException("DBConnectionMain must contain header and at least one row.");

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Skip header
            foreach (var line in lines.Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split(new[] { ',' }, 3);

                if (parts.Length < 3)
                    throw new InvalidOperationException("Invalid DBConnectionMain format.");

                var area = parts[0].Trim();
                var conn = parts[2].Trim();

                if (!string.IsNullOrEmpty(area) && !string.IsNullOrEmpty(conn))
                {
                    dict[area] = conn;
                }
            }

            if (dict.Count == 0)
                throw new InvalidOperationException("No valid DBConnectionMain entries.");

            return dict;
        }

        private static List<string> ResolveDbUser(string path)
        {
            var lines = File.ReadAllLines(path)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            if (lines.Count == 0)
                throw new InvalidOperationException("DBConnection must contain at least one connection.");

            return lines;
        }

        private static (List<string>, bool) ValidatePathsAndPermissions(ArgumentModel cfg, Logger log)
        {
            var missing = new List<string>();

            var requiredFiles = new[]
            {
                cfg.DBConnection,
                cfg.DBConnectionMain,
                cfg.FileKeyLocker,
                cfg.FileUpload,
                cfg.S3Bucket,
                cfg.Logger
            };

            foreach (var f in requiredFiles)
            {
                if (string.IsNullOrWhiteSpace(f) || !File.Exists(f))
                    missing.Add(f ?? "(null)");
            }

            if (missing.Count > 0)
                throw new InvalidOperationException(
                    "Missing files:\n - " + string.Join("\n - ", missing));

            if (string.IsNullOrWhiteSpace(cfg.CsvInbound))
                throw new InvalidOperationException("CsvInbound is empty.");

            if (!Directory.Exists(cfg.CsvInbound))
                Directory.CreateDirectory(cfg.CsvInbound);

            var uploadsRaw = FileHelper.ReadFile(cfg.FileUpload);

            var uploadPaths = uploadsRaw
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .ToList();

            var hasUploads = uploadPaths.Count > 0;

            if (hasUploads)
            {
                foreach (var path in uploadPaths)
                {
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);

                    WriteTest(path);
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
            var file = Path.Combine(dir, $"_test_{Guid.NewGuid():N}.tmp");

            File.WriteAllText(file, "ok");

            try { File.Delete(file); } catch { }
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

        private static string ReadSqlIfExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            return File.ReadAllText(path);
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
