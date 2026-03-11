using System;

namespace iSMSOverdue.InboundInvoice.Model
{
    public class ArgumentModel
    {
        public string DBConnectionMain { get; set; } = string.Empty;
        public string DBConnection { get; set; } = string.Empty;
        public string DBConnectionMail { get; set; } = string.Empty;

        public string FileQueryMain { get; set; } = string.Empty;
        public string FileQuery { get; set; } = string.Empty;

        public string FileKeyLocker { get; set; } = string.Empty;
        public string FileUpload { get; set; } = string.Empty;
        public string CsvInbound { get; set; } = string.Empty;

        public string S3Bucket { get; set; } = string.Empty;
        public string Logger { get; set; } = string.Empty;
        public string JobName { get; set; } = string.Empty;

        public string EmailBody { get; set; } = string.Empty;
        public string EmailExecute { get; set; } = string.Empty;
        public string EmailTo { get; set; } = string.Empty;
        public bool IsSendEmail { get; set; }

        public string LogData { get; set; } = string.Empty;

        // Unused in MODE B but kept for structural compatibility
        public bool UploadS3 { get; set; }
        public string CsvOutput { get; set; } = string.Empty;
        public string FileBackup { get; set; } = string.Empty;
        public bool IsSingleFile { get; set; }
        public bool IsConvertCsv { get; set; }

        public string Delimiter { get; set; } = ",";
    }
}