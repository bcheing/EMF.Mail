using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    // Provider-agnostic mail operations MessageProcessor actually calls, implemented by GraphMailService and
    // ImapMailService -- a future provider only needs to implement this surface, without MessageProcessor
    // changing at all.
    public interface IMailService
    {
        Task<(List<InternetMessage> Messages, string DeltaLink)> GetChangedMessagesAsync(string? lastMsgLink);
        Task<InternetMessage?> GetMessageByIdAsync(string internetMessageId);
        Task ReplyAsync(InternetMessage message, string comment);
        Task ForwardAsync(InternetMessage message, string toAddr, string comment);
        Task<string?> SendApprovalRequestAsync(InternetMessage message, string toAddr, string comment);
        Task<string?> SendInfoRequestAsync(InternetMessage message, string comment);
    }
}
