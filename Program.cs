using iSMSOverdue.InboundInvoice.Helper;
using iSMSOverdue.InboundInvoice.Model;
using iSMSOverdue.InboundInvoice.Service.Inbound;
using NLog;
using System;
using System.Threading.Tasks;

namespace iSMSOverdue.InboundInvoice
{
    internal class Program
    {
        private static readonly Logger _log = LogManager.GetCurrentClassLogger();

        private static async Task Main(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                _log.Error("argument command not found. Expected: importcsv --jobname=overdue-inbound_invoice ...");
                Environment.Exit(1);
            }

            if (!string.Equals(args[0], "importcsv", StringComparison.OrdinalIgnoreCase))
            {
                _log.Error($"Unsupported command '{args[0]}'. This executable only supports: importcsv");
                Environment.Exit(2);
            }

            ArgumentModel cfg = ArgumentHelper.Get(args);
            cfg.JobName = "overdue-inbound_invoice";  // forced just like BV1 forces its jobname

            if (!string.IsNullOrWhiteSpace(cfg.Logger) && System.IO.File.Exists(cfg.Logger))
                NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(cfg.Logger);

            string sessionId = Guid.NewGuid().ToString("N");

            try
            {
                IServiceInboundJob inbound = new ServiceInboundJob();
                await inbound.ExecJob(cfg, sessionId);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Overdue Invoice inbound job failed");
                Environment.Exit(10);
            }
        }
    }
}