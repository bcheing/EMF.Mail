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
        Task<List<InternetMessageHeader>> GetNewMessageHeadersAsync(string lastMsgLink);
        [System.Obsolete("Processing status is tracked in msg.TblMessages and shown on the DMS Mail page; the mailbox is no longer flagged or moved.")]
        Task FlagProcessedAsync(string provMsgId);
        [System.Obsolete("Processing status is tracked in msg.TblMessages and shown on the DMS Mail page; the mailbox is no longer flagged or moved.")]
        Task MarkNeedsReviewAsync(string provMsgId);
        Task ReplyAsync(string provMsgId, string comment);
        Task ForwardAsync(string provMsgId, string toAddr, string comment);
        Task<string?> SendApprovalRequestAsync(string provMsgId, string toAddr, string comment);
        Task<string?> SendInfoRequestAsync(string provMsgId, string comment);
    }
}
