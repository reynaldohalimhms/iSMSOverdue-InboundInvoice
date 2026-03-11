namespace iSMSOverdue.InboundInvoice.Model
{
    public class InvoiceStatusCsv
    {
        public string invoice_legal_number { get; set; }
        public string status { get; set; }  // may be empty
    }
}