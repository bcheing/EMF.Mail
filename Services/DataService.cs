using Cheing;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;

namespace EMF.MailSvc.Services
{
    public class DataService(DbBinder db)
    {
        public Task<Result<MessageResult>> SaveMessageAsync(MailMessage msg) => db.PutObjAsync<MessageResult>(new { HndName = "/filer/msg/message", msg });
        public Task<Result> SaveMessageItemsAsync(List<MessageItem> items) => db.PutAsync(new { HndName = "/filer/msg/items", items });
        public Task<Result> SaveMsgTypeAsync(int msgNo, string msgTpCode) => db.PutAsync(new { HndName = "/filer/msg/msgtype", msgNo, msgTpCode });

        public Task<List<MailAccount>> GetMailAccountsAsync() => db.GetTListAsync<MailAccount>("/msg/mail/accounts");
        public Task<List<SenderHistory>> GetSenderHistoryAsync(int senderId) => db.GetTListAsync<SenderHistory>("/msg/mail/sender", senderId);
        public Task<List<ReqStatus>> GetReqStatusAsync(int senderId, string invcNbr) => db.GetTListAsync<ReqStatus>("/msg/mail/requests", senderId, invcNbr);
    }
}
