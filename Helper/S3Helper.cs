using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using iSMSOverdue.InboundInvoice.Model;
using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class S3Helper
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        private static Tuple<string, string, string> GetCreds(string keyLockerPath)
        {
            // key locker contains the AES key to decrypt AwsKey (App.config)
            string keyLocker;
            using (var fs = new FileStream(keyLockerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) keyLocker = sr.ReadToEnd().Trim();

            var awsKeyEnc = ConfigurationManager.AppSettings["AwsKey"];
            var region = ConfigurationManager.AppSettings["AwsRegion"];

            var dec = new CryptoHelper(keyLocker).Decrypt(awsKeyEnc);
            var parts = dec.Split(new[] { "::" }, StringSplitOptions.None);
            if (parts.Length < 2) throw new InvalidOperationException("AwsKey decrypted but not in 'access::secret' format.");

            return Tuple.Create(parts[0], parts[1], region);
        }

        public static async Task<List<S3DownloadResult>> Download(BucketMapperModel bucket, string keyLockerPath, string destFolder)
        {
            var results = new List<S3DownloadResult>();
            try
            {
                var creds = GetCreds(keyLockerPath);
                var access = creds.Item1; var secret = creds.Item2; var region = creds.Item3;

                var cfg = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };
                using (var client = new AmazonS3Client(access, secret, cfg))
                using (var xfer = new TransferUtility(client))
                {
                    if (!Directory.Exists(destFolder)) Directory.CreateDirectory(destFolder);

                    var req = new ListObjectsV2Request { BucketName = bucket.bucket_name, Prefix = bucket.prefix, MaxKeys = 1000 };
                    ListObjectsV2Response resp;
                    do
                    {
                        resp = await client.ListObjectsV2Async(req);
                        if (resp == null || resp.S3Objects == null) break;

                        var sem = new SemaphoreSlim(8); // mild parallelism
                        var tasks = new List<Task>();

                        foreach (var obj in resp.S3Objects)
                        {
                            if (obj == null || string.IsNullOrEmpty(obj.Key)) continue;

                            var fileName = Path.GetFileName(obj.Key);
                            if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var localPath = Path.Combine(destFolder, fileName);
                            tasks.Add(Task.Run(async () =>
                            {
                                try
                                {
                                    await sem.WaitAsync();
                                    if (!File.Exists(localPath))
                                    {
                                        var dreq = new TransferUtilityDownloadRequest
                                        {
                                            BucketName = obj.BucketName,
                                            Key = obj.Key,
                                            FilePath = localPath
                                        };
                                        await xfer.DownloadAsync(dreq);
                                        _log.Info($"✅ Download file from S3: {fileName}");
                                    }
                                    results.Add(new S3DownloadResult { status = true, filename = fileName });
                                }
                                catch (Exception ex)
                                {
                                    _log.Error($"⛔ Download from S3 {fileName}: {ex.Message}");
                                    results.Add(new S3DownloadResult { status = false, filename = fileName, message = ex.Message });
                                }
                                finally
                                {
                                    try { sem.Release(); } catch { }
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);

                        req.ContinuationToken = resp.NextContinuationToken;
                    }
                    while (resp != null && resp.IsTruncated);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"⛔ Access S3: {ex.Message}");
            }
            finally
            {
                results.RemoveAll(x => x == null);
            }
            return results;
        }

        /// <summary>
        /// Delete objects under the specified prefix (or a single object when prefix includes a filename).
        /// Mirrors BV1 bulk delete behavior.
        /// </summary>
        public static async Task<List<S3DownloadResult>> Delete(BucketMapperModel bucket, string keyLockerPath)
        {
            var results = new List<S3DownloadResult>();
            try
            {
                var creds = GetCreds(keyLockerPath);
                var access = creds.Item1; var secret = creds.Item2; var region = creds.Item3;
                var cfg = new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) };

                using (var client = new AmazonS3Client(access, secret, cfg))
                {
                    var req = new ListObjectsV2Request { BucketName = bucket.bucket_name, Prefix = bucket.prefix, MaxKeys = 1000 };
                    ListObjectsV2Response resp;
                    do
                    {
                        resp = await client.ListObjectsV2Async(req);
                        if (resp == null || resp.S3Objects == null) break;

                        foreach (var entry in resp.S3Objects)
                        {
                            if (entry == null || string.IsNullOrEmpty(entry.Key)) continue;
                            try
                            {
                                await client.DeleteObjectAsync(new DeleteObjectRequest
                                {
                                    BucketName = entry.BucketName,
                                    Key = entry.Key
                                });
                                _log.Info($"Delete file S3 {entry.Key}");
                            }
                            catch (Exception ex)
                            {
                                _log.Error($"Delete file S3 {entry.BucketName} {ex.Message}");
                            }
                        }

                        req.ContinuationToken = resp.NextContinuationToken;
                    }
                    while (resp != null && resp.IsTruncated);
                }
            }
            catch (Exception ex)
            {
                _log.Error($"Error Access S3: {ex.Message}");
            }
            return results;
        }
    }
}