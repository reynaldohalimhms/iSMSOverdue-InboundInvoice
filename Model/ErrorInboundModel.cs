using System;

namespace iSMSOverdue.InboundInvoice.Model
{
    public class ErrorInboundModel
    {
        public string ServerName { get; set; }
        public string AreaCode { get; set; }
        public string FileName { get; set; }
        public DateTime ErrorDate { get; set; }
        public string Message { get; set; }
    }
}