using System;

namespace iSMSOverdue.InboundInvoice.Model
{
    public class LoggerModel
    {
        public string session_id { get; set; }
        public string servername { get; set; }
        public string jobname { get; set; }
        public string job_type { get; set; }
        public string filename { get; set; }
        public DateTime startjob_date { get; set; }
        public DateTime endjob_date { get; set; }
        public int total_record { get; set; }
        public string activity_msg { get; set; }
        public int sequence_number { get; set; }
        public string error_msg { get; set; }
    }
}
