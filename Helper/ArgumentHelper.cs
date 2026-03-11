using iSMSOverdue.InboundInvoice.Model;
using System;
using System.Linq;
using System.Text;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class ArgumentHelper
    {
        public static ArgumentModel Get(string[] args)
        {
            var cfg = new ArgumentModel();

            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
                if (i > 0) sb.Append(args[i] + " ");

            string parameters = sb.ToString();
            string[] tokens = parameters.Split(new[] { "--" }, StringSplitOptions.None);

            // Only "importcsv" is supported, mirroring BV1’s gated entry (Program.cs).
            if (args[0].Equals("importcsv", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var t in tokens)
                {
                    var kv = t.Split(new[] { '=' }, 2, StringSplitOptions.None);
                    if (kv.Length == 0) continue;

                    var key = kv[0].Trim().ToLowerInvariant();
                    var val = kv.Length > 1 ? kv[1].Trim() : string.Empty;

                    switch (key)
                    {
                        case "dbconnection_main": cfg.DBConnectionMain = val; break;
                        case "dbconnection_mail": cfg.DBConnectionMail = val; break;
                        case "dbconnection": cfg.DBConnection = val; break;
                        case "jobname": cfg.JobName = val; break;
                        case "csvinbound": cfg.CsvInbound = val; break;
                        case "filekey": cfg.FileKeyLocker = val; break;
                        case "s3bucket": cfg.S3Bucket = val; break;
                        case "logger": cfg.Logger = val; break;
                        case "filequery_main": cfg.FileQueryMain = val; break;
                        case "filequery": cfg.FileQuery = val; break;
                        case "fileupload": cfg.FileUpload = val; break;
                        case "emailbody": cfg.EmailBody = val; break;
                        case "emailexecute": cfg.EmailExecute = val; break;
                        case "issendemail": cfg.IsSendEmail = (val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase)); break;
                        case "emailto": cfg.EmailTo = val; break;
                        case "logdata": cfg.LogData = val; break;
                        case "delimiter": cfg.Delimiter = string.IsNullOrEmpty(val) ? "," : val; break;
                    }
                }
            }

            return cfg;
        }
    }
}