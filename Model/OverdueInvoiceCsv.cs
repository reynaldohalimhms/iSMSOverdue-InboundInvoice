namespace iSMSOverdue.InboundInvoice.Model
{
    public class OverdueInvoiceCsv
    {
        public string invoice_legal_number { get; set; }
        public string status { get; set; }
        public string csv_filename { get; set; }
        public long file_sequence { get; set; }
    }
}
