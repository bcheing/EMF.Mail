using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EMF.Mail.Services
{
    // Provider-agnostic mail operations MessageProcessor actually calls. GraphMailService is the only
    // implementation today -- this exists so a future provider only needs to implement this surface,
    // without MessageProcessor changing at all.
    public interface IMailService
    {
        Task<(List<Message> Messages, string DeltaLink)> GetChangedMessagesAsync(string? lastMsgLink);
        Task<Message?> GetMessageByIdAsync(string internetMessageId);
        Task FlagProcessedAsync(string provMsgId);
        Task MarkNeedsReviewAsync(string provMsgId);
        Task ReplyAsync(string provMsgId, string comment);
        Task ForwardAsync(string provMsgId, string toAddr, string comment);
        Task<string?> SendApprovalRequestAsync(string provMsgId, string toAddr, string comment);
        Task<string?> SendInfoRequestAsync(string provMsgId, string comment);
    }
}
