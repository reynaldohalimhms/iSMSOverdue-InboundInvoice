using iSMSOverdue.InboundInvoice.Model;
using System.Threading.Tasks;

namespace iSMSOverdue.InboundInvoice.Service.Inbound
{
    public interface IServiceInboundJob
    {
        Task ExecJob(ArgumentModel cfg, string sessionId);
    }
}