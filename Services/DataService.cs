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
        // DB Reads
        public Task<List<MailAccount>> GetMailAccountsAsync() => db.GetTListAsync<MailAccount>("/msg/mail/accounts");
        public Task<List<SenderHistory>> GetSenderHistoryAsync(string hndName, int senderId, int appId) => db.GetTListAsync<SenderHistory>(hndName, senderId, appId);
        public Task<List<ReqStatus>> GetReqStatusAsync(int senderId, string invcNbr) => db.GetTListAsync<ReqStatus>("/msg/mail/requests", senderId, invcNbr);
        public Task<List<HeldMessage>> GetHeldBridgeAsync(List<string> candidateIds) => db.GetTListAsync<HeldMessage>("/msg/mail/heldbridge", candidateIds); // ordered candidate ids (In-Reply-To + References) -- SQL picks the first match, not a C# loop
        public Task<List<HeldMessage>> GetHeldMessagesAsync(int senderId, int? vendId, int anchorMsgNo) => db.GetTListAsync<HeldMessage>("/msg/mail/heldbysender", senderId, vendId, anchorMsgNo); // scoped to one hold cycle + one vendor; vendId nullable, SQL matches unresolved rows regardless
        public Task<List<VendorMatch>> LookupVendorAsync(string nameFragment) => db.GetTListAsync<VendorMatch>("/ap/lookups/vendid", nameFragment);
        public Task<List<PkgTask>> GetPkgTasksAsync(List<int> pkgNos) => db.GetTListAsync<PkgTask>("/ap/pkg/tasks", pkgNos); // gap-check covers every PkgNo in one call; caller filters IsComplete
        public Task<List<RfiBridgeResult>> GetRfiBridgeAsync(List<string> candidateIds) => db.GetTListAsync<RfiBridgeResult>("/msg/mail/rfibridge", candidateIds); // same bridging as GetHeldBridgeAsync, matched against SentMsgId instead of FwdMsgId

        // DB Writes
        public Task<Result<MessageResult>> SaveMessageAsync(MailMessage msg) => db.PutObjAsync<MessageResult>(new { HndName = "/filer/msg/message", msg });
        public Task<Result> SaveMessageItemsAsync(List<MessageItem> items) => db.PutAsync(new { HndName = "/filer/msg/msgitems", items });
        public Task<Result> FinalizeMessageAsync(MessageFinalize msg) => db.PutAsync(new { HndName = "/filer/msg/finalize", msg }); // single write-back point for a message's terminal state; null fields leave those columns untouched
        public Task<Result> ResolveCommandAsync(CommandResolve cmd) => db.PutAsync(new { HndName = "/filer/msg/resolvecmd", cmd }); // approve/reject in one transactional call
        public Task<Result> LinkVendorAsync(int senderId, int appId, int vendId) => db.PutAsync(new { HndName = "/ap/vend/sender", senderId, appId, vendId });
        public Task<Result> SetLastPollAsync(AccountPoll acct) => db.PutAsync(new { HndName = "/filer/msg/lastpoll", acct });
        public Task<Result<InfoRequestOpenResult>> OpenInfoRequestAsync(InfoRequest rfi) => db.PutObjAsync<InfoRequestOpenResult>(new { HndName = "/msg/req/open", rfi });
        public Task<Result> ResendInfoRequestAsync(int iReqNo, string sentMsgId) => db.PutAsync(new { HndName = "/msg/req/resend", IReqNo = iReqNo, SentMsgId = sentMsgId }); // for future use
        public Task<Result> CloseInfoRequestAsync(int iReqNo) => db.PutAsync(new { HndName = "/msg/req/close", IReqNo = iReqNo });
        public Task<Result> LinkReplyAsync(int msgNo, int iReqNo) => db.PutAsync(new { HndName = "/filer/msg/linkreply", MsgNo = msgNo, IReqNo = iReqNo }); // correlation only, not terminal state
    }
}