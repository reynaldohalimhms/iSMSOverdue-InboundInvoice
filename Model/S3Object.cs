namespace iSMSOverdue.InboundInvoice.Model
{
    public class S3ObjectFile
    {
        public string bucket { get; set; }
        public string filename { get; set; }
    }

    public class S3DownloadResult
    {
        public bool status { get; set; }
        public string filename { get; set; }
        public string message { get; set; }
        public string area { get; set; }
    }
}