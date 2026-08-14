using Cheing;
using EMF.Mail.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;

namespace EMF.Mail.Services
{
    public class DataService(DbBinder db)
    {
        public Task<Result<MessageResult>> SaveMessageAsync(MailMessage msg) => db.PutObjAsync<MessageResult>(new { HndName = "/filer/msg/message", msg });
        public Task<Result> SaveMessageItemsAsync(List<MessageItem> items) => db.PutAsync(new { HndName = "/filer/msg/msgitems", items });
        public Task<Result> SaveMsgTypeAsync(int msgNo, string msgTpCode) => db.PutAsync(new { HndName = "/filer/msg/msgtype", msgNo, msgTpCode });
        public Task<Result> SetHoldAsync(int msgNo, bool isHeld, string? fwdMsgId) => db.PutAsync(new { HndName = "/filer/msg/sethold", msgNo, isHeld, fwdMsgId });
        public Task<Result> ReleaseHoldAsync(int senderId, bool isApproved) => db.PutAsync(new { HndName = "/filer/msg/releasehold", senderId, isApproved });
        public Task<Result> SetLastPollAsync(int acctId, DateTime lastPollDt) => db.PutAsync(new { HndName = "/filer/msg/lastpoll", acctId, lastPollDt });
        public Task<List<MailAccount>> GetMailAccountsAsync() => db.GetTListAsync<MailAccount>("/msg/mail/accounts");
        public Task<List<SenderHistory>> GetSenderHistoryAsync(string hndName, int senderId) => db.GetTListAsync<SenderHistory>(hndName, senderId);
        public Task<List<ReqStatus>> GetReqStatusAsync(int senderId, string invcNbr) => db.GetTListAsync<ReqStatus>("/msg/mail/requests", senderId, invcNbr);
        public Task<List<HeldMessage>> GetHeldMessageAsync(string fwdMsgId) => db.GetTListAsync<HeldMessage>("/msg/mail/held", fwdMsgId);
        public Task<List<HeldMessage>> GetHeldMessagesAsync(int senderId) => db.GetTListAsync<HeldMessage>("/msg/mail/heldbysender", senderId);
    }
}
