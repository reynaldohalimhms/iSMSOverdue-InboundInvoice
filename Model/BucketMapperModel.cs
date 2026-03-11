namespace iSMSOverdue.InboundInvoice.Model
{
    public class BucketMapperModel
    {
        public string job_name { get; set; }
        public string bucket_name { get; set; }
        public string prefix { get; set; }
        public bool update_only { get; set; }
        public string job_type { get; set; }

        public static BucketMapperModel FromCsv(string csv)
        {
            var v = csv.Split(';');
            return new BucketMapperModel
            {
                job_name = v[0].Trim(),
                bucket_name = v[1].Trim(),
                prefix = v[2].Trim(),
                update_only = v[3].Trim() == "1",
                job_type = v[4].Trim()
            };
        }
    }

    public class BucketUploadMapperModel
    {
        public string job_name { get; set; }
        public string file_upload { get; set; }
        public string file_backup { get; set; }
        public string bucket_name { get; set; }
        public string prefix { get; set; }
        public bool delete_s3file { get; set; }

        public static BucketUploadMapperModel FromCsv(string csv)
        {
            var v = csv.Split(';');
            return new BucketUploadMapperModel
            {
                job_name = v[0].Trim(),
                file_upload = v[1].Trim(),
                file_backup = v[2].Trim(),
                bucket_name = v[3].Trim(),
                prefix = v[4].Trim(),
                delete_s3file = v[5].Trim() == "1"
            };
        }
    }
}