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

        // DB Reads: DMS Mail page
        public Task<(AccountBookmark?, List<MessageEntry>)> GetBookmarkAndMessageEntriesAsync(int acctId, int? maxMsgNo, List<int> msgNos) => db.GetObjAndTListAsync<AccountBookmark, MessageEntry>("/msg/mail/bookmark", "/msg/mail/messages", acctId, maxMsgNo, msgNos);
        public Task<List<int>> GetReadMsgNosAsync(List<int> msgNos) => db.GetScalarListAsync<int>("/msg/mail/readitems", msgNos);

        // DB Writes
        public Task<Result<MessageResult>> SaveMessageAsync(Message msg) => db.PutObjAsync<MessageResult>(new { HndName = "/filer/msg/message", msg });
        public Task<Result> SaveMessageItemsAsync(List<MessageItem> items) => db.PutAsync(new { HndName = "/filer/msg/msgitems", items });
        public Task<Result> SaveMessageFinalizationAsync(MessageFinalization msg) => db.PutAsync(new { HndName = "/filer/msg/finalize", msg }); // single write-back point for a message's terminal state; null fields leave those columns untouched
        public Task<Result> SaveMessageContextAsync(int msgNo, object msgContext) => db.PutAsync(new { HndName = "/filer/msg/context", msg = new { MsgNo = msgNo, MsgContext = msgContext } }); // MsgContext-only update for a message that stays held
        public Task<Result> SaveMessageResolutionAsync(ApprResolution cmd) => db.PutAsync(new { HndName = "/filer/msg/resolvecmd", cmd }); // approve/reject in one transactional call
        public Task<Result> SaveMessageBookmarkAsync(AccountBookmark acct) => db.PutAsync(new { HndName = "/filer/msg/lastpoll", acct });
        public Task<Result<int>> SaveApprRequestAsync(dynamic req) => db.PutObjAsync<int>(new { HndName = "/msg/appr/request", req });
        public Task<Result<InfoRequestOpenResult>> OpenInfoRequestAsync(InfoRequest rfi) => db.PutObjAsync<InfoRequestOpenResult>(new { HndName = "/msg/req/open", rfi });
        public Task<Result> ResendInfoRequestAsync(int iReqNo, string sentMsgId) => db.PutAsync(new { HndName = "/msg/req/resend", IReqNo = iReqNo, SentMsgId = sentMsgId }); // for future use
        public Task<Result> CloseInfoRequestAsync(int iReqNo) => db.PutAsync(new { HndName = "/msg/req/close", IReqNo = iReqNo });

        // DB Writes: DMS Mail page
        public Task<Result> SaveReadItemAsync(int msgNo) => db.PutAsync(new { HndName = "/msg/mail/readitems", item = new { MsgNo = msgNo } });
    }
}
