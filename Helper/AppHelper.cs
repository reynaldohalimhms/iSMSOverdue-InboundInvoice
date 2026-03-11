using System;
using System.Configuration;

namespace iSMSOverdue.InboundInvoice.Helper
{
    public static class AppHelper
    {
        public static string GetComputerName()
        {
            try { return Environment.MachineName; } catch { return "UNKNOWN_HOST"; }
        }

        public static bool ActiveEmail()
        {
            try
            {
                var v = ConfigurationManager.AppSettings["ActiveEmail"];
                if (string.IsNullOrWhiteSpace(v)) return false;
                return v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}