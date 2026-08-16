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

        // Single write-back point for a message's terminal state -- MsgContext, MsgTpId, IsHeld/FwdMsgId,
        // ResTpId/MsgResult, and IsProcessed all live on the same msg.TblMessages row and are only ever
        // known once, at the point a message's processing pass actually ends. Any param left null leaves
        // that column untouched, so a caller only supplies what changed at its particular exit point.
        public Task<Result> FinalizeMessageAsync(int msgNo, TriageResult? triageResult, string? msgTpCode, bool? isHeld, string? fwdMsgId, string resTpCode, string? msgResult) =>
            db.PutAsync(new { HndName = "/filer/msg/finalize", msgNo, msgContext = triageResult, msgTpCode, isHeld, fwdMsgId, resTpCode, msgResult });

        // Approve/reject in one transactional call -- releases the held batch and logs the command against
        // the admin's own message row together (both msg schema), instead of two round trips.
        public Task<Result> ResolveCommandAsync(int senderId, int vendId, int anchorMsgNo, bool isApproved, string cmdCode, int adminMsgNo, string? reference, int resultCode, string? resultMsg) =>
            db.PutAsync(new { HndName = "/filer/msg/resolvecmd", senderId, vendId, anchorMsgNo, isApproved, cmdCode, adminMsgNo, reference, resultCode, resultMsg });

        public Task<Result> LinkVendorAsync(int senderId, int appId, int vendId) => db.PutAsync(new { HndName = "/ap/vend/sender", senderId, appId, vendId });
        public Task<Result> SetLastPollAsync(int acctId, DateTime lastPollDt) => db.PutAsync(new { HndName = "/filer/msg/lastpoll", acctId, lastPollDt });
        public Task<List<MailAccount>> GetMailAccountsAsync() => db.GetTListAsync<MailAccount>("/msg/mail/accounts");
        public Task<List<SenderHistory>> GetSenderHistoryAsync(string hndName, int senderId, int appId) => db.GetTListAsync<SenderHistory>(hndName, senderId, appId);
        public Task<List<ReqStatus>> GetReqStatusAsync(int senderId, string invcNbr) => db.GetTListAsync<ReqStatus>("/msg/mail/requests", senderId, invcNbr);

        // Takes the whole ordered candidate id list (In-Reply-To + References, per GraphMailService.GetBridgeMsgIds)
        // in one call -- SQL picks the first match in candidate order instead of C# looping call-by-call.
        public Task<List<HeldMessage>> GetHeldBridgeAsync(List<string> candidateIds) => db.GetTListAsync<HeldMessage>("/msg/mail/heldbridge", candidateIds);

        // Scoped to one hold cycle (anchorMsgNo = the MsgNo whose classification triggered the admin
        // question) and to the vendor being decided -- not a blanket sweep of every held message for the
        // sender, since a sender can have more than one vendor-link decision in flight at once. vendId is
        // nullable -- a first-contact sender's own hold has no resolved VendId yet, and the SQL side matches
        // those rows regardless of what's passed here, so the dedup check still works for unresolved senders.
        public Task<List<HeldMessage>> GetHeldMessagesAsync(int senderId, int? vendId, int anchorMsgNo) => db.GetTListAsync<HeldMessage>("/msg/mail/heldbysender", senderId, vendId, anchorMsgNo);
        public Task<List<VendorMatch>> LookupVendorAsync(string nameFragment) => db.GetTListAsync<VendorMatch>("/ap/lookups/vendid", nameFragment);
    }
}
