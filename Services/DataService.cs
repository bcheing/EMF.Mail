using Cheing;
using EMF.DMS.Client;
using EMF.Mail.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using DbBinder = Cheing.Binder;

namespace EMF.Mail.Services
{
    public class DataService(DbBinder db)
    {
        // DB Reads
        public Task<List<Account>> GetMailAccountsAsync() => db.GetTListAsync<Account>("/msg/mail/accounts");
        public Task<List<SenderHistory>> GetSenderHistoryAsync(string hndName, int senderId, int appId) => db.GetTListAsync<SenderHistory>(hndName, senderId, appId);
        public Task<InfoRequestBridge> GetRfiBridgeAsync(List<string> candidateIds) => db.GetObjAsync<InfoRequestBridge>("/msg/mail/rfibridge", candidateIds); // same bridging as GetHeldBridgeAsync, matched against SentMsgId instead of FwdMsgId
        public Task<ApprRequest> GetApprRequestAsync(List<string> candidateIds) => db.GetObjAsync<ApprRequest>("/msg/mail/apprrequest", candidateIds);
        public Task<ApprRequest> GetOpenRequestAsync(int senderId, int partyTpId) => db.GetObjAsync<ApprRequest>("/msg/mail/senderholds", senderId, partyTpId);
        public Task<List<ApprRequestItem>> GetApprRequestItemsAsync(int reqNo) => db.GetTListAsync<ApprRequestItem>("/msg/mail/apprrequestitems", reqNo);
        public Task<List<int?>> GetSenderPartyIdsAsync(int senderId, int partyTpId) => db.GetScalarListAsync<int?>("/msg/mail/permissions", senderId, partyTpId);
        public Task<List<PkgTask>> GetPkgTasksAsync(string hndName, List<int> pkgNos) => db.GetTListAsync<PkgTask>(hndName, pkgNos); // gap-check covers every PkgNo in one call; caller filters IsComplete
        public Task<List<Dictionary<string, object>>> GetRecordsAsync(string hndName, params object[] parameters) => db.GetDictListAsync(hndName, parameters);

        // DB Reads: download and processing queue
        public Task<AccountBookmark> GetAccountBookmarkAsync(int acctId) => db.GetObjAsync<AccountBookmark>("/msg/mail/bookmark", acctId);
        public Task<List<QueuedMessage>> GetQueuedMessagesAsync(string hndName, int acctId) => db.GetTListAsync<QueuedMessage>(hndName, acctId);
        public Task<MessageContent> GetMessageContentAsync(int msgNo) => db.GetObjAsync<MessageContent>("/msg/messages/content", msgNo);

        // DB Reads: DMS Mail page
        public Task<(AccountBookmark?, List<MessageEntry>)> GetBookmarkAndMessageEntriesAsync(int acctId, int? maxMsgNo, List<int> msgNos) => db.GetObjAndTListAsync<AccountBookmark, MessageEntry>("/msg/mail/bookmark", "/msg/messages/list", acctId, maxMsgNo, msgNos);
        public Task<List<int>> GetReadMsgNosAsync(List<int> msgNos) => db.GetScalarListAsync<int>("/msg/messages/readitems", msgNos);

        // DB Writes
        public Task<Result<MessageResult>> SaveMessageAsync(Message msg) => db.PutObjAsync<MessageResult>(new { HndName = "/msg/messages/save", msg });
        public Task<Result> SaveMessageItemsAsync(List<MessageItem> items) => db.PutAsync(new { HndName = "/msg/messages/items", items });
        public Task<Result> SaveMessageFinalizationAsync(MessageFinalization msg) => db.PutAsync(new { HndName = "/msg/messages/finalize", msg }); // single write-back point for a message's terminal state; null fields leave those columns untouched
        public Task<Result> SaveMessageContextAsync(int msgNo, object msgContext) => db.PutAsync(new { HndName = "/msg/messages/context", msg = new { MsgNo = msgNo, MsgContext = msgContext } }); // MsgContext-only update for a message that stays held
        public Task<Result> SaveMessageResolutionAsync(ApprResolution cmd) => db.PutAsync(new { HndName = "/msg/appr/resolve", cmd }); // approve/reject in one transactional call
        public Task<Result<int>> SaveApprRequestAsync(dynamic req) => db.PutObjAsync<int>(new { HndName = "/msg/appr/request", req });
        public Task<Result<InfoRequestOpenResult>> OpenInfoRequestAsync(InfoRequest rfi) => db.PutObjAsync<InfoRequestOpenResult>(new { HndName = "/msg/req/open", rfi });
        public Task<Result> ResendInfoRequestAsync(int iReqNo, string sentMsgId) => db.PutAsync(new { HndName = "/msg/req/resend", IReqNo = iReqNo, SentMsgId = sentMsgId }); // for future use
        public Task<Result> CloseInfoRequestAsync(int iReqNo) => db.PutAsync(new { HndName = "/msg/req/close", IReqNo = iReqNo });

        // DB Writes: jobs and processing queue
        public Task<Result<JobResult>> StartJobAsync(int acctId) => db.PutObjAsync<JobResult>(new { HndName = "/msg/job/start", job = new { AcctId = acctId } });
        public Task<Result> EndJobAsync(int jobNo, int numMessages, string? lastMsgLink) => db.PutAsync(new { HndName = "/msg/job/end", job = new { JobNo = jobNo, NumMessages = numMessages, LastMsgLink = lastMsgLink } }); // a null link leaves the bookmark where it is
        public Task<Result> SaveMessageClaimAsync(int msgNo) => db.PutAsync(new { HndName = "/msg/messages/claim", msg = new { MsgNo = msgNo } });
        public Task<Result> SaveMessageContentAsync(int msgNo, string msgContent) => db.PutAsync(new { HndName = "/msg/messages/content", msg = new { MsgNo = msgNo, MsgContent = msgContent } });

        // DB Writes: DMS Mail page
        public Task<Result> SaveReadItemAsync(int msgNo) => db.PutAsync(new { HndName = "/msg/messages/readitems", item = new { MsgNo = msgNo } });
    }
}
