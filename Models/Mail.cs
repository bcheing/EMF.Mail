using System;
using System.Collections.Generic;

namespace EMF.Mail.Models
{
    public class MailAccount
    {
        public int AcctId { get; set; }
        public int AppId { get; set; }
        public int OwnerUId { get; set; }
        public string AcctName { get; set; } = string.Empty;
        public string AdmAcctEMail { get; set; } = string.Empty;
        public string GetSenderHistHndName { get; set; } = string.Empty;
        public string ProvCode { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string SecretName { get; set; } = string.Empty;
        public DateTime LastPollDT { get; set; }
        public string? LastMsgLink { get; set; }
    }
    public class AppUser
    {
        public int UId { get; set; }
        public int? EntId { get; set; }
        public bool IsAdmin { get; set; }
    }

    // Provider-agnostic inbound message, mapped from whatever the underlying mail provider returns
    // (GraphMailService maps from Microsoft.Graph.Models.Message) so MessageProcessor never touches a
    // provider SDK type directly. ProvMsgId is the provider's own opaque message id (Graph's Id) --
    // used only to make further provider calls (reply/forward/flag/mark-review/send) against this same
    // message, never persisted. InternetMessageId is the RFC5322 id that IS persisted (msg.TblMessages.MsgId).
    // OrigMsgId/BridgeMsgIds are computed by the provider from its own header mechanics at mapping time,
    // so no raw header list needs to flow through MessageProcessor either.
    public class Message
    {
        public string ProvMsgId { get; set; } = string.Empty;
        public string? InternetMessageId { get; set; }
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public DateTime ReceivedDateTime { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
        public string UniqueBody { get; set; } = string.Empty;
        public List<AttachmentContent> Attachments { get; set; } = [];
        public string? OrigMsgId { get; set; }
        public List<string> BridgeMsgIds { get; set; } = [];
    }

    public class MailMessage
    {
        public int AcctId { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public string FromAddr { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public DateTime RcptDate { get; set; }
        public string Subject { get; set; } = string.Empty;
        public string? OrigMsgId { get; set; }
        public TriageResult? MsgContext { get; set; }
    }
    public class MessageItem
    {
        public int MsgNo { get; set; }
        public int PkgNo { get; set; }
        public int DocNo { get; set; }
    }
    public class MessageResult { public int MsgNo { get; set; } public int SenderId { get; set; } }

    // Result of /msg/mail/held -- resolves an admin reply back to the message it was forwarded from,
    // via References[0]/In-Reply-To matched against the stored FwdMsgId. CandVendId/CandVendName are
    // the candidate Claude proposed at hold time (from MsgContext) -- reused on a bare confirmation
    // reply instead of re-resolving the vendor name from scratch. MsgContext is the raw persisted json
    // (populated only by /msg/mail/heldbysender) -- deserializes to TriageResult to reuse a message's
    // original classification on approval, without a second Claude call.
    public class HeldMessage
    {
        public int MsgNo { get; set; }
        public int SenderId { get; set; }
        public string MsgId { get; set; } = string.Empty;
        public int? CandVendId { get; set; }
        public string? CandVendName { get; set; }
        public string? MsgContext { get; set; }
    }
    public class ReqStatus
    {
        public int ReqNo { get; set; }
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
        public string InvcNbr { get; set; } = string.Empty;
        public string QName { get; set; } = string.Empty;
        public string QStatus { get; set; } = string.Empty;
    }

    // One row per (vendor, doc type) this sender has previously sent for -- a sender linked to more than
    // one vendor has multiple rows with different VendId. Distinct by VendId/VendName for the linked-vendor set.
    public class SenderHistory
    {
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
        public int? DocTypeId { get; set; }
        public int NumDocs { get; set; }
    }
    public class VendorMatch
    {
        public int VendId { get; set; }
        public string VendName { get; set; } = string.Empty;
    }
    public class CmdResult
    {
        public string? CmdCode { get; set; }
        public string? VendorName { get; set; }
    }

    // Full classification output -- also the persisted shape of msg.TblMessages.MsgContext (opaque json,
    // msg/ai schemas never interpret it) so a held message's original classification survives to approval
    // without being reclassified.
    public class TriageResult
    {
        public string? MsgTpCode { get; set; }
        public string? InvcNbr { get; set; }
        public int? VendId { get; set; }
        public string? VendName { get; set; }
        public List<AttachmentLabel> Attachments { get; set; } = [];
    }
    // ExtractedFields is only ever populated for a "Processing" attachment on a known sender's Submission --
    // that's the one case triage already has the document image open (see TriageService), so extraction
    // happens there instead of Filer reading the same file again later. DocTpId, however, is now set for
    // Supporting attachments too (typed, not extracted) so ap.sprTblGetTasks can recognize the requirement
    // as satisfied instead of everything landing under the generic "Misc" doc type.
    public class AttachmentLabel
    {
        public string FileName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public int? DocTpId { get; set; }
        public Dictionary<string, object>? ExtractedFields { get; set; }
    }

    // Attachment content actually loaded for a Claude call -- replaces the old filenames-only, content-blind
    // approach for classification now that vendor identification may need to read the invoice image itself.
    public record AttachmentContent(string FileName, byte[] Bytes, string MediaType);

    public class EmailReply { public string Body { get; set; } = string.Empty; }

    // Result of /ap/pkg/tasks -- one row per outstanding (or already-satisfied) attachment requirement for
    // a package, per its invoice type (ap.LstInvcTypeDocTypes). Same shape DMS's TaskPane consumes; Mail
    // calls the same handler with a list of PkgNo (one submission can create more than one package) so the
    // gap-check for a whole submission is one round trip, not one per package.
    public class PkgTask
    {
        public int PkgNo { get; set; }
        public int DocTypeId { get; set; }
        public string Task { get; set; } = string.Empty;
        public bool IsComplete { get; set; }
        public bool IsOptional { get; set; }
    }

    // Result of /msg/mail/rfibridge -- resolves a vendor's reply back to the open msg.TblInfoRequests row
    // it's answering, via In-Reply-To/References matched against SentMsgId. SentTo is returned so the
    // caller can confirm the reply's sender matches who the RFI was actually sent to -- sufficient
    // authorization for this one thread even for a sender with no other approval on file (e.g. a
    // freight forwarder), without needing a static whitelist for every ad hoc external party.
    public class RfiBridgeResult
    {
        public int IReqNo { get; set; }
        public int PkgNo { get; set; }
        public int? MsgNo { get; set; }
        public int ConvNo { get; set; }
        public string SentTo { get; set; } = string.Empty;
    }

    // Payload for /msg/req/open -- ReqUId null means system-generated (the only origin EMF.Mail creates
    // today; NotNo/user-initiated-via-PkgNotify origin is deferred, see ProjectContext).
    public class InfoRequest
    {
        public int PkgNo { get; set; }
        public int? MsgNo { get; set; }
        public int? NotNo { get; set; }
        public string SentMsgId { get; set; } = string.Empty;
        public string SentTo { get; set; } = string.Empty;
        public int? ReqUId { get; set; }
        public int ConvNo { get; set; }
    }
    public class InfoRequestOpenResult
    {
        public int IReqNo { get; set; }
    }
    // Payload for /filer/msg/finalize -- single write-back point for a message's terminal state. Null
    // fields leave the corresponding column untouched (see the proc). IReqNo absorbs what used to be a
    // separate /filer/msg/linkreply call -- only ever set on the RFI-reply finalize.
    public class MessageFinalize
    {
        public int MsgNo { get; set; }
        public TriageResult? MsgContext { get; set; }
        public string? MsgTpCode { get; set; }
        public bool? IsHeld { get; set; }
        public string? FwdMsgId { get; set; }
        public int? IReqNo { get; set; }
        public string ResTpCode { get; set; } = string.Empty;
        public string? MsgResult { get; set; }
    }

    // Payload for /filer/msg/resolvecmd -- releases a held sender/vendor batch and logs the admin's
    // APPROVE/REJECT command against msg.TblCommands in one transactional call.
    public class CommandResolve
    {
        public int SenderId { get; set; }
        public int VendId { get; set; }
        public int AnchorMsgNo { get; set; }
        public bool IsApproved { get; set; }
        public string CmdCode { get; set; } = string.Empty;
        public int AdminMsgNo { get; set; }
        public string? Reference { get; set; }
        public int ResultCode { get; set; }
        public string? ResultMsg { get; set; }
    }
    // Payload for /filer/msg/lastpoll -- LastPollDT stays as an audit "last run" timestamp; LastMsgLink is
    // the actual delta bookmark now driving what GetChangedMessagesAsync fetches next round.
    public class AccountPoll
    {
        public int AcctId { get; set; }
        public DateTime LastPollDT { get; set; }
        public string? LastMsgLink { get; set; }
    }
}
